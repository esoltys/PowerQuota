using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PowerQuota.Core.Storage;

public class StoredTokens
{
    public string AccessToken { get; set; } = string.Empty;
    public string? RefreshToken { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? TokenId { get; set; }
}

public class WindowsCredentialVault
{
    private static readonly string StorageDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PowerQuota"
    );
    private static readonly string VaultFile = Path.Combine(StorageDir, "vault.dat");

    private readonly Dictionary<string, StoredTokens> _tokens = new();
    private readonly Dictionary<string, string> _apiKeys = new();
    private readonly object _lock = new();

    public WindowsCredentialVault()
    {
        Load();
    }

    public void SaveTokens(string accountId, StoredTokens tokens)
    {
        lock (_lock)
        {
            _tokens[accountId] = tokens;
            Persist();
        }
    }

    public StoredTokens? GetTokens(string accountId)
    {
        lock (_lock)
        {
            return _tokens.TryGetValue(accountId, out var tokens) ? tokens : null;
        }
    }

    public void SaveApiKey(string accountId, string apiKey)
    {
        lock (_lock)
        {
            _apiKeys[accountId] = apiKey;
            Persist();
        }
    }

    public string? GetApiKey(string accountId)
    {
        lock (_lock)
        {
            return _apiKeys.TryGetValue(accountId, out var key) ? key : null;
        }
    }

    public void RemoveAccount(string accountId)
    {
        lock (_lock)
        {
            _tokens.Remove(accountId);
            _apiKeys.Remove(accountId);
            Persist();
        }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(StorageDir);
            var payload = new VaultPayload
            {
                Tokens = _tokens,
                ApiKeys = _apiKeys
            };
            var json = JsonSerializer.Serialize(payload);
            var rawBytes = Encoding.UTF8.GetBytes(json);
            var encryptedBytes = ProtectedData.Protect(rawBytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(VaultFile, encryptedBytes);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PowerQuota Vault] Persist error: {ex.Message}");
        }
    }

    private void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(VaultFile)) return;
            try
            {
                var encryptedBytes = File.ReadAllBytes(VaultFile);
                var rawBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
                var json = Encoding.UTF8.GetString(rawBytes);
                var payload = JsonSerializer.Deserialize<VaultPayload>(json);
                if (payload != null)
                {
                    _tokens.Clear();
                    foreach (var (k, v) in payload.Tokens) _tokens[k] = v;
                    _apiKeys.Clear();
                    foreach (var (k, v) in payload.ApiKeys) _apiKeys[k] = v;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PowerQuota Vault] Load error: {ex.Message}");
            }
        }
    }

    private class VaultPayload
    {
        public Dictionary<string, StoredTokens> Tokens { get; set; } = new();
        public Dictionary<string, string> ApiKeys { get; set; } = new();
    }
}

