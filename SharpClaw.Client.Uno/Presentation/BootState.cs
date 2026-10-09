using System.Collections.Immutable;
using SharpClaw.Services;

namespace SharpClaw.Presentation;


public sealed record BootState(
    string Icon,
    Windows.UI.Color IconColor,
    string Text,
    Windows.UI.Color TextColor,
    bool IsRetryVisible,
    ImmutableArray<DiagnosticLine> DiagnosticLog = default);
