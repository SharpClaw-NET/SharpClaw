using SharpClaw.Providers.Common;
using SharpClaw.Contracts.Models;

namespace SharpClaw.Tests.Providers;

/// <summary>
/// Per-provider tests for the model-name capability resolvers introduced
/// in Phase 4. Each <c>For*</c> helper is the name-shape contract the
/// matching <see cref="IProviderPlugin"/> uses when it auto-classifies
/// freshly-discovered models. The generic resolver mirrors the behaviour
/// of the legacy <c>ProviderService.InferCapabilitiesAndTags</c> ladder
/// and serves as the fallback for gateway providers and open endpoints.
/// </summary>
[TestFixture]
public class CapabilityResolverTests
{
    private const string Chat      = WellKnownCapabilityKeys.Chat;
    private const string Vision    = WellKnownCapabilityKeys.Vision;
    private const string Embedding = WellKnownCapabilityKeys.Embedding;
    private const string Tts       = WellKnownCapabilityKeys.Tts;
    private const string ImageGen  = WellKnownCapabilityKeys.ImageGeneration;

    [TestCase("gpt-4o",                 new[] { Chat, Vision })]
    [TestCase("gpt-4o-mini",            new[] { Chat, Vision })]
    [TestCase("gpt-4-turbo",            new[] { Chat, Vision })]
    [TestCase("gpt-5",                  new[] { Chat, Vision })]
    [TestCase("o1-mini",                new[] { Chat, Vision })]
    [TestCase("o3",                     new[] { Chat, Vision })]
    [TestCase("gpt-3.5-turbo",          new[] { Chat })]
    [TestCase("chatgpt-4o-latest",      new[] { Chat })]
    [TestCase("text-embedding-3-large", new[] { Embedding })]
    [TestCase("tts-1",                  new[] { Tts })]
    [TestCase("dall-e-3",               new[] { ImageGen })]
    [TestCase("gpt-image-1",            new[] { ImageGen })]
    [TestCase("sora-1",                 new[] { ImageGen })]
    [TestCase("text-moderation-latest", new string[0])]
    [TestCase("babbage-002",            new string[0])]
    [TestCase("davinci-002",            new string[0])]
    [TestCase("gpt-3.5-turbo-instruct", new string[0])]
    [TestCase("gpt-4o-audio-preview",   new[] { Chat, Tts })]
    [TestCase("gpt-4o-realtime-preview",new[] { Chat, Tts })]
    [TestCase("claude-3-5-sonnet",      new string[0])]
    public void OpenAIResolver(string model, string[] expected)
        => ProviderCapabilityHeuristics.ForOpenAI(model).Should().BeEquivalentTo(expected);

    [TestCase("claude-3-5-sonnet-20241022", new[] { Chat, Vision })]
    [TestCase("claude-3-haiku",             new[] { Chat, Vision })]
    [TestCase("claude-4-opus",              new[] { Chat, Vision })]
    [TestCase("claude-2.1",                 new[] { Chat })]
    [TestCase("claude-instant-1.2",         new[] { Chat })]
    [TestCase("gpt-4o",                     new string[0])]
    public void AnthropicResolver(string model, string[] expected)
        => ProviderCapabilityHeuristics.ForAnthropic(model).Should().BeEquivalentTo(expected);

    [TestCase("gemini-1.5-pro",     new[] { Chat, Vision })]
    [TestCase("gemini-2.0-flash",   new[] { Chat, Vision })]
    [TestCase("gemini-pro-vision",  new[] { Chat, Vision })]
    [TestCase("gemini-pro",         new[] { Chat })]
    [TestCase("text-embedding-004", new[] { Embedding })]
    [TestCase("claude-3-haiku",     new string[0])]
    public void GoogleResolver(string model, string[] expected)
        => ProviderCapabilityHeuristics.ForGoogle(model).Should().BeEquivalentTo(expected);

    [TestCase("mistral-large-latest", new[] { Chat })]
    [TestCase("mixtral-8x7b",         new[] { Chat })]
    [TestCase("codestral-latest",     new[] { Chat })]
    [TestCase("ministral-8b",         new[] { Chat })]
    [TestCase("pixtral-12b-2409",     new[] { Chat, Vision })]
    [TestCase("mistral-embed",        new[] { Embedding })]
    [TestCase("gpt-4o",               new string[0])]
    public void MistralResolver(string model, string[] expected)
        => ProviderCapabilityHeuristics.ForMistral(model).Should().BeEquivalentTo(expected);

    [TestCase("grok-2-vision-1212", new[] { Chat, Vision })]
    [TestCase("grok-3",             new[] { Chat, Vision })]
    [TestCase("grok-beta",          new[] { Chat })]
    [TestCase("gpt-4o",             new string[0])]
    public void XaiResolver(string model, string[] expected)
        => ProviderCapabilityHeuristics.ForXai(model).Should().BeEquivalentTo(expected);

    [TestCase("MiniMax-Text-01", new[] { Chat })]
    [TestCase("MiniMax-VL-01",   new[] { Chat, Vision })]
    [TestCase("abab6.5s-chat",   new[] { Chat })]
    [TestCase("gpt-4o",          new string[0])]
    public void MinimaxResolver(string model, string[] expected)
        => ProviderCapabilityHeuristics.ForMinimax(model).Should().BeEquivalentTo(expected);

    [TestCase("deepseek-v4-flash",  new[] { Chat })]
    [TestCase("deepseek-v4-pro",    new[] { Chat })]
    [TestCase("deepseek-chat",      new[] { Chat })]
    [TestCase("deepseek-reasoner",  new[] { Chat })]
    [TestCase("gpt-4o",             new string[0])]
    public void DeepSeekResolver(string model, string[] expected)
        => ProviderCapabilityHeuristics.ForDeepSeek(model).Should().BeEquivalentTo(expected);

    [TestCase("gpt-4o",                                   new[] { Chat, Vision })]
    [TestCase("claude-3-5-sonnet",                        new[] { Chat, Vision })]
    [TestCase("gemini-1.5-pro",                           new[] { Chat, Vision })]
    [TestCase("meta-llama/llama-3.2-90b-vision-instruct", new string[0])]
    [TestCase("llama-3.2-90b-vision",                     new[] { Chat, Vision })]
    [TestCase("llama-4-maverick",                         new[] { Chat, Vision })]
    [TestCase("llama-3.1-70b",                            new[] { Chat })]
    [TestCase("deepseek-v3",                              new[] { Chat })]
    [TestCase("deepseek-v4-flash",                        new[] { Chat })]
    [TestCase("qwen-2.5-72b",                             new[] { Chat })]
    [TestCase("phi-4",                                    new[] { Chat })]
    [TestCase("command-r-plus",                           new[] { Chat })]
    [TestCase("yi-lightning",                             new[] { Chat })]
    [TestCase("jamba-1.5-large",                          new[] { Chat })]
    [TestCase("text-embedding-3-large",                   new[] { Embedding })]
    [TestCase("dall-e-3",                                 new[] { ImageGen })]
    [TestCase("babbage-002",                              new string[0])]
    [TestCase("totally-unknown-model",                    new string[0])]
    public void GenericResolver(string model, string[] expected)
        => ProviderCapabilityHeuristics.ForGeneric(model).Should().BeEquivalentTo(expected);

    [Test]
    public void HeuristicCapabilityResolver_DelegatesToFunc()
    {
        var resolver = new HeuristicCapabilityResolver(ProviderCapabilityHeuristics.ForOpenAI);
        resolver.Resolve("gpt-4o").Should().BeEquivalentTo([Chat, Vision]);
    }
}
