namespace SharpClaw.Contracts.Enums;

public enum ProviderType
{
    OpenAI = 0,
    Anthropic = 1,
    OpenRouter = 2,

    /// <summary>
    /// Native Google Vertex AI API (<c>generateContent</c> endpoint).
    /// <b>Not yet implemented</b> — use <see cref="GoogleVertexAIOpenAi"/>
    /// instead. This value reserves the enum slot for the future native
    /// Vertex AI protocol.
    /// </summary>
    GoogleVertexAI = 3,

    /// <summary>
    /// Native Google Gemini API (<c>generateContent</c> endpoint).
    /// Provider parameters follow the Gemini schema and are passed through as-is.
    /// </summary>
    GoogleGemini = 4,

    ZAI = 5,
    VercelAIGateway = 6,
    XAI = 7,
    Groq = 8,
    Cerebras = 9,
    Mistral = 10,
    GitHubCopilot = 11,
    Custom = 12,

    /// <summary>
    /// In-process LLM inference via LLamaSharp (llama.cpp).
    /// </summary>
    LlamaSharp = 13,

    Minimax = 14,

    /// <summary>
    /// Google Gemini via the OpenAI-compatible endpoint
    /// (<c>/v1beta/openai/chat/completions</c>). Provider parameters
    /// follow the OpenAI schema.
    /// </summary>
    GoogleGeminiOpenAi = 15,

    /// <summary>
    /// Google Vertex AI via the OpenAI-compatible endpoint
    /// (<c>/v1beta1/openai/chat/completions</c>). Provider parameters
    /// follow the OpenAI schema.
    /// </summary>
    GoogleVertexAIOpenAi = 16,

    /// <summary>
    /// In-process speech-to-text transcription via Whisper.net.
    /// </summary>
    Whisper = 17,

    /// <summary>
    /// User-managed Ollama server (OpenAI-compatible HTTP API).
    /// Default endpoint: <c>http://localhost:11434</c>.
    /// </summary>
    Ollama = 18
}
