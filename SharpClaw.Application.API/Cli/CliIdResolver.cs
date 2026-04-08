using SharpClaw.Contracts.Modules;

namespace SharpClaw.Application.API.Cli;

/// <summary>
/// Bridges the static <see cref="CliIdMap"/> into a DI-resolvable service
/// so that module CLI handlers can resolve short IDs and print JSON with
/// <c>#</c> annotations.
/// </summary>
internal sealed class CliIdResolver : ICliIdResolver
{
    public Guid Resolve(string arg) => CliIdMap.Resolve(arg);

    public int GetOrAssign(Guid guid) => CliIdMap.GetOrAssign(guid);

    public void PrintJson(object value) => CliDispatcher.PrintJsonWithShortIds(value);
}
