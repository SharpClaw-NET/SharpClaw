using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Application.Core.Modules;

/// <summary>
/// Common runtime boundary for modules that are hosted outside the main
/// bundled-module service provider.
/// </summary>
public interface IModuleRuntimeHost : IAsyncDisposable
{
    ISharpClawModule Module { get; }
    string SourceDirectory { get; }
    IServiceProvider Services { get; }

    IServiceScope CreateScope();
    bool TryAcquireExecution();
    void ReleaseExecution();
    Task DrainAsync(TimeSpan timeout, CancellationToken ct = default);
}
