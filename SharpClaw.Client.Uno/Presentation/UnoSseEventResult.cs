using System.Text.Json;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Presentation;


public sealed record UnoSseEventResult(bool ShouldEnd, bool TextChanged);
