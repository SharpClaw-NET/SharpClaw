using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media;

namespace SharpClaw.Helpers;

/// <summary>
/// Shared terminal-style UI constants and cached brushes used across the base client.
/// </summary>
internal static class TerminalUI
{
    // ── JSON ─────────────────────────────────────────────────────
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
    // ── Fonts ────────────────────────────────────────────────────
    public static readonly FontFamily Mono = new("Consolas, Courier New, monospace");

    // ── Brush cache ─────────────────────────────────────────────
    public static readonly SolidColorBrush Transparent = new(Microsoft.UI.Colors.Transparent);
    private static readonly Dictionary<int, SolidColorBrush> _brushCache = [];

    public static SolidColorBrush Brush(int rgb)
    {
        if (!_brushCache.TryGetValue(rgb, out var brush))
        {
            brush = new SolidColorBrush(ColorFrom(rgb));
            _brushCache[rgb] = brush;
        }
        return brush;
    }

    public static Windows.UI.Color ColorFrom(int rgb)
        => Windows.UI.Color.FromArgb(255,
            (byte)((rgb >> 16) & 0xFF),
            (byte)((rgb >> 8) & 0xFF),
            (byte)(rgb & 0xFF));

    // ── Helpers ──────────────────────────────────────────────────

    public static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "\u2026";

    public static void CopyToClipboard(string text)
    {
        var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dataPackage.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
    }

    /// <summary>Creates a small remove button in terminal style.</summary>
    public static Button RemoveButton(Action onClick)
    {
        var btn = new Button
        {
            Content = new TextBlock { Text = "\u2715", FontFamily = Mono, FontSize = 10, Foreground = Brush(0xFF4444) },
            Background = Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 4), MinWidth = 0, MinHeight = 0,
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

}
