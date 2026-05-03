namespace SharpClaw.Providers.Common;

/// <summary>
/// Well-known string keys that identify built-in provider types.
/// Stored as-is in <c>ProviderDB.ProviderKey</c> and used throughout
/// Core to route to the correct API client.
/// </summary>
public static class WellKnownProviderKeys
{
    public const string OpenAI                  = "openai";
    public const string Anthropic               = "anthropic";
    public const string OpenRouter              = "openrouter";
    public const string GoogleVertexAI          = "google-vertex-ai";
    public const string GoogleGemini            = "google-gemini";
    public const string ZAI                     = "zai";
    public const string VercelAIGateway         = "vercel-ai-gateway";
    public const string XAI                     = "xai";
    public const string Groq                    = "groq";
    public const string Cerebras                = "cerebras";
    public const string Mistral                 = "mistral";
    public const string GitHubCopilot           = "github-copilot";
    public const string Custom                  = "custom";
    public const string LlamaSharp              = "llamasharp";
    public const string Minimax                 = "minimax";
    public const string GoogleGeminiOpenAi      = "google-gemini-openai";
    public const string GoogleVertexAIOpenAi    = "google-vertex-ai-openai";
    public const string Ollama                  = "ollama";

    /// <summary>
    /// All well-known provider keys, excluding <see cref="Custom"/>.
    /// Used for validation and enumeration when the full enum is unavailable.
    /// </summary>
    public static readonly IReadOnlyList<string> All =
    [
        OpenAI, Anthropic, OpenRouter, GoogleGemini, GoogleGeminiOpenAi,
        GoogleVertexAI, GoogleVertexAIOpenAi, ZAI, VercelAIGateway, XAI,
        Groq, Cerebras, Mistral, GitHubCopilot, Minimax, LlamaSharp, Ollama,
    ];
}
