using System.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.Extensibility;
using SharpClaw.VS2026Extension.Services;

namespace SharpClaw.VS2026Extension;

/// <summary>
/// Entry point for the SharpClaw VS2026 extension.
/// Hosts the Chat tool window via the new VisualStudio.Extensibility SDK
/// (out-of-process, Remote UI based).
/// </summary>
[VisualStudioContribution]
internal sealed class SharpClawExtension : Extension
{
    /// <inheritdoc />
    public override ExtensionConfiguration ExtensionConfiguration => new()
    {
        Metadata = new(
            id: "SharpClaw.VS2026Extension.d5e3a8f1-4c2b-4e9d-8f1a-2b3c4d5e6f7a",
            version: ExtensionAssemblyVersion,
            publisherName: "mkn8rn",
            displayName: "SharpClaw for Visual Studio 2026",
            description: "SharpClaw AI agent integration for Visual Studio 2026."),
    };

    /// <inheritdoc />
    protected override ResourceManager? ResourceManager => null;

    /// <inheritdoc />
    protected override void InitializeServices(IServiceCollection serviceCollection)
    {
        base.InitializeServices(serviceCollection);

        // Backend connectivity is shared across commands and tool windows.
        serviceCollection.AddSingleton<SharpClawConnectionOptionsStore>();
        serviceCollection.AddSingleton<SharpClawBackend>();
        serviceCollection.AddSingleton<SharpClawConnector>();
        serviceCollection.AddSingleton<SharpClawChatSession>();

        // Register the output log both as itself and as IExtensionInitializer so
        // the framework calls its InitializeAsync to create the Output channel.
        // The previous attempt put IExtensionInitializer on the Extension class,
        // which the framework does not invoke, so the "SharpClaw" entry never
        // showed up in the Output window's "Show output from:" dropdown.
        serviceCollection.AddSingleton<SharpClawOutputLog>();
        serviceCollection.AddSingleton<IExtensionInitializer>(
            sp => sp.GetRequiredService<SharpClawOutputLog>());

        // Drives the auto-connect at extension startup so the SharpClaw output
        // pane is populated immediately and users don't have to click Connect
        // manually before the dropdown shows up.
        serviceCollection.AddSingleton<SharpClawAutoConnectInitializer>();
        serviceCollection.AddSingleton<IExtensionInitializer>(
            sp => sp.GetRequiredService<SharpClawAutoConnectInitializer>());
    }
}
