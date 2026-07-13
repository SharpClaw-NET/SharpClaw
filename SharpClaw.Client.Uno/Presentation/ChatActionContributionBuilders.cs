using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SharpClaw.Contracts.Modules;
using SharpClaw.Helpers;
using SharpClaw.Services;

namespace SharpClaw.Presentation;

internal sealed record ChatActionBuildContext(
    SharpClawApiClient Api,
    ModuleFrontendContribution Contribution,
    Action<string> InsertText,
    Action<string> ReportError,
    CancellationToken CancellationToken);

internal interface IChatActionContributionBuilder
{
    string Key { get; }

    FrameworkElement Build(ChatActionBuildContext context);
}

internal sealed class ChatActionContributionBuilderRegistry
{
    private readonly Dictionary<string, IChatActionContributionBuilder> _builders;

    private ChatActionContributionBuilderRegistry(IEnumerable<IChatActionContributionBuilder> builders)
    {
        _builders = builders.ToDictionary(builder => builder.Key, StringComparer.OrdinalIgnoreCase);
    }

    public static ChatActionContributionBuilderRegistry CreateDefault() =>
        new([new ApiButtonChatActionContributionBuilder()]);

    public IChatActionContributionBuilder? Resolve(string builderKey)
        => _builders.GetValueOrDefault(builderKey);
}

internal sealed class ApiButtonChatActionContributionBuilder : IChatActionContributionBuilder
{
    public string Key => "api-button";

    public FrameworkElement Build(ChatActionBuildContext context)
    {
        var button = new Button
        {
            Background = TerminalUI.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 4),
            MinWidth = 0,
            MinHeight = 0,
            Content = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(context.Contribution.Icon) ? "+" : context.Contribution.Icon!,
                FontFamily = TerminalUI.Mono,
                FontSize = 14,
                Foreground = TerminalUI.Brush(0x00FF00),
            },
        };

        ToolTipService.SetToolTip(button, string.IsNullOrWhiteSpace(context.Contribution.Tooltip)
            ? context.Contribution.Label
            : context.Contribution.Tooltip);
        button.Click += async (_, _) => await InvokeAsync(button, context);
        return button;
    }

    private static async Task InvokeAsync(Button button, ChatActionBuildContext context)
    {
        var action = context.Contribution.Action;
        if (action is null)
        {
            context.ReportError($"{context.Contribution.Label} did not declare an action.");
            return;
        }

        button.IsEnabled = false;
        try
        {
            var method = action.Method.Trim().ToUpperInvariant();
            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var resp = method switch
            {
                "GET" => await context.Api.GetAsync(action.InternalApiPath, context.CancellationToken),
                "POST" => await context.Api.PostAsync(action.InternalApiPath, content, context.CancellationToken),
                _ => null,
            };

            if (resp is null)
            {
                context.ReportError($"{context.Contribution.Label} uses unsupported method '{action.Method}'.");
                return;
            }

            if (!resp.IsSuccessStatusCode)
            {
                context.ReportError($"{context.Contribution.Label} failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");
                return;
            }

            if (!IsInsertTextResponse(action.ResponseMode))
                return;

            using var stream = await resp.Content.ReadAsStreamAsync(context.CancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: context.CancellationToken);
            var text = ReadTextResult(doc.RootElement);
            if (!string.IsNullOrWhiteSpace(text))
                context.InsertText(text);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            context.ReportError($"{context.Contribution.Label} failed: {ex.Message}");
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private static bool IsInsertTextResponse(string? responseMode)
        => responseMode is not null
            && (responseMode.Equals("insert-text", StringComparison.OrdinalIgnoreCase)
                || responseMode.Equals("insertText", StringComparison.OrdinalIgnoreCase));

    private static string? ReadTextResult(JsonElement root)
    {
        foreach (var key in new[] { "insertText", "text" })
        {
            if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }

        return null;
    }
}
