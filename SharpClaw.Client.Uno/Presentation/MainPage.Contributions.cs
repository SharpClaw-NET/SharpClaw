using SharpClaw.Contracts.Modules;
using SharpClaw.Services;

namespace SharpClaw.Presentation;

public sealed partial class MainPage
{
    private readonly ChatActionContributionBuilderRegistry _chatActionBuilders =
        ChatActionContributionBuilderRegistry.CreateDefault();

    private void RebuildChatActionContributions()
    {
        if (App.Services is null) return;

        var registry = App.Services.GetService<ModuleFrontendContributionRegistry>();
        if (registry is null) return;
        var api = App.Services.GetRequiredService<SharpClawApiClient>();

        ChatActionsPanel.Children.Clear();
        foreach (var contribution in registry.GetActive(FrontendContributionPoint.ChatInputAction))
        {
            var builder = _chatActionBuilders.Resolve(contribution.BuilderKey);
            if (builder is null) continue;

            ChatActionsPanel.Children.Add(builder.Build(new ChatActionBuildContext(
                api,
                contribution,
                InsertChatActionText,
                ReportChatActionError,
                CancellationToken.None)));
        }
    }

    private void InsertChatActionText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var existing = MessageInput.Text ?? string.Empty;
        MessageInput.Text = string.IsNullOrWhiteSpace(existing)
            ? text
            : $"{existing} {text}";
        MessageInput.Focus(FocusState.Programmatic);
    }

    private void ReportChatActionError(string message)
    {
        AppendMessage("system", $"✗ {message}", DateTimeOffset.Now, senderName: "system");
        ScrollToBottom();
    }
}
