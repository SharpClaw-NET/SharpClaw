using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LLama;
using LLama.Common;
using LLama.Native;
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
    private const string ToolCallMarker = "{\"tool_call\"";

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

    public ProviderType ProviderType => ProviderType.Local;
    public bool SupportsNativeToolCalling => true;

    public Task<IReadOnlyList<string>> ListModelIdsAsync(
        HttpClient httpClient, string apiKey, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>([]);

    public async Task<string> ChatCompletionAsync(
        HttpClient httpClient, string apiKey, string model, string? systemPrompt,
        IReadOnlyList<ChatCompletionMessage> messages, int? maxCompletionTokens = null,
        Dictionary<string, JsonElement>? providerParameters = null,
        CancellationToken ct = default)
    {
        var loaded = GetLoadedOrThrow();

        // Probe: validate that KV cache allocation succeeds, then free
        // the VRAM so the StatelessExecutor can allocate its own context.
        // Without this the process crashes with an access violation when
        // VRAM is insufficient.
        using (loaded.CreateContext()) { }
        var executor = new StatelessExecutor(loaded.Weights, loaded.Params);

        var history = BuildChatHistory(systemPrompt, messages);
        var prompt = ApplyTemplate(loaded.Weights, history);
        var antiPrompts = BuildAntiPrompts(loaded.Weights);

        var inferParams = new InferenceParams
        {
            MaxTokens = maxCompletionTokens ?? 4096,
            AntiPrompts = antiPrompts,
        };

        var sb = new StringBuilder();
        await foreach (var token in executor.InferAsync(prompt, inferParams, ct))
            sb.Append(token);

        var result = StripStopTokens(sb.ToString(), antiPrompts).TrimEnd();
        LogResponse(result);
        return result;
    }

    public async Task<ChatCompletionResult> ChatCompletionWithToolsAsync(
        HttpClient httpClient, string apiKey, string model, string? systemPrompt,
        IReadOnlyList<ToolAwareMessage> messages, IReadOnlyList<ChatToolDefinition> tools,
        int? maxCompletionTokens = null,
        Dictionary<string, JsonElement>? providerParameters = null,
        CancellationToken ct = default)
    {
        var loaded = GetLoadedOrThrow();

        using (loaded.CreateContext()) { }
        var executor = new StatelessExecutor(loaded.Weights, loaded.Params);

        var history = BuildToolChatHistory(systemPrompt, messages);
        var prompt = ApplyTemplate(loaded.Weights, history);
        var antiPrompts = BuildAntiPrompts(loaded.Weights);

        var inferParams = new InferenceParams
        {
            MaxTokens = maxCompletionTokens ?? 4096,
            AntiPrompts = antiPrompts,
        };

        var sb = new StringBuilder();
        await foreach (var token in executor.InferAsync(prompt, inferParams, ct))
            sb.Append(token);

        var raw = StripStopTokens(sb.ToString(), antiPrompts).TrimEnd();
        LogResponse(raw);
        var toolCalls = ParseToolCalls(raw);

        // Always strip tool-call JSON from content, even if all calls
        // were filtered (e.g. no-op name "none"). Without this, the
        // JSON blob would leak into the text content.
        var content = raw.Contains(ToolCallMarker, StringComparison.Ordinal)
            ? ContentBeforeFirstToolCall(raw)
            : raw;

        return new ChatCompletionResult
        {
            Content = content,
            ToolCalls = toolCalls
        };
    }

    public async IAsyncEnumerable<ChatStreamChunk> StreamChatCompletionWithToolsAsync(
        HttpClient httpClient, string apiKey, string model, string? systemPrompt,
        IReadOnlyList<ToolAwareMessage> messages, IReadOnlyList<ChatToolDefinition> tools,
        int? maxCompletionTokens = null,
        Dictionary<string, JsonElement>? providerParameters = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var loaded = GetLoadedOrThrow();

        using (loaded.CreateContext()) { }
        var executor = new StatelessExecutor(loaded.Weights, loaded.Params);

        var history = BuildToolChatHistory(systemPrompt, messages);
        var prompt = ApplyTemplate(loaded.Weights, history);
        var antiPrompts = BuildAntiPrompts(loaded.Weights);

        var inferParams = new InferenceParams
        {
            MaxTokens = maxCompletionTokens ?? 4096,
            AntiPrompts = antiPrompts,
        };

        // Compute retain length for the holdback buffer.
        // the longest anti-prompt and the tool-call marker so neither can
        // be partially flushed before detection.
        var maxApLen = 0;
        foreach (var ap in antiPrompts)
            if (ap.Length > maxApLen) maxApLen = ap.Length;
        var retainLen = Math.Max(maxApLen, ToolCallMarker.Length);

        // Buffer trailing text that could be the start of a stop sequence
        // or a tool-call marker so neither leaks into the stream.
        var holdback = new StringBuilder();
        var fullContent = new StringBuilder();
        var toolCallBuffer = new StringBuilder();
        bool toolCallDetected = false;

        await foreach (var token in executor.InferAsync(prompt, inferParams, ct))
        {
            // Phase 2: tool call detected — buffer silently, don't stream.
            if (toolCallDetected)
            {
                toolCallBuffer.Append(token);
                continue;
            }

            // Phase 1: normal streaming with holdback.
            holdback.Append(token);
            var text = holdback.ToString();

            // Check if buffer contains a complete anti-prompt.
            bool hitStop = false;
            foreach (var ap in antiPrompts)
            {
                var apIdx = text.IndexOf(ap, StringComparison.Ordinal);
                if (apIdx >= 0)
                {
                    if (apIdx > 0)
                    {
                        var before = text[..apIdx];
                        fullContent.Append(before);
                        yield return ChatStreamChunk.Text(before);
                    }
                    hitStop = true;
                    holdback.Clear();
                    break;
                }
            }

            if (hitStop)
                break;

            // Check if buffer contains a tool-call start. Once detected,
            // flush any preceding text and switch to silent buffering.
            var tcIdx = text.IndexOf(ToolCallMarker, StringComparison.Ordinal);
            if (tcIdx >= 0)
            {
                if (tcIdx > 0)
                {
                    var before = text[..tcIdx];
                    fullContent.Append(before);
                    yield return ChatStreamChunk.Text(before);
                }
                toolCallDetected = true;
                toolCallBuffer.Append(text[tcIdx..]);
                holdback.Clear();
                continue;
            }

            // Flush characters that cannot be the start of any anti-prompt
            // or tool-call marker.
            var safeLen = text.Length - retainLen;
            if (safeLen > 0)
            {
                var safe = text[..safeLen];
                fullContent.Append(safe);
                yield return ChatStreamChunk.Text(safe);
                holdback.Remove(0, safeLen);
            }
        }

        // Resolve remaining buffers.
        IReadOnlyList<ChatToolCall> toolCalls;
        if (toolCallDetected)
        {
            // Tool calls live in the silent buffer.
            toolCalls = ParseToolCalls(toolCallBuffer.ToString());
        }
        else if (holdback.Length > 0)
        {
            // No tool calls detected — flush leftover holdback.
            var remaining = StripStopTokens(holdback.ToString(), antiPrompts);

            // Check for tool calls that arrived entirely within holdback.
            toolCalls = ParseToolCalls(remaining);
            if (toolCalls.Count > 0)
            {
                remaining = ContentBeforeFirstToolCall(remaining);
            }

            if (remaining.Length > 0)
            {
                fullContent.Append(remaining);
                yield return ChatStreamChunk.Text(remaining);
            }
        }
        else
        {
            toolCalls = [];
        }

        var content = fullContent.ToString().TrimEnd();
        LogResponse(content);

        yield return ChatStreamChunk.Final(new ChatCompletionResult
        {
            Content = content,
            ToolCalls = toolCalls
        });
    }

    // ── Helpers ────────────────────────────────────────────────────

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

    /// <summary>
    /// Builds a <see cref="ChatHistory"/> from tool-aware messages,
    /// flattening tool calls/results into text for models that lack
    /// a native "tool" role in their chat template.
    /// </summary>
    private static ChatHistory BuildToolChatHistory(
        string? systemPrompt,
        IReadOnlyList<ToolAwareMessage> messages)
    {
        var history = new ChatHistory();

        if (!string.IsNullOrEmpty(systemPrompt))
            history.AddMessage(AuthorRole.System, systemPrompt);

        foreach (var msg in messages)
        {
            if (msg.Role == "tool")
            {
                // Map tool results to user messages — local models don't
                // have a native "tool" role in their chat template.
                // Format matches ChatService's text-based [TOOL_RESULT:] convention.
                history.AddMessage(AuthorRole.User,
                    $"[TOOL_RESULT:{msg.ToolCallId}] {msg.Content}");
            }
            else if (msg.Role == "assistant" && msg.ToolCalls is { Count: > 0 })
            {
                // Serialize previous tool calls in the same [TOOL_CALL:]
                // format that ChatService's text-based path uses.
                var content = msg.Content ?? "";
                foreach (var tc in msg.ToolCalls)
                {
                    content += $"\n[TOOL_CALL:{tc.Id}] {tc.ArgumentsJson}";
                }
                history.AddMessage(AuthorRole.Assistant, content);
            }
            else
            {
                history.AddMessage(
                    MapRole(msg.Role ?? "user"),
                    msg.Content ?? "");
            }
        }

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

    // ── Tool call parsing ─────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Names that local models emit to signal "I don't want to call a
    /// tool." Treating these as real invocations would feed an error
    /// back to the model and trigger an infinite tool-call loop.
    /// </summary>
    private static readonly HashSet<string> NoOpToolNames =
        new(StringComparer.OrdinalIgnoreCase)
        { "none", "null", "no_tool", "noop", "no-op", "n/a", "" };

    private static IReadOnlyList<ChatToolCall> ParseToolCalls(string content)
    {
        var calls = new List<ChatToolCall>();
        var remaining = content.AsSpan();

        while (true)
        {
            var idx = remaining.IndexOf("{\"tool_call\"");
            if (idx < 0) break;

            remaining = remaining[idx..];
            var json = ExtractJson(remaining);
            if (json is null) break;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("tool_call", out var tc))
                {
                    var name = tc.GetProperty("name").GetString() ?? "";

                    // Skip no-op names that local models generate to
                    // indicate "I have no tool to call."
                    if (!NoOpToolNames.Contains(name))
                    {
                        var id = tc.GetProperty("id").GetString()
                            ?? $"call_{Guid.NewGuid():N}";
                        var args = tc.TryGetProperty("arguments", out var a)
                            ? a.GetRawText() : "{}";
                        calls.Add(new ChatToolCall(id, name, args));
                    }
                }
            }
            catch (JsonException) { }

            remaining = remaining[json.Length..];
        }

        return calls;
    }

    private static string? ExtractJson(ReadOnlySpan<char> text)
    {
        if (text.Length == 0 || text[0] != '{') return null;

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (escaped) { escaped = false; continue; }
            if (c == '\\' && inString) { escaped = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                    return text[..(i + 1)].ToString();
            }
        }

        return null;
    }

    /// <summary>
    /// Returns only the text content that appears before the first
    /// tool-call JSON. Everything from the first <c>{"tool_call"</c>
    /// onward — including the JSON and any trailing explanation — is
    /// discarded because it is captured separately via
    /// <see cref="ParseToolCalls"/>.
    /// </summary>
    private static string ContentBeforeFirstToolCall(string content)
    {
        var idx = content.IndexOf(ToolCallMarker, StringComparison.Ordinal);
        return idx >= 0 ? content[..idx].TrimEnd() : content;
    }
}
