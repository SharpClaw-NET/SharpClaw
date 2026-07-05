using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpClaw.Services;

/// <summary>
/// Persists multiple user accounts' authentication tokens to the
/// frontend instance root for multi-account
/// switching and "remember me" auto-login on app restart.
/// </summary>
public sealed class AccountStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly FrontendInstanceService _frontendInstance;
    private readonly object _lock = new();
    private AccountData _data;

    public AccountStore(FrontendInstanceService frontendInstance)
    {
        _frontendInstance = frontendInstance;
        _data = Load();
    }

    /// <summary>The user ID of the last active (auto-login) account, if any.</summary>
    public Guid? ActiveUserId
    {
        get { lock (_lock) return _data.ActiveUserId; }
    }

    /// <summary>Returns a snapshot of all saved accounts.</summary>
    public IReadOnlyList<SavedAccount> GetAccounts()
    {
        lock (_lock)
            return [.. _data.Accounts];
    }

    /// <summary>Returns the saved account for the active user, or null.</summary>
    public SavedAccount? GetActiveAccount()
    {
        lock (_lock)
        {
            if (_data.ActiveUserId is not { } id) return null;
            return _data.Accounts.Find(a => a.UserId == id);
        }
    }

    /// <summary>Returns a saved account by user ID, or null.</summary>
    public SavedAccount? GetAccount(Guid userId)
    {
        lock (_lock)
            return _data.Accounts.Find(a => a.UserId == userId);
    }

    /// <summary>
    /// Saves or updates an account's tokens after a successful login/refresh.
    /// Sets this account as the active account.
    /// </summary>
    public void SaveAccount(SavedAccount account)
    {
        lock (_lock)
        {
            var idx = _data.Accounts.FindIndex(a => a.UserId == account.UserId);
            if (idx >= 0)
                _data.Accounts[idx] = account;
            else
                _data.Accounts.Add(account);

            _data.ActiveUserId = account.UserId;
            Flush();
        }
    }

    /// <summary>Sets the active user ID without modifying tokens.</summary>
    public void SetActiveUser(Guid userId)
    {
        lock (_lock)
        {
            _data.ActiveUserId = userId;
            Flush();
        }
    }

    /// <summary>Removes a saved account by user ID.</summary>
    public void RemoveAccount(Guid userId)
    {
        lock (_lock)
        {
            _data.Accounts.RemoveAll(a => a.UserId == userId);
            if (_data.ActiveUserId == userId)
                _data.ActiveUserId = _data.Accounts.Count > 0
                    ? _data.Accounts[0].UserId
                    : null;
            Flush();
        }
    }

    /// <summary>Deletes all saved accounts and the store file.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _data = new();
            try { File.Delete(_frontendInstance.AccountsPath); } catch { /* best-effort */ }
        }
    }

    // ── Internal ─────────────────────────────────────────────────

    private AccountData Load()
    {
        try
        {
            if (!File.Exists(_frontendInstance.AccountsPath))
                return new();

            var json = File.ReadAllText(_frontendInstance.AccountsPath);
            return JsonSerializer.Deserialize<AccountData>(json, JsonOpts) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private void Flush()
    {
        try
        {
            var dir = Path.GetDirectoryName(_frontendInstance.AccountsPath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_data, JsonOpts);
            File.WriteAllText(_frontendInstance.AccountsPath, json);
        }
        catch { /* best-effort */ }
    }

    private sealed class AccountData
    {
        public Guid? ActiveUserId { get; set; }
        public List<SavedAccount> Accounts { get; set; } = [];
    }

    /// <summary>A persisted user account with cached authentication tokens.</summary>
    public sealed class SavedAccount
    {
        public Guid UserId { get; set; }
        public string Username { get; set; } = "";
        public string? AccessToken { get; set; }
        public DateTimeOffset? AccessTokenExpiresAt { get; set; }
        public string? RefreshToken { get; set; }
        public DateTimeOffset? RefreshTokenExpiresAt { get; set; }
        public bool RememberMe { get; set; }
    }
}
