using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LLama;
using LLama.Abstractions;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using LLama.Transformers;
using SharpClaw.Application.Core.LocalInference;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Core.Clients;

/// <summary>
/// Provider client that runs inference in-process via LLamaSharp.
/// The <see cref="LocalInferenceProcessManager"/> holds loaded model
/// weights; this client creates a context per-request and runs the
/// executor directly — no HTTP, no external server process.
/// <para>
/// Uses the model's embedded GGUF chat template via
/// <see cref="PromptTemplateTransformer"/> to format prompts correctly
/// for any model family (ChatML, Llama, Mistral, Phi, Gemma, etc.)
/// instead of hardcoding a single template format.
/// </para>
/// </summary>
public sealed class LocalInferenceApiClient : IProviderApiClient
{
    private readonly LocalInferenceProcessManager _modelManager;

    public LocalInferenceApiClient(LocalInferenceProcessManager modelManager)
    {
        _modelManager = modelManager;
        // L-007 — drop the probe cache for a model when it unloads so
        // the next request against a freshly reloaded instance runs a
        // real VRAM probe instead of trusting a stale success entry.
        _modelManager.ModelUnloaded += id =>
        {
            _lastProbeSuccess.TryRemove(id, out _);
        };
    }
    /// <summary>
    /// Common text-based stop sequences across model families.
    /// These act as a safety net alongside the model's native EOS token
    /// (which the executor handles at the token level automatically).
    /// </summary>
    private static readonly string[] CommonStopSequences =
    [
        "<|im_end|>",              // ChatML (Qwen, etc.)
        "<|im_start|>",            // ChatML turn boundary
        "<|eot_id|>",              // Llama 3+
        "<|end_of_text|>",         // Llama 3+
        "</s>",                    // Mistral, Llama 2, others
        "[/INST]",                 // Mistral instruct
        "<|end|>",                 // Phi
        "<end_of_turn>",           // Gemma
        "<|endoftext|>",           // Qwen / GPT-NeoX
        "<｜end▁of▁sentence｜>",   // DeepSeek
    ];

    /// <summary>
    /// The model ID currently targeted. Set by the caller (typically
    /// <c>ChatService</c>) before each provider invocation.
    /// <para>
    /// Backed by an <see cref="AsyncLocal{T}"/> so that concurrent chat
    /// requests running on the same singleton instance do not overwrite
    /// each other's model ID. Each logical async flow carries its own
    /// value: setting <c>CurrentModelId = …</c> on the synchronous
    /// portion before awaiting a completion call captures the value into
    /// the async state machine's execution context; sibling tasks on
    /// other models see their own value.
    /// </para>
    /// <para>
    /// The plain field-backed design was unsound because
    /// <see cref="LocalInferenceApiClient"/> is registered as a singleton
    /// in DI (see <c>Program.cs</c>) and two concurrent requests for
    /// different local models would race on a single mutable field — a
    /// request could run inference against the wrong model's weights and
    /// report its usage to the wrong model ID. See
    /// <c>docs/internal/llamasharp-pipeline-audit-and-remediation-plan.md</c>
    /// finding <c>L-001</c>.
    /// </para>
    /// </summary>
    internal Guid CurrentModelId
    {
        get => _currentModelId.Value;
        set => _currentModelId.Value = value;
    }

    private static readonly AsyncLocal<Guid> _currentModelId = new();

    // Cache of the last successful VRAM probe per model ID.
    // A probe is skipped when the last successful one is within ProbeTtl,
    // saving an allocate-and-free round-trip on every sequential call.
    // ConcurrentDictionary because multiple concurrent requests against the
    // same model can run simultaneously; static so it survives across calls.
    private static readonly TimeSpan ProbeTtl = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<Guid, DateTime> _lastProbeSuccess = new();

    public ProviderType ProviderType => ProviderType.LlamaSharp;

    /// <summary>
    /// Always <see langword="true"/>: grammar-constrained inference is the only
    /// supported mode. The legacy text-scanning path has been removed.
    /// </summary>
    public bool SupportsNativeToolCalling => true;

    public Task<IReadOnlyList<string>> ListModelIdsAsync(
        HttpClient httpClient, string apiKey, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>([]);

    public async Task<ChatCompletionResult> ChatCompletionAsync(
        HttpClient httpClient, string apiKey, string model, string? systemPrompt,
        IReadOnlyList<ChatCompletionMessage> messages, int? maxCompletionTokens = null,
        Dictionary<string, JsonElement>? providerParameters = null,
        CompletionParameters? completionParameters = null,
        CancellationToken ct = default)
    {
        var loaded = GetLoadedOrThrow();

        EnsureContextAllocatable(loaded, CurrentModelId);

        var history = BuildChatHistory(systemPrompt, messages);
        var prompt = ApplyTemplate(loaded.Weights, history);
        var antiPrompts = BuildAntiPrompts(loaded.Weights, completionParameters?.Stop);

        var responseFormatGrammar = ResolveResponseFormatGrammar(completionParameters);
        using var plainPipeline = BuildSamplingPipeline(completionParameters, responseFormatGrammar);

        var inferParams = new InferenceParams
        {
            MaxTokens = maxCompletionTokens ?? 4096,
            AntiPrompts = antiPrompts,
            SamplingPipeline = plainPipeline,
        };

        // Item #3 — KV cache reuse when the caller supplies a ThreadId.
        // Text-only, non-tool, non-streaming path. Tool paths below use
        // the same lease helper; multimodal is excluded because
        // MtmdWeights staging + the per-model mmLock already imply a
        // dedicated InteractiveExecutor lifecycle.
        var lease = await AcquireExecutorLeaseAsync(
            loaded, completionParameters?.ThreadId, prompt, ct);
        try
        {
            var sb = new StringBuilder();
            var completionTokens = 0;
            await foreach (var token in lease.Executor.InferAsync(lease.FeedText, inferParams, ct))
            {
                sb.Append(token);
                completionTokens++;
            }

            var raw = sb.ToString();
            var content = StripStopTokens(raw, antiPrompts).TrimEnd();
            LogResponse(content);
            lease.Commit(raw);
            return new ChatCompletionResult
            {
                Content = content,
                Usage = BuildUsage(loaded, prompt, completionTokens),
                FinishReason = InferLocalFinishReason(completionTokens, maxCompletionTokens, hasToolCalls: false),
            };
        }
        catch
        {
            lease.Invalidate();
            throw;
        }
        finally
        {
            lease.Release();
        }
    }

    // ── Session lease (item #3 — KV cache reuse) ──────────────────

    /// <summary>
    /// Wraps the executor used for a single inference turn. When
    /// <paramref name="threadId"/> is null this returns a one-shot
    /// <see cref="StatelessExecutor"/> and <see cref="FeedText"/> equal to
    /// the full prompt. When a thread ID is supplied the lease wraps a
    /// cached <see cref="InteractiveExecutor"/>; on prefix-match only the
    /// suffix is fed, otherwise the session is rebuilt.
    /// </summary>
    private readonly struct ExecutorLease
    {
        public ILLamaExecutor Executor { get; }
        public string FeedText { get; }
        private readonly string _fullPrompt;
        private readonly LocalInferenceProcessManager.CachedSession? _session;
        private readonly LocalInferenceProcessManager? _manager;
        private readonly Guid _modelId;
        private readonly Guid _threadId;

        internal ExecutorLease(ILLamaExecutor executor, string feedText, string fullPrompt)
        {
            Executor = executor;
            FeedText = feedText;
            _fullPrompt = fullPrompt;
            _session = null;
            _manager = null;
            _modelId = Guid.Empty;
            _threadId = Guid.Empty;
        }

        internal ExecutorLease(
            LocalInferenceProcessManager.CachedSession session,
            string feedText,
            string fullPrompt,
            LocalInferenceProcessManager manager,
            Guid modelId,
            Guid threadId)
        {
            Executor = session.Executor;
            FeedText = feedText;
            _fullPrompt = fullPrompt;
            _session = session;
            _manager = manager;
            _modelId = modelId;
            _threadId = threadId;
        }

        public void Commit(string rawOutput)
        {
            if (_session is null) return;
            _session.AccumulatedPrompt = _fullPrompt + rawOutput;
            _session.LastUsedUtc = DateTime.UtcNow;
        }

        public void Invalidate()
        {
            if (_manager is null) return;
            _manager.InvalidateSession(_modelId, _threadId);
        }

        public void Release()
        {
            if (_manager is null || _session is null) return;
            _manager.ReleaseSession(_session);
        }
    }

    private async Task<ExecutorLease> AcquireExecutorLeaseAsync(
        LocalInferenceProcessManager.LoadedModel loaded,
        Guid? threadId,
        string prompt,
        CancellationToken ct)
    {
        if (threadId is not Guid tid || tid == Guid.Empty)
        {
            var exec = new StatelessExecutor(loaded.Weights, loaded.Params);
            return new ExecutorLease(exec, prompt, prompt);
        }

        var session = await _modelManager.AcquireSessionAsync(CurrentModelId, tid, loaded, ct);
        string feed;
        if (!string.IsNullOrEmpty(session.AccumulatedPrompt)
            && prompt.StartsWith(session.AccumulatedPrompt, StringComparison.Ordinal))
        {
            feed = prompt[session.AccumulatedPrompt.Length..];
        }
        else
        {
            // Divergence: drop and rebuild. Release the gate before
            // disposing so Invalidate doesn't wait on ourselves.
            _modelManager.ReleaseSession(session);
            _modelManager.InvalidateSession(CurrentModelId, tid);
            session = await _modelManager.AcquireSessionAsync(CurrentModelId, tid, loaded, ct);
            feed = prompt;
        }

        return new ExecutorLease(session, feed, prompt, _modelManager, CurrentModelId, tid);
    }

    public async Task<ChatCompletionResult> ChatCompletionWithToolsAsync(
        HttpClient httpClient, string apiKey, string model, string? systemPrompt,
        IReadOnlyList<ToolAwareMessage> messages, IReadOnlyList<ChatToolDefinition> tools,
        int? maxCompletionTokens = null,
        Dictionary<string, JsonElement>? providerParameters = null,
        CompletionParameters? completionParameters = null,
        CancellationToken ct = default)
    {
        var loaded = GetLoadedOrThrow();

        if (loaded.ClipModel is not null && messages.Any(m => m.HasImage))
            return await ChatCompletionWithToolsMultimodalAsync(
                loaded, systemPrompt, messages, tools, maxCompletionTokens, completionParameters, ct);

        // L-008 — share the cached probe path with chat/non-tool and
        // streaming-tool variants. The previous unconditional
        // `using (loaded.CreateContext()) {}` paid full context
        // allocation per request even when a recent probe was on file.
        EnsureContextAllocatable(loaded, CurrentModelId);

        var strictTools = completionParameters?.StrictTools ?? true;
        var allowRefusal = ResolveAllowRefusal();

        var history = LlamaSharpToolPromptBuilder.Build(
            systemPrompt, messages, tools,
            imageCount: 0, strictTools: strictTools, allowRefusal: allowRefusal);
        var prompt = ApplyTemplate(loaded.Weights, history);
        var antiPrompts = BuildAntiPrompts(loaded.Weights, completionParameters?.Stop);

        WarnIfResponseFormatSupersededByToolGrammar(completionParameters);

        using var pipeline = BuildSamplingPipeline(
            completionParameters,
            new Grammar(ResolveToolGrammar(completionParameters, tools), "root"));

        var inferParams = new InferenceParams
        {
            MaxTokens = maxCompletionTokens ?? 4096,
            AntiPrompts = antiPrompts,
            SamplingPipeline = pipeline,
        };

        // Item #3 — reuse cached session when the caller supplies a
        // ThreadId. Tool-calling multi-turn is the primary use case.
        var lease = await AcquireExecutorLeaseAsync(
            loaded, completionParameters?.ThreadId, prompt, ct);
        try
        {
            var sb = new StringBuilder();
            var completionTokens = 0;
            await foreach (var token in lease.Executor.InferAsync(lease.FeedText, inferParams, ct))
            {
                sb.Append(token);
                completionTokens++;
            }

            var rawOutput = sb.ToString();
            var raw = StripStopTokens(rawOutput, antiPrompts).TrimEnd();
            LogResponse(raw);
            lease.Commit(rawOutput);

            var envelope = ParseEnvelope(raw);
            return new ChatCompletionResult
            {
                Content = envelope.Content,
                Refusal = envelope.Refusal,
                ToolCalls = envelope.ToolCalls,
                Usage = BuildUsage(loaded, prompt, completionTokens),
                FinishReason = envelope.Refusal is not null
                    ? FinishReason.ContentFilter
                    : InferLocalFinishReason(completionTokens, maxCompletionTokens, envelope.ToolCalls.Count > 0),
            };
        }
        catch
        {
            lease.Invalidate();
            throw;
        }
        finally
        {
            lease.Release();
        }
    }

    public async IAsyncEnumerable<ChatStreamChunk> StreamChatCompletionWithToolsAsync(
        HttpClient httpClient, string apiKey, string model, string? systemPrompt,
        IReadOnlyList<ToolAwareMessage> messages, IReadOnlyList<ChatToolDefinition> tools,
        int? maxCompletionTokens = null,
        Dictionary<string, JsonElement>? providerParameters = null,
        CompletionParameters? completionParameters = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var loaded = GetLoadedOrThrow();

        if (loaded.ClipModel is not null && messages.Any(m => m.HasImage))
        {
            // Multimodal streaming: delegate to non-streaming path, then yield
            // the single result as Final. InteractiveExecutor with MTMD doesn't
            // expose an IAsyncEnumerable — wrap it for interface compatibility.
            var multimodalResult = await ChatCompletionWithToolsMultimodalAsync(
                loaded, systemPrompt, messages, tools, maxCompletionTokens, completionParameters, ct);
            yield return ChatStreamChunk.Final(multimodalResult);
            yield break;
        }

        EnsureContextAllocatable(loaded, CurrentModelId);

        var strictTools = completionParameters?.StrictTools ?? true;
        var allowRefusal = ResolveAllowRefusal();

        var history = LlamaSharpToolPromptBuilder.Build(
            systemPrompt, messages, tools,
            imageCount: 0, strictTools: strictTools, allowRefusal: allowRefusal);
        var prompt = ApplyTemplate(loaded.Weights, history);
        var antiPrompts = BuildAntiPrompts(loaded.Weights, completionParameters?.Stop);

        WarnIfResponseFormatSupersededByToolGrammar(completionParameters);

        using var pipeline = BuildSamplingPipeline(
            completionParameters,
            new Grammar(ResolveToolGrammar(completionParameters, tools), "root"));

        var inferParams = new InferenceParams
        {
            MaxTokens = maxCompletionTokens ?? 4096,
            AntiPrompts = antiPrompts,
            SamplingPipeline = pipeline,
        };

        // Item #3 — session reuse for streaming tool calls. Tool
        // multi-turn is the primary hot path.
        var lease = await AcquireExecutorLeaseAsync(
            loaded, completionParameters?.ThreadId, prompt, ct);
        var committed = false;

        // Holdback buffer length
        // prefix needed to detect "mode":"tool_calls" in the envelope.
        // Once the mode field is confirmed we switch streaming phase.
        const string ToolCallsModeToken = "\"tool_calls\"";
        var maxApLen = 0;
        foreach (var ap in antiPrompts)
            if (ap.Length > maxApLen) maxApLen = ap.Length;
        var retainLen = Math.Max(maxApLen, ToolCallsModeToken.Length);

        // Phase detection: we read the mode field as tokens arrive.
        // Null  = not yet determined.
        // true  = "tool_calls" — buffer everything silently.
        // false = "message"    — stream text value incrementally.
        bool? isToolCallsMode = null;

        var holdback = new StringBuilder();
        var fullBuffer = new StringBuilder(); // complete raw envelope
        var textContent = new StringBuilder(); // streamed text (message mode)
        var walker = new EnvelopeStreamWalker();

        var completionTokens = 0;
        try
        {
        await foreach (var token in lease.Executor.InferAsync(lease.FeedText, inferParams, ct))
        {
            fullBuffer.Append(token);
            completionTokens++;

            // Once mode is known and it's tool_calls, feed the walker
            // so consumers receive incremental id/name/args deltas.
            if (isToolCallsMode == true)
            {
                foreach (var d in walker.Feed(token))
                    yield return ChatStreamChunk.ToolCall(d);
                continue;
            }

            holdback.Append(token);
            var text = holdback.ToString();

            // Detect mode field as soon as enough tokens have arrived.
            if (isToolCallsMode is null)
            {
                if (text.Contains("\"tool_calls\"", StringComparison.Ordinal))
                {
                    isToolCallsMode = true;
                    holdback.Clear();
                    // Replay everything buffered so far through the walker
                    // so it can lock onto the "calls":[ opener that may
                    // already be in-flight.
                    foreach (var d in walker.Feed(fullBuffer.ToString()))
                        yield return ChatStreamChunk.ToolCall(d);
                    continue;
                }

                if (text.Contains("\"message\"", StringComparison.Ordinal))
                {
                    isToolCallsMode = false;
                    // Fall through to normal text streaming below.
                }
                else
                {
                    // Mode not confirmed yet — hold back and wait.
                    continue;
                }
            }

            // isToolCallsMode == false: stream text content.
            // Check for anti-prompts first.
            bool hitStop = false;
            foreach (var ap in antiPrompts)
            {
                var apIdx = text.IndexOf(ap, StringComparison.Ordinal);
                if (apIdx >= 0)
                {
                    if (apIdx > 0)
                    {
                        var before = text[..apIdx];
                        textContent.Append(before);
                        yield return ChatStreamChunk.Text(before);
                    }
                    hitStop = true;
                    holdback.Clear();
                    break;
                }
            }

            if (hitStop)
                break;

            // Flush characters that cannot be the start of an anti-prompt.
            // In message mode the grammar only emits the text field value
            // after the "text": key, so we can stream most of it directly.
            var safeLen = text.Length - retainLen;
            if (safeLen > 0)
            {
                var safe = text[..safeLen];
                textContent.Append(safe);
                yield return ChatStreamChunk.Text(safe);
                holdback.Remove(0, safeLen);
            }
        }

        // Flush remaining holdback in message mode.
        if (isToolCallsMode == false && holdback.Length > 0)
        {
            var remaining = StripStopTokens(holdback.ToString(), antiPrompts);
            if (remaining.Length > 0)
            {
                textContent.Append(remaining);
                yield return ChatStreamChunk.Text(remaining);
            }
        }

        var raw = StripStopTokens(fullBuffer.ToString(), antiPrompts).TrimEnd();
        LogResponse(raw);

        lease.Commit(fullBuffer.ToString());
        committed = true;

        var result = ParseEnvelope(raw);
        yield return ChatStreamChunk.Final(new ChatCompletionResult
        {
            Content = result.Content,
            Refusal = result.Refusal,
            ToolCalls = result.ToolCalls,
            Usage = BuildUsage(loaded, prompt, completionTokens),
            FinishReason = result.Refusal is not null
                ? FinishReason.ContentFilter
                : InferLocalFinishReason(completionTokens, maxCompletionTokens, result.ToolCalls.Count > 0),
        });
        }
        finally
        {
            if (!committed)
                lease.Invalidate();
            lease.Release();
        }
    }

    // ── Multimodal inference (MTMD / LLaVA-style) ─────────────────

    /// <summary>
    /// Runs a single tool-calling turn through the multimodal path using
    /// <see cref="InteractiveExecutor"/> paired with the loaded
    /// <see cref="LocalInferenceProcessManager.LoadedModel.ClipModel"/>.
    /// <para>
    /// The most recent message carrying an image is used as the visual
    /// input. <see cref="MtmdWeights.LoadMedia(ReadOnlySpan{byte})"/> stages
    /// the image bytes before inference; <see cref="MtmdWeights.ClearMedia"/>
    /// cleans them up afterwards regardless of outcome.
    /// </para>
    /// </summary>
    private async Task<ChatCompletionResult> ChatCompletionWithToolsMultimodalAsync(
        LocalInferenceProcessManager.LoadedModel loaded,
        string? systemPrompt,
        IReadOnlyList<ToolAwareMessage> messages,
        IReadOnlyList<ChatToolDefinition> tools,
        int? maxCompletionTokens,
        CompletionParameters? completionParameters,
        CancellationToken ct)
    {
        var clipModel = loaded.ClipModel!;

        // L-005 — MtmdWeights.LoadMedia/ClearMedia mutate global state on
        // the native projector handle. Two concurrent multimodal requests
        // against the same model would clobber each other's staged image
        // and the second one's ClearMedia would drop data the first one
        // still needs. Serialise per model ID.
        var mmLock = _modelManager.GetMultimodalLock(CurrentModelId);
        await mmLock.WaitAsync(ct);
        try
        {
            // Phase 4 — stage every image that appears in the turn, up to the
            // configured cap (Local__MaxImagesPerTurn, default 8). MtmdWeights
            // supports additive staging: each LoadMedia call appends another
            // image to the projector context. ClearMedia wipes them all.
            var maxImages = ResolveMaxImagesPerTurn();
            var imagesStaged = 0;
            foreach (var msg in messages)
            {
                if (!msg.HasImage || msg.ImageBase64 is not { } b64)
                    continue;
                if (imagesStaged >= maxImages)
                    break;
                clipModel.LoadMedia(Convert.FromBase64String(b64));
                imagesStaged++;
            }

            try
            {
                using var ctx = loaded.CreateContext();
                var executor = new InteractiveExecutor(ctx, clipModel, null);

                var strictTools = completionParameters?.StrictTools ?? true;
                var allowRefusal = ResolveAllowRefusal();

                var history = LlamaSharpToolPromptBuilder.Build(
                    systemPrompt, messages, tools, imagesStaged,
                    strictTools: strictTools, allowRefusal: allowRefusal);
                var prompt = ApplyTemplate(loaded.Weights, history);
                var antiPrompts = BuildAntiPrompts(loaded.Weights, completionParameters?.Stop);

                WarnIfResponseFormatSupersededByToolGrammar(completionParameters);

                using var pipeline = BuildSamplingPipeline(
                    completionParameters,
                    new Grammar(ResolveToolGrammar(completionParameters, tools), "root"));

                var inferParams = new InferenceParams
                {
                    MaxTokens = maxCompletionTokens ?? 4096,
                    AntiPrompts = antiPrompts,
                    SamplingPipeline = pipeline,
                };

                var sb = new StringBuilder();
                var completionTokens = 0;
                await foreach (var token in executor.InferAsync(prompt, inferParams, ct))
                {
                    sb.Append(token);
                    completionTokens++;
                }

                var raw = StripStopTokens(sb.ToString(), antiPrompts).TrimEnd();
                LogResponse(raw);

                var envelope = ParseEnvelope(raw);
                return new ChatCompletionResult
                {
                    Content = envelope.Content,
                    Refusal = envelope.Refusal,
                    ToolCalls = envelope.ToolCalls,
                    Usage = BuildUsage(loaded, prompt, completionTokens),
                    FinishReason = envelope.Refusal is not null
                        ? FinishReason.ContentFilter
                        : InferLocalFinishReason(completionTokens, maxCompletionTokens, envelope.ToolCalls.Count > 0),
                };
            }
            finally
            {
                if (imagesStaged > 0)
                    clipModel.ClearMedia();
            }
        }
        finally
        {
            mmLock.Release();
        }
    }

    // ── Helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the per-turn multimodal image cap from the
    /// <c>Local__MaxImagesPerTurn</c> environment variable, falling back
    /// to <see cref="DefaultMaxImagesPerTurn"/>. Values less than 1 are
    /// treated as 1 (at least the last image staged).
    /// </summary>
    private static int ResolveMaxImagesPerTurn()
    {
        var raw = Environment.GetEnvironmentVariable("Local__MaxImagesPerTurn");
        if (int.TryParse(raw, out var n) && n > 0)
            return n;
        return DefaultMaxImagesPerTurn;
    }

    private const int DefaultMaxImagesPerTurn = 8;

    /// <summary>
    /// Maps local inference signals into a <see cref="FinishReason"/>.
    /// Tool calls always win; otherwise if we exhausted the caller's
    /// max-token budget we report <see cref="FinishReason.Length"/>;
    /// otherwise the envelope arrived naturally via the stop sampler,
    /// which we normalise as <see cref="FinishReason.Stop"/>.
    /// </summary>
    private static FinishReason InferLocalFinishReason(int completionTokens, int? maxCompletionTokens, bool hasToolCalls)
    {
        if (hasToolCalls) return FinishReason.ToolCalls;
        if (maxCompletionTokens is int cap && completionTokens >= cap)
            return FinishReason.Length;
        return FinishReason.Stop;
    }

    /// <summary>
    /// Validates that a KV-cache context can be allocated for <paramref name="loaded"/>
    /// (VRAM probe). The probe allocates a context then immediately frees it so
    /// the <see cref="StatelessExecutor"/> can allocate its own; without it the
    /// process crashes with an access violation when VRAM is insufficient.
    /// <para>
    /// The probe is skipped when the same model was successfully probed within
    /// <see cref="ProbeTtl"/> to avoid the allocation overhead on every sequential
    /// call in a conversation or agentic loop.
    /// </para>
    /// </summary>
    private static void EnsureContextAllocatable(
        LocalInferenceProcessManager.LoadedModel loaded, Guid modelId)
    {
        var now = DateTime.UtcNow;
        if (_lastProbeSuccess.TryGetValue(modelId, out var last)
            && (now - last) < ProbeTtl)
        {
            return;
        }

        using (loaded.CreateContext()) { }
        _lastProbeSuccess[modelId] = now;
    }

    private LocalInferenceProcessManager.LoadedModel GetLoadedOrThrow() =>
        _modelManager.GetLoaded(CurrentModelId)
        ?? throw new InvalidOperationException("Model not loaded.");

    // ── Chat history building ─────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="ChatHistory"/> from system prompt and simple messages.
    /// </summary>
    private static ChatHistory BuildChatHistory(
        string? systemPrompt, IReadOnlyList<ChatCompletionMessage> messages)
    {
        var history = new ChatHistory();

        if (!string.IsNullOrEmpty(systemPrompt))
            history.AddMessage(AuthorRole.System, systemPrompt);

        foreach (var msg in messages)
            history.AddMessage(MapRole(msg.Role), msg.Content);

        return history;
    }

    private static AuthorRole MapRole(string role) => role.ToLowerInvariant() switch
    {
        "system" => AuthorRole.System,
        "assistant" => AuthorRole.Assistant,
        _ => AuthorRole.User,
    };

    // ── Template application ──────────────────────────────────────

    /// <summary>
    /// Applies the model's embedded GGUF chat template to format the
    /// conversation correctly for the loaded model family. Delegates to
    /// <c>llama_chat_apply_template</c> inside llama.cpp via
    /// <see cref="PromptTemplateTransformer"/>.
    /// </summary>
    private static string ApplyTemplate(LLamaWeights weights, ChatHistory history)
    {
        var transformer = new PromptTemplateTransformer(weights, true);
        var prompt = transformer.HistoryToText(history);
        LogPrompt(prompt);
        return prompt;
    }

    [Conditional("DEBUG")]
    private static void LogPrompt(string prompt)
    {
        Debug.WriteLine("── Local inference prompt ──", "SharpClaw.CLI");
        Debug.WriteLine(prompt.Length > 2000
            ? $"{prompt[..1000]}\n  … [{prompt.Length - 2000} chars] …\n{prompt[^1000..]}"
            : prompt, "SharpClaw.CLI");
        Debug.WriteLine("── end prompt ──", "SharpClaw.CLI");
    }

    [Conditional("DEBUG")]
    private static void LogResponse(string response)
    {
        Debug.WriteLine("── Local inference response ──", "SharpClaw.CLI");
        Debug.WriteLine(response.Length > 2000
            ? $"{response[..1000]}\n  … [{response.Length - 2000} chars] …\n{response[^1000..]}"
            : response, "SharpClaw.CLI");
        Debug.WriteLine("── end response ──", "SharpClaw.CLI");
    }

    // ── Anti-prompt resolution ────────────────────────────────────

    /// <summary>
    /// Builds anti-prompts from the model's special tokens plus common
    /// stop sequences, merged with any caller-supplied <paramref name="additionalStops"/>
    /// (e.g. <see cref="CompletionParameters.Stop"/>). The executor's built-in
    /// EOS detection is the primary stop mechanism; these are a text-level
    /// safety net.
    /// </summary>
    private static IReadOnlyList<string> BuildAntiPrompts(
        LLamaWeights weights,
        IReadOnlyList<string>? additionalStops = null)
    {
        var set = new HashSet<string>(CommonStopSequences);

        try
        {
            var vocab = weights.NativeHandle.Vocab;
            AddTokenText(vocab, vocab.EOS, set);
            AddTokenText(vocab, vocab.EOT, set);
        }
        catch (Exception ex) when (ex is InvalidOperationException or AccessViolationException or NullReferenceException)
        {
            // L-019 — narrow the catch and log so we don't silently lose
            // anti-prompt safety on model variants whose vocab access
            // pattern changes between LLamaSharp versions.
            Debug.WriteLine($"BuildAntiPrompts: vocab access failed ({ex.GetType().Name}: {ex.Message})", "SharpClaw.CLI");
        }

        if (additionalStops is { Count: > 0 })
        {
            foreach (var s in additionalStops)
            {
                if (!string.IsNullOrWhiteSpace(s))
                    set.Add(s);
            }
        }

        set.RemoveWhere(string.IsNullOrWhiteSpace);
        return [.. set];
    }

    private static void AddTokenText(
        SafeLlamaModelHandle.Vocabulary vocab,
        LLamaToken? token,
        HashSet<string> dest)
    {
        try
        {
            var text = vocab.LLamaTokenToString(token, true);
            if (!string.IsNullOrWhiteSpace(text))
                dest.Add(text);
        }
        catch (Exception ex) when (ex is InvalidOperationException or AccessViolationException or ArgumentException)
        {
            // L-019 — narrowed catch so unrelated bugs surface.
            Debug.WriteLine($"AddTokenText: token decode failed ({ex.GetType().Name}: {ex.Message})", "SharpClaw.CLI");
        }
    }

    // ── Response format grammar resolution ────────────────────────

    /// <summary>
    /// Maps <see cref="CompletionParameters.ResponseFormat"/> to a GBNF
    /// <see cref="Grammar"/> when the caller requested a JSON-shaped
    /// response. Only the OpenAI-compatible <c>{"type": "json_object"}</c>
    /// form is recognised today; any other shape is logged and ignored.
    /// <para>
    /// Callers on tool-calling paths must <em>not</em> consult this helper:
    /// the tool envelope grammar (<see cref="LlamaSharpToolGrammar"/>) always
    /// takes precedence because it already produces JSON and additionally
    /// encodes the function-call contract.
    /// </para>
    /// </summary>
    private static Grammar? ResolveResponseFormatGrammar(CompletionParameters? p)
    {
        if (p?.ResponseFormat is not { } fmt)
            return null;

        if (fmt.ValueKind != JsonValueKind.Object)
            return null;

        if (!fmt.TryGetProperty("type", out var typeProp) ||
            typeProp.ValueKind != JsonValueKind.String)
            return null;

        var type = typeProp.GetString();
        if (string.Equals(type, "json_object", StringComparison.Ordinal))
        {
            Debug.WriteLine(
                "ResponseFormat=json_object → applying generic JSON grammar",
                "SharpClaw.CLI");
            return new Grammar(LlamaSharpJsonGrammars.JsonObject(), "root");
        }

        if (string.Equals(type, "json_schema", StringComparison.Ordinal))
        {
            return ResolveJsonSchemaGrammar(fmt);
        }

        // Any other shape silently falls back to unconstrained sampling.
        // The validator already rejects unsupported response-format shapes
        // before reaching this point for LlamaSharp.
        Debug.WriteLine(
            $"ResponseFormat type='{type}' is not supported by LlamaSharp; ignoring.",
            "SharpClaw.CLI");
        return null;
    }

    /// <summary>
    /// Handles the <c>json_schema</c> branch of
    /// <see cref="ResolveResponseFormatGrammar"/>. Extracts the nested
    /// schema defensively and delegates to
    /// <see cref="LlamaSharpJsonSchemaConverter.TryConvert"/>.
    /// <para>
    /// Contract: malformed payloads, schemas the converter cannot handle,
    /// and any unexpected converter failure all fall back to the generic
    /// JSON grammar — this helper must never throw. Callers on the
    /// tool-calling path do not reach this helper; the tool envelope
    /// grammar takes precedence.
    /// </para>
    /// </summary>
    private static Grammar ResolveJsonSchemaGrammar(JsonElement fmt)
    {
        // OpenAI's structured-output payload nests the schema under
        // `json_schema.schema`. Anything else (missing nest, wrong kind)
        // degrades to the generic JSON grammar rather than throwing.
        if (!fmt.TryGetProperty("json_schema", out var wrapper) ||
            wrapper.ValueKind != JsonValueKind.Object ||
            !wrapper.TryGetProperty("schema", out var schema) ||
            schema.ValueKind != JsonValueKind.Object)
        {
            Debug.WriteLine(
                "ResponseFormat=json_schema payload malformed (missing or non-object 'schema'); falling back to generic JSON grammar.",
                "SharpClaw.CLI");
            return new Grammar(LlamaSharpJsonGrammars.JsonObject(), "root");
        }

        try
        {
            if (LlamaSharpJsonSchemaConverter.TryConvert(schema, out var gbnf, out var unsupported))
            {
                if (unsupported.Count > 0)
                {
                    Debug.WriteLine(
                        $"ResponseFormat=json_schema converted with {unsupported.Count} degraded keyword(s): {string.Join(", ", unsupported)}",
                        "SharpClaw.CLI");
                }
                else
                {
                    Debug.WriteLine(
                        "ResponseFormat=json_schema → applying converted grammar",
                        "SharpClaw.CLI");
                }
                return new Grammar(gbnf, "root");
            }

            Debug.WriteLine(
                $"ResponseFormat=json_schema not convertible ({string.Join(", ", unsupported)}); falling back to generic JSON grammar.",
                "SharpClaw.CLI");
            return new Grammar(LlamaSharpJsonGrammars.JsonObject(), "root");
        }
        catch (Exception ex)
        {
            // The converter is contractually non-throwing, but we add a
            // belt-and-braces guard so a bug there cannot crash a live
            // completion stream. Any escape is treated as a fallback.
            Debug.WriteLine(
                $"ResponseFormat=json_schema converter threw unexpectedly ({ex.GetType().Name}: {ex.Message}); falling back to generic JSON grammar.",
                "SharpClaw.CLI");
            return new Grammar(LlamaSharpJsonGrammars.JsonObject(), "root");
        }
    }

    /// <summary>
    /// Emits a debug log when <see cref="CompletionParameters.ResponseFormat"/>
    /// is set on a tool-calling path. The tool envelope grammar already
    /// enforces JSON output, so the response-format hint is intentionally
    /// ignored — the message surfaces the precedence decision for operators.
    /// </summary>
    private static void WarnIfResponseFormatSupersededByToolGrammar(CompletionParameters? p)
    {
        if (p?.ResponseFormat is not null)
            Debug.WriteLine(
                "ResponseFormat ignored on tool-calling path — tool envelope grammar takes precedence.",
                "SharpClaw.CLI");
    }

    /// <summary>
    /// Compiles the tool-envelope GBNF grammar for this call, specialised
    /// by <see cref="CompletionParameters.ToolChoice"/>,
    /// <see cref="CompletionParameters.ParallelToolCalls"/>, and
    /// <see cref="CompletionParameters.StrictTools"/>. When strict mode
    /// is on and <paramref name="tools"/> is non-empty, the grammar
    /// enforces per-tool argument schemas derived from
    /// <see cref="ChatToolDefinition.ParametersSchema"/>.
    /// </summary>
    private static string ResolveToolGrammar(
        CompletionParameters? p,
        IReadOnlyList<ChatToolDefinition> tools)
    {
        var choice = p?.ToolChoice ?? ToolChoice.Auto;
        var parallel = p?.ParallelToolCalls ?? true;
        var strict = p?.StrictTools ?? true;
        var allowRefusal = ResolveAllowRefusal();
        return LlamaSharpToolGrammar.Build(choice, parallel, tools, strict, allowRefusal);
    }

    /// <summary>
    /// Resolves whether the envelope grammar should include the third
    /// <c>"refusal"</c> mode branch. Controlled by the
    /// <c>Local__AllowRefusal</c> environment variable (default off).
    /// When on, the parser routes <c>"refusal"</c> envelopes to
    /// <see cref="ChatCompletionResult.Refusal"/> with
    /// <see cref="FinishReason.ContentFilter"/>.
    /// </summary>
    private static bool ResolveAllowRefusal()
    {
        var raw = Environment.GetEnvironmentVariable("Local__AllowRefusal");
        return !string.IsNullOrWhiteSpace(raw)
            && (bool.TryParse(raw, out var b) && b
                || string.Equals(raw, "1", StringComparison.Ordinal));
    }

    // ── Sampling pipeline ─────────────────────────────────────────
    // Kept here as named constants so they are not scattered across initialisers.
    private const float DefaultTemperature    = 0.80f;
    private const float DefaultTopP           = 0.95f;
    private const int   DefaultTopK           = 40;
    private const float DefaultFreqPenalty    = 0.00f;
    private const float DefaultPresencePenalty = 0.00f;

    /// <summary>
    /// Builds a <see cref="DefaultSamplingPipeline"/> applying any sampling
    /// parameters from <paramref name="p"/>. When <paramref name="grammar"/> is
    /// provided the pipeline is grammar-constrained (tool-call paths); otherwise
    /// a plain sampling pipeline is returned. Caller owns and must dispose the result.
    /// <para>
    /// Because all <see cref="DefaultSamplingPipeline"/> properties are <c>init</c>-only,
    /// the full initialiser is written out each time with null-coalescing to
    /// llama.cpp defaults when a parameter is not set by the caller.
    /// </para>
    /// </summary>
    private static DefaultSamplingPipeline BuildSamplingPipeline(
        CompletionParameters? p, Grammar? grammar = null) =>
        new()
        {
            GrammarOptimization = DefaultSamplingPipeline.GrammarOptimizationMode.Extended,
            Grammar          = grammar,
            Temperature      = p?.Temperature      ?? DefaultTemperature,
            TopP             = p?.TopP             ?? DefaultTopP,
            TopK             = p?.TopK             ?? DefaultTopK,
            FrequencyPenalty = p?.FrequencyPenalty ?? DefaultFreqPenalty,
            PresencePenalty  = p?.PresencePenalty  ?? DefaultPresencePenalty,
            // Seed: 0 (default) asks llama.cpp to use a fresh random seed per call.
            // Any caller-supplied int is reinterpreted as an unsigned 32-bit value so
            // negative sentinels like -1 still map to a deterministic seed.
            Seed             = p?.Seed is { } s ? unchecked((uint)s) : 0u,
        };

    // ── Token usage ───────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="TokenUsage"/> for the completed inference call.
    /// Completion tokens are the exact count of tokens yielded by <c>InferAsync</c>.
    /// Prompt tokens are counted by running the model's own tokenizer over the
    /// formatted prompt — the same vocabulary used at inference time, so the
    /// count is exact.
    /// </summary>
    private static TokenUsage BuildUsage(
        LocalInferenceProcessManager.LoadedModel loaded,
        string prompt,
        int completionTokens)
    {
        int promptTokens;
        try
        {
            // L-013 — BOS-aware. Many model families (Llama 3, Mistral,
            // Qwen) prepend a BOS token at the chat-template boundary.
            // Suppressing it here under-counted prompt tokens by one,
            // which propagated into per-request usage and cost.
            // Detect from the model's own vocab whether a BOS is added.
            bool addBos = false;
            try { addBos = loaded.Weights.NativeHandle.Vocab.ShouldAddBOS; }
            catch (Exception ex) when (ex is InvalidOperationException or AccessViolationException)
            {
                // Vocab unavailable; default to false.
            }

            promptTokens = loaded.Weights
                .Tokenize(prompt, add_bos: addBos, special: true, Encoding.UTF8)
                .Length;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or AccessViolationException)
        {
            // Tokenizer call unavailable (e.g. model handle closed); fall back to
            // a rough character-based estimate so Usage is never null.
            Debug.WriteLine($"BuildUsage tokenize failed ({ex.GetType().Name}); using char/4 estimate.", "SharpClaw.CLI");
            promptTokens = prompt.Length / 4;
        }

        return new TokenUsage(promptTokens, completionTokens);
    }

    // ── Output cleaning ───────────────────────────────────────────

    /// <summary>
    /// Strips any stop-sequence text from the output that the executor
    /// may have yielded before recognising the full anti-prompt.
    /// </summary>
    private static string StripStopTokens(
        string text, IReadOnlyList<string> antiPrompts)
    {
        foreach (var ap in antiPrompts)
        {
            var idx = text.IndexOf(ap, StringComparison.Ordinal);
            if (idx >= 0)
                text = text[..idx];
        }
        return text;
    }

    // ── Envelope parsing ──────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Names that local models emit to signal "I don't want to call a tool."
    /// Treating these as real invocations would feed an error back to the model
    /// and trigger an infinite tool-call loop.
    /// </summary>
    private static readonly HashSet<string> NoOpToolNames =
        new(StringComparer.OrdinalIgnoreCase)
        { "none", "null", "no_tool", "noop", "no-op", "n/a", "" };

    /// <summary>
    /// Parses the grammar-constrained envelope JSON produced by the model.
    /// On parse failure — possible on heavily-quantised models that defeat the
    /// grammar sampler — returns an empty <see cref="ChatCompletionResult"/> and
    /// logs a warning.
    /// </summary>
    internal static ChatCompletionResult ParseEnvelope(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new ChatCompletionResult { Content = string.Empty };

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // L-018 — be tolerant of casing and surrounding whitespace.
            // The grammar emits canonical lowercase, but quantised models
            // occasionally produce "Tool_Calls" or " tool_calls ". Trim
            // and lower so the downstream comparison is robust without
            // changing the grammar contract.
            string mode = "message";
            if (root.TryGetProperty("mode", out var modeEl) && modeEl.ValueKind == JsonValueKind.String)
            {
                var raw = modeEl.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                    mode = raw.Trim().ToLowerInvariant();
            }

            var text = root.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
                ? textEl.GetString() ?? string.Empty
                : string.Empty;

            if (mode == "refusal")
            {
                // Refusal mode is semantically mutually exclusive with
                // Content/ToolCalls — text carries the model-provided
                // decline reason, which surfaces through Refusal so that
                // ChatService can map finish_reason to ContentFilter.
                return new ChatCompletionResult { Refusal = text };
            }

            if (mode != "tool_calls")
                return new ChatCompletionResult { Content = text };

            // tool_calls mode — parse the calls array.
            if (!root.TryGetProperty("calls", out var callsEl)
                || callsEl.ValueKind != JsonValueKind.Array)
                return new ChatCompletionResult { Content = text };

            var calls = new List<ChatToolCall>();
            foreach (var callEl in callsEl.EnumerateArray())
            {
                var name = callEl.TryGetProperty("name", out var nameEl)
                    ? nameEl.GetString() ?? string.Empty
                    : string.Empty;

                if (NoOpToolNames.Contains(name))
                    continue;

                var id = callEl.TryGetProperty("id", out var idEl)
                    ? idEl.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(id))
                    id = $"call_{Guid.NewGuid():N}";

                var args = callEl.TryGetProperty("args", out var argsEl)
                    ? argsEl.GetRawText()
                    : "{}";

                calls.Add(new ChatToolCall(id!, name, args));
            }

            return new ChatCompletionResult { Content = text, ToolCalls = calls };
        }
        catch (JsonException ex)
        {
            // L-017 — bubble the failure up as a typed exception so
            // ChatService can map it to an error response rather than
            // silently returning a fake apology that downstream tooling
            // would treat as a successful completion.
            var preview = json[..Math.Min(json.Length, 200)];
            Debug.WriteLine(
                $"[WARN] Envelope parse failed — grammar sampler may have been defeated. Input: {preview} — {ex.Message}",
                "SharpClaw.CLI");
            throw new LocalInferenceEnvelopeException(preview, ex);
        }
    }
}
