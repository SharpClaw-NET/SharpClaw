using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LLama;
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
public sealed class LocalInferenceApiClient(
    LocalInferenceProcessManager modelManager) : IProviderApiClient
{
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
    /// The model ID currently targeted. Set by the factory before each call.
    /// </summary>
    internal Guid CurrentModelId { get; set; }

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
        var executor = new StatelessExecutor(loaded.Weights, loaded.Params);

        var history = BuildChatHistory(systemPrompt, messages);
        var prompt = ApplyTemplate(loaded.Weights, history);
        var antiPrompts = BuildAntiPrompts(loaded.Weights);

        using var plainPipeline = BuildSamplingPipeline(completionParameters);

        var inferParams = new InferenceParams
        {
            MaxTokens = maxCompletionTokens ?? 4096,
            AntiPrompts = antiPrompts,
            SamplingPipeline = plainPipeline,
        };

        var sb = new StringBuilder();
        var completionTokens = 0;
        await foreach (var token in executor.InferAsync(prompt, inferParams, ct))
        {
            sb.Append(token);
            completionTokens++;
        }

        var content = StripStopTokens(sb.ToString(), antiPrompts).TrimEnd();
        LogResponse(content);
        return new ChatCompletionResult
        {
            Content = content,
            Usage = BuildUsage(loaded, prompt, completionTokens),
        };
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

        using (loaded.CreateContext()) { }
        var executor = new StatelessExecutor(loaded.Weights, loaded.Params);

        var history = LlamaSharpToolPromptBuilder.Build(systemPrompt, messages, tools);
        var prompt = ApplyTemplate(loaded.Weights, history);
        var antiPrompts = BuildAntiPrompts(loaded.Weights);

        using var pipeline = BuildSamplingPipeline(
            completionParameters,
            new Grammar(LlamaSharpToolGrammar.Build(), "root"));

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
            ToolCalls = envelope.ToolCalls,
            Usage = BuildUsage(loaded, prompt, completionTokens),
        };
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
        var executor = new StatelessExecutor(loaded.Weights, loaded.Params);

        var history = LlamaSharpToolPromptBuilder.Build(systemPrompt, messages, tools);
        var prompt = ApplyTemplate(loaded.Weights, history);
        var antiPrompts = BuildAntiPrompts(loaded.Weights);

        using var pipeline = BuildSamplingPipeline(
            completionParameters,
            new Grammar(LlamaSharpToolGrammar.Build(), "root"));

        var inferParams = new InferenceParams
        {
            MaxTokens = maxCompletionTokens ?? 4096,
            AntiPrompts = antiPrompts,
            SamplingPipeline = pipeline,
        };

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

        var completionTokens = 0;
        await foreach (var token in executor.InferAsync(prompt, inferParams, ct))
        {
            fullBuffer.Append(token);
            completionTokens++;

            // Once mode is known and it's tool_calls, just accumulate.
            if (isToolCallsMode == true)
                continue;

            holdback.Append(token);
            var text = holdback.ToString();

            // Detect mode field as soon as enough tokens have arrived.
            if (isToolCallsMode is null)
            {
                if (text.Contains("\"tool_calls\"", StringComparison.Ordinal))
                {
                    isToolCallsMode = true;
                    holdback.Clear();
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

        var result = ParseEnvelope(raw);
        yield return ChatStreamChunk.Final(new ChatCompletionResult
        {
            Content = result.Content,
            ToolCalls = result.ToolCalls,
            Usage = BuildUsage(loaded, prompt, completionTokens),
        });
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

        // Pick the last message that carries an image.
        var imageMsg = messages.LastOrDefault(m => m.HasImage);
        var imageBytes = imageMsg?.ImageBase64 is { } b64
            ? Convert.FromBase64String(b64)
            : null;

        // Stage image into the MTMD context before inference.
        if (imageBytes is not null)
            clipModel.LoadMedia(imageBytes);

        try
        {
            using var ctx = loaded.CreateContext();
            var executor = new InteractiveExecutor(ctx, clipModel, null);

            var history = LlamaSharpToolPromptBuilder.Build(systemPrompt, messages, tools);
            var prompt = ApplyTemplate(loaded.Weights, history);
            var antiPrompts = BuildAntiPrompts(loaded.Weights);

            using var pipeline = BuildSamplingPipeline(
                completionParameters,
                new Grammar(LlamaSharpToolGrammar.Build(), "root"));

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
                ToolCalls = envelope.ToolCalls,
                Usage = BuildUsage(loaded, prompt, completionTokens),
            };
        }
        finally
        {
            if (imageBytes is not null)
                clipModel.ClearMedia();
        }
    }

    // ── Helpers ────────────────────────────────────────────────────

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
        modelManager.GetLoaded(CurrentModelId)
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
    /// stop sequences. The executor's built-in EOS detection is the
    /// primary stop mechanism; these are a text-level safety net.
    /// </summary>
    private static IReadOnlyList<string> BuildAntiPrompts(LLamaWeights weights)
    {
        var set = new HashSet<string>(CommonStopSequences);

        try
        {
            var vocab = weights.NativeHandle.Vocab;
            AddTokenText(vocab, vocab.EOS, set);
            AddTokenText(vocab, vocab.EOT, set);
        }
        catch { /* Vocabulary access not available */ }

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
        catch { /* Token conversion failed */ }
    }

    // ── Sampling pipeline ─────────────────────────────────────────

    // LLamaSharp / llama.cpp default values for the sampling knobs we map.
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
            promptTokens = loaded.Weights
                .Tokenize(prompt, add_bos: false, special: true, Encoding.UTF8)
                .Length;
        }
        catch
        {
            // Tokenizer call unavailable (e.g. model handle closed); fall back to
            // a rough character-based estimate so Usage is never null.
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

            var mode = root.TryGetProperty("mode", out var modeEl)
                ? modeEl.GetString() ?? "message"
                : "message";

            var text = root.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
                ? textEl.GetString() ?? string.Empty
                : string.Empty;

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
            Debug.WriteLine(
                $"[WARN] Envelope parse failed — grammar sampler may have been defeated. Input: {json[..Math.Min(json.Length, 200)]} — {ex.Message}",
                "SharpClaw.CLI");
            return new ChatCompletionResult
            {
                Content = "(Local model returned malformed output. " +
                          "The quantization level may be too aggressive for reliable tool calling. " +
                          "Try a higher-bit-depth variant of this model.)"
            };
        }
    }
}
