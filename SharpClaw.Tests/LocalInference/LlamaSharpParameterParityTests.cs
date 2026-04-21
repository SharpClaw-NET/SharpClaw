using System.Text.Json;
using SharpClaw.Application.Core.Clients;
using SharpClaw.Application.Core.LocalInference;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Tests.LocalInference;

// ═══════════════════════════════════════════════════════════════════════
// Phase 1 — seed + stop parity
// ═══════════════════════════════════════════════════════════════════════

[TestFixture]
public class LlamaSharpValidatorStopAndSeedTests
{
    [Test]
    public void Validate_StopAtMax_16_Passes()
    {
        var parameters = new CompletionParameters
        {
            Stop = Enumerable.Range(0, 16).Select(i => $"STOP_{i}").ToArray(),
        };

        var errors = CompletionParameterValidator.Validate(
            parameters, ProviderType.LlamaSharp);

        errors.Should().BeEmpty();
    }

    [Test]
    public void Validate_StopAt_17_Rejected()
    {
        var parameters = new CompletionParameters
        {
            Stop = Enumerable.Range(0, 17).Select(i => $"STOP_{i}").ToArray(),
        };

        var errors = CompletionParameterValidator.Validate(
            parameters, ProviderType.LlamaSharp);

        errors.Should().ContainSingle()
            .Which.Should().Contain("Too many stop sequences")
            .And.Contain("17")
            .And.Contain("16");
    }

    [Test]
    public void Validate_StopSingle_Passes()
    {
        var parameters = new CompletionParameters { Stop = ["<<END>>"] };

        var errors = CompletionParameterValidator.Validate(
            parameters, ProviderType.LlamaSharp);

        errors.Should().BeEmpty();
    }

    [Test]
    public void Validate_SeedWithPositiveValue_Passes()
    {
        var parameters = new CompletionParameters { Seed = 42 };

        var errors = CompletionParameterValidator.Validate(
            parameters, ProviderType.LlamaSharp);

        errors.Should().BeEmpty();
    }

    [Test]
    public void Validate_SeedWithNegativeValue_Passes()
    {
        // Negative seeds round-trip via unchecked((uint)seed) wraparound;
        // the validator accepts any int and leaves the coercion to the client.
        var parameters = new CompletionParameters { Seed = -1 };

        var errors = CompletionParameterValidator.Validate(
            parameters, ProviderType.LlamaSharp);

        errors.Should().BeEmpty();
    }

    [Test]
    public void Spec_LlamaSharp_ClaimsParityOnStopAndSeed()
    {
        var spec = CompletionParameterSpec.For(ProviderType.LlamaSharp);

        spec.SupportsStop.Should().BeTrue();
        spec.MaxStopSequences.Should().Be(16);
        spec.SupportsSeed.Should().BeTrue();
    }
}

// ═══════════════════════════════════════════════════════════════════════
// responseFormat=json_object via GBNF grammar
// ═══════════════════════════════════════════════════════════════════════

[TestFixture]
public class LlamaSharpJsonGrammarsTests
{
    [Test]
    public void JsonObject_IsNonEmpty()
    {
        LlamaSharpJsonGrammars.JsonObject().Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void JsonObject_Cached_ReturnsSameReference()
    {
        // Grammar strings are built once and reused; this catches accidental
        // rebuild-on-call regressions that would allocate per request.
        var first = LlamaSharpJsonGrammars.JsonObject();
        var second = LlamaSharpJsonGrammars.JsonObject();

        ReferenceEquals(first, second).Should().BeTrue();
    }

    [Test]
    public void JsonObject_DeclaresRootAndCoreValueRules()
    {
        var grammar = LlamaSharpJsonGrammars.JsonObject();

        grammar.Should().Contain("root");
        grammar.Should().Contain("value");
        grammar.Should().Contain("obj");
        grammar.Should().Contain("arr");
        grammar.Should().Contain("string");
        grammar.Should().Contain("number");
        grammar.Should().Contain("\"true\"");
        grammar.Should().Contain("\"false\"");
        grammar.Should().Contain("\"null\"");
    }
}

[TestFixture]
public class LlamaSharpValidatorResponseFormatTests
{
    private static JsonElement Json(string raw) =>
        JsonDocument.Parse(raw).RootElement.Clone();

    [Test]
    public void Validate_ResponseFormat_JsonObject_Passes()
    {
        var parameters = new CompletionParameters
        {
            ResponseFormat = Json("""{"type":"json_object"}"""),
        };

        var errors = CompletionParameterValidator.Validate(
            parameters, ProviderType.LlamaSharp);

        errors.Should().BeEmpty();
    }

    [Test]
    public void Validate_ResponseFormat_JsonSchema_Passes()
    {
        // Phase C: the schema-to-GBNF converter is wired end-to-end, so
        // well-formed json_schema payloads are accepted at validation
        // time. Coverage-matrix gaps degrade at runtime via fallback to
        // the generic JSON grammar — they do not fail validation.
        var parameters = new CompletionParameters
        {
            ResponseFormat = Json(
                """{"type":"json_schema","json_schema":{"name":"x","schema":{"type":"object"}}}"""),
        };

        var errors = CompletionParameterValidator.Validate(
            parameters, ProviderType.LlamaSharp);

        errors.Should().BeEmpty();
    }

    [Test]
    public void Validate_ResponseFormat_Text_Passes()
    {
        // With OnlyJsonObjectResponseFormat flipped off, "text" is no
        // longer rejected by the validator. Runtime treats unknown
        // response_format shapes as a no-op (no grammar attached).
        var parameters = new CompletionParameters
        {
            ResponseFormat = Json("""{"type":"text"}"""),
        };

        var errors = CompletionParameterValidator.Validate(
            parameters, ProviderType.LlamaSharp);

        errors.Should().BeEmpty();
    }

    [Test]
    public void Spec_LlamaSharp_ResponseFormat_AcceptsBothShapes()
    {
        var spec = CompletionParameterSpec.For(ProviderType.LlamaSharp);

        spec.SupportsResponseFormat.Should().BeTrue();
        spec.OnlyJsonObjectResponseFormat.Should().BeFalse();
    }
}

// ═══════════════════════════════════════════════════════════════════════
// Phase 3 — reasoningEffort as informational chat-header notice
// ═══════════════════════════════════════════════════════════════════════

[TestFixture]
public class ChatHeaderNoticesTests
{
    [Test]
    public void FormatReasoningEffortNotice_NullEffort_ReturnsEmpty()
    {
        ChatHeaderNotices.FormatReasoningEffortNotice(null).Should().BeEmpty();
    }

    [Test]
    public void FormatReasoningEffortNotice_WhitespaceEffort_ReturnsEmpty()
    {
        ChatHeaderNotices.FormatReasoningEffortNotice("   ").Should().BeEmpty();
    }

    [Test]
    public void FormatReasoningEffortNotice_ValidEffort_RendersInformationalString()
    {
        var notice = ChatHeaderNotices.FormatReasoningEffortNotice("medium");

        notice.Should().Contain("reasoning-effort: medium");
        notice.Should().Contain("informational");
    }

    [Test]
    public void FormatReasoningEffortNotice_UppercaseInput_IsNormalisedToLowercase()
    {
        var notice = ChatHeaderNotices.FormatReasoningEffortNotice("HIGH");

        notice.Should().Contain("reasoning-effort: high");
        notice.Should().NotContain("HIGH");
    }
}

[TestFixture]
public class LlamaSharpValidatorReasoningEffortTests
{
    [Test]
    public void Validate_ReasoningEffort_ValidValue_Accepted_OnLlamaSharp()
    {
        // LlamaSharp accepts reasoningEffort informationally — the validator
        // must no longer treat it as an unsupported-parameter error.
        var parameters = new CompletionParameters { ReasoningEffort = "medium" };

        var errors = CompletionParameterValidator.Validate(
            parameters, ProviderType.LlamaSharp);

        errors.Should().BeEmpty();
    }

    [Test]
    public void Validate_ReasoningEffort_InvalidValue_Rejected_OnLlamaSharp()
    {
        // Informational-only does not mean "anything goes" — the whitelist
        // of valid values still applies so typos are caught.
        var parameters = new CompletionParameters { ReasoningEffort = "ludicrous" };

        var errors = CompletionParameterValidator.Validate(
            parameters, ProviderType.LlamaSharp);

        errors.Should().ContainSingle()
            .Which.Should().Contain("ludicrous");
    }

    [Test]
    public void Spec_LlamaSharp_ReasoningEffort_IsInformationalOnly()
    {
        var spec = CompletionParameterSpec.For(ProviderType.LlamaSharp);

        spec.SupportsReasoningEffort.Should().BeTrue();
        spec.ReasoningEffortInformationalOnly.Should().BeTrue();
    }

    [Test]
    public void Spec_OpenAI_ReasoningEffort_IsNotInformationalOnly()
    {
        // Sanity check — the informational-only flag must remain scoped to
        // providers that genuinely lack a mechanical knob, not leak into
        // providers that consume reasoningEffort on the wire.
        var spec = CompletionParameterSpec.For(ProviderType.OpenAI);

        spec.ReasoningEffortInformationalOnly.Should().BeFalse();
    }
}
