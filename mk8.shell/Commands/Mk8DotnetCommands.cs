using Mk8.Shell.Safety;

namespace Mk8.Shell;

/// <summary>
/// <c>dotnet</c> command templates and word lists for <see cref="Mk8CommandWhitelist"/>.
/// <para>
/// All data is compile-time constant.  To add or modify allowed commands,
/// edit this file and recompile — there is no runtime registration.
/// </para>
/// </summary>
public static class Mk8DotnetCommands
{
    // ── Word lists (edit these arrays to change what agents can use) ──

    /// <summary>EF migration names the agent may use with <c>dotnet ef migrations add</c>.</summary>
    public static readonly string[] MigrationNames =
    [
        "Initial", "Baseline", "Seed",
        "AddUsers", "AddRoles", "AddPermissions", "AddAuth",
        "AddAgents", "AddChannels", "AddContexts", "AddModels",
        "AddProviders", "AddJobs", "AddTasks", "AddSkills",
        "AddResources", "AddContainers", "AddDevices",
        "AddMessages", "AddConversations", "AddScheduledTasks",
        "AddIndexes", "AddConstraints", "AddRelations",
        "UpdateSchema", "RefactorTables", "RenameColumns",
        "RemoveDeprecated", "Cleanup", "Optimize",
        "V1", "V2", "V3", "V4", "V5",
        "Test", "Debug", "Snapshot",

        // Single letters and digits (matching is case-insensitive).
        // EF prefixes digit-starting names with '_' in the generated class.
        "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M",
        "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z",
        "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
    ];

    /// <summary>
    /// Compile-time suffixes for project names.  Combined at whitelist
    /// construction with runtime base names from
    /// <see cref="Mk8RuntimeConfig.ProjectBases"/> to form compound
    /// names like <c>"BananaApi"</c> or <c>"Banana.Api"</c>.
    /// <para>
    /// The base name alone is also valid: <c>"Banana"</c>.
    /// </para>
    /// </summary>
    public static readonly string[] ProjectSuffixes =
    [
        "App", "Api", "Core", "Infrastructure", "Contracts",
        "Tests", "Utils", "Client", "Server", "Worker",
        "Service", "Web", "Grpc", "Shared", "Common",
        "Domain", "Data", "Models", "Handlers", "Extensions",

        // Compound suffixes (common .NET solution patterns).
        "Application.API", "Application.Core", "Application.Infrastructure",
        "Application.Contracts", "Application.Tests",
        "PublicAPI", "UITests",
    ];

    /// <summary>SDK templates the agent may use with <c>dotnet new</c>.</summary>
    public static readonly string[] DotnetTemplates =
    [
        "console", "classlib", "webapi", "web", "mvc", "razor",
        "worker", "mstest", "nunit", "xunit",
        "blazorserver", "blazorwasm", "grpc",
        "gitignore", "editorconfig", "globaljson",
        "tool-manifest",
    ];

    // ── Registration (called once at startup by Mk8CommandWhitelist) ──

    internal static KeyValuePair<string, string[]>[] GetWordLists() =>
    [
        new("MigrationNames", MigrationNames),
        new("ProjectSuffixes", ProjectSuffixes),
        new("DotnetTemplates", DotnetTemplates),
    ];

    internal static Mk8AllowedCommand[] GetCommands()
    {
        // Shared flag definitions
        var configFlag = new Mk8FlagDef("--configuration",
            new Mk8Slot("config", Mk8SlotKind.Choice, AllowedValues: ["Release", "Debug"]));
        var shortConfigFlag = new Mk8FlagDef("-c",
            new Mk8Slot("config", Mk8SlotKind.Choice, AllowedValues: ["Release", "Debug"]));
        var noRestore = new Mk8FlagDef("--no-restore");
        var noBuild = new Mk8FlagDef("--no-build");
        var outputFlag = new Mk8FlagDef("-o",
            new Mk8Slot("outputPath", Mk8SlotKind.SandboxPath));
        var longOutputFlag = new Mk8FlagDef("--output",
            new Mk8Slot("outputPath", Mk8SlotKind.SandboxPath));

        return
        [
            // ── Informational ─────────────────────────────────────
            new("dotnet version", "dotnet", ["--version"]),
            new("dotnet info", "dotnet", ["--info"]),
            new("dotnet list-sdks", "dotnet", ["--list-sdks"]),
            new("dotnet list-runtimes", "dotnet", ["--list-runtimes"]),
            new("dotnet tool list", "dotnet", ["tool", "list"]),

            // ── Build / publish / test / clean / restore / format ──
            new("dotnet build", "dotnet", ["build"],
                Flags: [configFlag, shortConfigFlag, noRestore, outputFlag, longOutputFlag]),

            new("dotnet publish", "dotnet", ["publish"],
                Flags: [configFlag, shortConfigFlag, noRestore, outputFlag, longOutputFlag]),

            new("dotnet test", "dotnet", ["test"],
                Flags: [configFlag, shortConfigFlag, noRestore, noBuild]),

            new("dotnet clean", "dotnet", ["clean"],
                Flags: [configFlag, shortConfigFlag]),

            new("dotnet restore", "dotnet", ["restore"],
                Flags: [new Mk8FlagDef("--no-cache")]),

            new("dotnet format", "dotnet", ["format"],
                Flags: [new Mk8FlagDef("--verify-no-changes")]),

            // ── dotnet new ────────────────────────────────────────
            new("dotnet new project", "dotnet", ["new"],
                Flags: [
                    new("-n", new Mk8Slot("name", Mk8SlotKind.CompoundName)),
                    new("--name", new Mk8Slot("name", Mk8SlotKind.CompoundName)),
                    outputFlag, longOutputFlag,
                ],
                Params: [new Mk8Slot("template", Mk8SlotKind.AdminWord, WordListName: "DotnetTemplates")]),

            // ── EF — safe operations only ─────────────────────────
            new("dotnet ef migrations add", "dotnet", ["ef", "migrations", "add"],
                Params: [new Mk8Slot("name", Mk8SlotKind.AdminWord, WordListName: "MigrationNames")]),

            new("dotnet ef migrations list", "dotnet", ["ef", "migrations", "list"]),

            new("dotnet ef migrations script", "dotnet", ["ef", "migrations", "script"],
                Flags: [new Mk8FlagDef("--idempotent"), outputFlag, longOutputFlag]),

            new("dotnet ef dbcontext info", "dotnet", ["ef", "dbcontext", "info"]),
            new("dotnet ef dbcontext list", "dotnet", ["ef", "dbcontext", "list"]),
        ];
    }
}
