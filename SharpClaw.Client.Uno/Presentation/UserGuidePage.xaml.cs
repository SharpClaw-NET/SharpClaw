using Microsoft.UI.Xaml.Media;
using SharpClaw.Helpers;

namespace SharpClaw.Presentation;

public sealed partial class UserGuidePage : Page
{
    private static readonly FontFamily _mono = TerminalUI.Mono;
    private static readonly SolidColorBrush _trans = TerminalUI.Transparent;
    
    private string _activeTopic = "welcome";

    // Guide topics with their file names (without .md extension)
    private static readonly (string Id, string Title)[] Topics =
    [
        ("welcome", "Welcome"),
        ("getting-started", "Getting Started"),
        ("channels-threads", "Channels & Threads"),
        ("agents-models", "Agents & Models"),
        ("chat-features", "Chat Features"),
        ("permissions", "Permissions & Roles"),
        ("jobs-tasks", "Jobs & Tasks"),
        ("bot-integrations", "Bot Integrations"),
        ("gateway", "Gateway"),
        ("advanced", "Advanced Topics"),
        ("troubleshooting", "Troubleshooting"),
    ];

    public UserGuidePage()
    {
        InitializeComponent();
        Loaded += OnPageLoaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        BuildTopicsList();
        _ = LoadTopicAsync("welcome");
    }

    private void BuildTopicsList()
    {
        TopicsPanel.Children.Clear();

        foreach (var (id, title) in Topics)
        {
            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = _trans,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(12, 8, 12, 8),
                Tag = id,
            };

            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            sp.Children.Add(new TextBlock
            {
                Text = "›",
                FontFamily = _mono,
                FontSize = 12,
                Foreground = B(id == _activeTopic ? 0x00FF00 : 0x555555)
            });
            sp.Children.Add(new TextBlock
            {
                Text = title,
                FontFamily = _mono,
                FontSize = 12,
                Foreground = B(id == _activeTopic ? 0xE0E0E0 : 0x999999)
            });

            btn.Content = sp;
            btn.Click += (_, _) => _ = LoadTopicAsync(id);
            TopicsPanel.Children.Add(btn);
        }
    }

    private async Task LoadTopicAsync(string topicId)
    {
        _activeTopic = topicId;
        HighlightActiveTopic();

        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromApplicationUriAsync(
                new Uri($"ms-appx:///Assets/Guide/{topicId}.md"));
            var markdown = await Windows.Storage.FileIO.ReadTextAsync(file);
            
            // Simple markdown rendering (basic formatting only)
            ContentText.Text = RenderMarkdown(markdown);
            
            // Update title
            var topic = Array.Find(Topics, t => t.Id == topicId);
            ContentTitle.Text = topic.Title ?? "Guide";
        }
        catch
        {
            ContentText.Text = "Unable to load guide content.";
            ContentTitle.Text = "Error";
        }
    }

    private void HighlightActiveTopic()
    {
        foreach (var child in TopicsPanel.Children)
        {
            if (child is not Button { Tag: string tag, Content: StackPanel sp } || sp.Children.Count < 2)
                continue;

            var isActive = tag == _activeTopic;
            if (sp.Children[0] is TextBlock arrow)
                arrow.Foreground = B(isActive ? 0x00FF00 : 0x555555);
            if (sp.Children[1] is TextBlock title)
                title.Foreground = B(isActive ? 0xE0E0E0 : 0x999999);
        }
    }

    /// <summary>
    /// Simple markdown-to-plain-text renderer for terminal display.
    /// Preserves basic structure while removing markdown syntax.
    /// </summary>
    private static string RenderMarkdown(string markdown)
    {
        var lines = markdown.Split('\n');
        var result = new System.Text.StringBuilder();

        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd();
            
            // Headers
            if (trimmed.StartsWith("# "))
                result.AppendLine($"═══ {trimmed[2..].ToUpperInvariant()} ═══\n");
            else if (trimmed.StartsWith("## "))
                result.AppendLine($"\n─── {trimmed[3..]} ───\n");
            else if (trimmed.StartsWith("### "))
                result.AppendLine($"\n• {trimmed[4..]}\n");
            
            // Bullet lists
            else if (trimmed.StartsWith("- "))
                result.AppendLine($"  › {trimmed[2..]}");
            else if (trimmed.StartsWith("* "))
                result.AppendLine($"  › {trimmed[2..]}");
            
            // Numbered lists
            else if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\d+\.\s"))
                result.AppendLine($"  {trimmed}");
            
            // Code blocks (just indent)
            else if (trimmed.StartsWith("```"))
                result.AppendLine(trimmed.StartsWith("```") && trimmed.Length > 3 ? "" : "");
            else if (trimmed.StartsWith("    "))
                result.AppendLine($"    {trimmed.TrimStart()}");
            
            // Bold/italic removal (simple)
            else if (!string.IsNullOrWhiteSpace(trimmed))
            {
                var clean = trimmed
                    .Replace("**", "")
                    .Replace("__", "")
                    .Replace("`", "")
                    .Replace("*", "")
                    .Replace("_", "");
                result.AppendLine(clean);
            }
            else
                result.AppendLine();
        }

        return result.ToString();
    }

    private static SolidColorBrush B(int rgb) => TerminalUI.Brush(rgb);

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (App.Services is not { } services) return;
        _ = services.GetRequiredService<INavigator>().NavigateRouteAsync(this, "Main");
    }
}
