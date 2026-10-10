using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;

namespace SharpClaw.TestFixtures.ExternalRegistration;


[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "The module registration supplies this type to Microsoft DI, which activates its constructor through reflection.")]
internal sealed class SettingsFixtureState
{
    public string Message { get; set; } = "original";
}
