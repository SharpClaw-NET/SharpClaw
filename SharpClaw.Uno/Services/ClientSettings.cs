using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpClaw.Services;

/// <summary>
/// Persists Uno-specific frontend-only preferences to
/// <c>%LOCALAPPDATA%/SharpClaw/client-settings.json</c>.
/// <para>
/// These settings are a <b>frontend-only convention</b> — they are never
/// sent to or read by the API backend. Examples include the default
/// transcription agent, the selected audio input device, and any future
/// per-channel UI preferences.
/// </para>
/// <para>
/// <b>Why not <c>ApplicationData.Current.LocalSettings</c>?</b><br/>
/// The WinRT LocalSettings API is container-scoped in packaged (MSIX)
/// apps and has limited support on non-Windows Uno targets.  A plain
/// JSON file in LocalAppData is portable, inspectable, and survives
/// across packaging modes (unpackaged exe, MSIX sideload, Store).
/// </para>
/// </summary>
public sealed class ClientSettings
{
    private static readonly string BaseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SharpClaw");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _lock = new();
    private Dictionary<string, string> _values;
    private string _settingsPath;
    private Guid? _activeUserId;

    // ── Well-known keys ──────────────────────────────────────────
    // Centralised here so every page references the same constants.

    /// <summary>Default transcription agent selected in channel settings.</summary>
    public const string TranscriptionAgentId = "TranscriptionAgentId";

    /// <summary>Selected audio input device (microphone).</summary>
    public const string SelectedAudioDeviceId = "SelectedAudioDeviceId";

    public ClientSettings()
    {
        _settingsPath = Path.Combine(BaseDir, "client-settings.json");
        _values = Load();
    }

    /// <summary>Current user context, if any.</summary>
    public Guid? ActiveUserId { get { lock (_lock) return _activeUserId; } }

    /// <summary>
    /// Switches the settings context to a specific user.
    /// Saves current in-memory state, then loads the target user's settings file
    /// from <c>%LOCALAPPDATA%/SharpClaw/users/{userId}/settings.json</c>.
    /// </summary>
    public void SwitchUser(Guid userId)
    {
        lock (_lock)
        {
            if (_values.Count > 0)
                Flush();

            _activeUserId = userId;
            _settingsPath = UserSettingsPath(userId);
            _values = Load();
        }
    }

    private static string UserSettingsPath(Guid userId)
        => Path.Combine(BaseDir, "users", userId.ToString("N"), "settings.json");

    /// <summary>
    /// Reads a setting value by key. Returns <c>null</c> when absent.
    /// </summary>
    public string? Get(string key)
    {
        lock (_lock)
            return _values.TryGetValue(key, out var v) ? v : null;
    }

    /// <summary>
    /// Writes or removes a setting. Passing <c>null</c> removes the key.
    /// Changes are flushed to disk immediately.
    /// </summary>
    public void Set(string key, string? value)
    {
        lock (_lock)
        {
            if (value is null)
                _values.Remove(key);
            else
                _values[key] = value;

            Flush();
        }
    }

    /// <summary>
    /// Deletes the settings file from disk and clears in-memory state.
    /// Called by the Danger Zone reset flow.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _values.Clear();
            _activeUserId = null;
            _settingsPath = Path.Combine(BaseDir, "client-settings.json");
            try { File.Delete(_settingsPath); } catch { /* best-effort */ }
            try
            {
                var usersDir = Path.Combine(BaseDir, "users");
                if (Directory.Exists(usersDir))
                    Directory.Delete(usersDir, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    // ── Internal ─────────────────────────────────────────────────

    private Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
                return new(StringComparer.Ordinal);

            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)
                   ?? new(StringComparer.Ordinal);
        }
        catch
        {
            // Corrupted or inaccessible — start fresh.
            return new(StringComparer.Ordinal);
        }
    }

    private void Flush()
    {
        try
        {
            var dir = Path.GetDirectoryName(_settingsPath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_values, JsonOptions);
            File.WriteAllText(_settingsPath, json);
        }
        catch
        {
            // Swallow — disk may be read-only during edge cases.
            // Settings will be retried on next write.
        }
    }
}
