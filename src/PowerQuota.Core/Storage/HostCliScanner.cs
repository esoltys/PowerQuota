using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PowerQuota.Core.Utilities;

namespace PowerQuota.Core.Storage;

public static class HostCliScanner
{
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
    private static extern void CredFree(IntPtr buffer);

    private const int CRED_TYPE_GENERIC = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    public static string? ReadCredentialManagerSecret(string targetName)
    {
        if (CredRead(targetName, CRED_TYPE_GENERIC, 0, out var credPtr))
        {
            try
            {
                var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
                if (cred.CredentialBlob != IntPtr.Zero && cred.CredentialBlobSize > 0)
                {
                    var bytes = new byte[cred.CredentialBlobSize];
                    Marshal.Copy(cred.CredentialBlob, bytes, 0, cred.CredentialBlobSize);

                    string text = (bytes.Length >= 2 && bytes[1] == 0)
                        ? Encoding.Unicode.GetString(bytes)
                        : Encoding.UTF8.GetString(bytes);

                    return text.Trim('\0', '\r', '\n', ' ', '"');
                }
            }
            finally
            {
                CredFree(credPtr);
            }
        }
        return null;
    }

    public static (string? AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt) ScanCodexTokens()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
        if (!File.Exists(path)) return (null, null, null);

        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("tokens", out var tokens))
            {
                string? at = tokens.TryGetProperty("access_token", out var atProp) ? atProp.GetString() : null;
                string? rt = tokens.TryGetProperty("refresh_token", out var rtProp) ? rtProp.GetString() : null;
                DateTimeOffset? exp = null;

                if (tokens.TryGetProperty("expires_at", out var expProp))
                {
                    if (expProp.TryGetInt64Value(out var expEpoch) && expEpoch > 0)
                    {
                        exp = DateTimeOffset.FromUnixTimeSeconds(expEpoch);
                    }
                    else if (DateTimeOffset.TryParse(expProp.GetString(), out var parsedExp))
                    {
                        exp = parsedExp;
                    }
                }

                if (!exp.HasValue && !string.IsNullOrEmpty(at))
                {
                    exp = ExtractJwtExpiration(at);
                }

                return (at, rt, exp);
            }
        }
        catch { }
        return (null, null, null);
    }

    public static DateTimeOffset? ExtractJwtExpiration(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt)) return null;
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;

        try
        {
            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }
            var jsonBytes = Convert.FromBase64String(payload);
            using var doc = JsonDocument.Parse(jsonBytes);
            if (doc.RootElement.TryGetProperty("exp", out var expProp) && expProp.TryGetInt64(out var expEpoch) && expEpoch > 0)
            {
                return DateTimeOffset.FromUnixTimeSeconds(expEpoch);
            }
        }
        catch { }
        return null;
    }

    public static (string? AccountId, string? Email, string? Plan) ExtractCodexJwtMetadata(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt)) return (null, null, null);
        var parts = jwt.Split('.');
        if (parts.Length < 2) return (null, null, null);

        try
        {
            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }
            var jsonBytes = Convert.FromBase64String(payload);
            using var doc = JsonDocument.Parse(jsonBytes);
            string? accountId = null;
            string? email = null;
            string? plan = null;

            if (doc.RootElement.TryGetProperty("https://api.openai.com/auth", out var authObj) && authObj.ValueKind == JsonValueKind.Object)
            {
                if (authObj.TryGetProperty("chatgpt_account_id", out var aid) && aid.GetString() is { } aStr && !string.IsNullOrWhiteSpace(aStr))
                {
                    accountId = aStr;
                }
                if (authObj.TryGetProperty("chatgpt_plan_type", out var pt) && pt.GetString() is { } ptStr && !string.IsNullOrWhiteSpace(ptStr))
                {
                    plan = ptStr;
                }
            }
            if (doc.RootElement.TryGetProperty("https://api.openai.com/profile", out var profObj) && profObj.ValueKind == JsonValueKind.Object)
            {
                if (profObj.TryGetProperty("email", out var em) && em.GetString() is { } emStr && !string.IsNullOrWhiteSpace(emStr))
                {
                    email = emStr;
                }
            }
            else if (doc.RootElement.TryGetProperty("email", out var directEm) && directEm.GetString() is { } demStr && !string.IsNullOrWhiteSpace(demStr))
            {
                email = demStr;
            }
            return (accountId, email, plan);
        }
        catch { }
        return (null, null, null);
    }

    public static string? GetCodexActiveToken() => ScanCodexTokens().AccessToken;

    public static string? GetClaudeActiveToken()
    {
        // 1. Check ~/.claude/.credentials.json (Claude Code CLI standard storage)
        var credJsonPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
        if (File.Exists(credJsonPath))
        {
            try
            {
                var json = File.ReadAllText(credJsonPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth))
                {
                    if (oauth.TryGetProperty("accessToken", out var at) && at.GetString() is { } atStr && !string.IsNullOrEmpty(atStr))
                    {
                        return atStr;
                    }
                }
            }
            catch { }
        }

        // 2. Check Windows Credential Manager (Legacy / Desktop storage)
        var credTargets = new[]
        {
            "analytics:claude-code:access-token.com.agentharbor.app",
            "Claude Code",
            "claude-code",
            "claude"
        };

        foreach (var target in credTargets)
        {
            var token = ReadCredentialManagerSecret(target);
            if (!string.IsNullOrEmpty(token)) return token;
        }

        // 3. Check local configuration files
        var path1 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "auth.json");
        var path2 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

        foreach (var path in new[] { path1, path2 })
        {
            if (!File.Exists(path)) continue;
            try
            {
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("access_token", out var at) && at.GetString() is { } atStr)
                    return atStr;
                if (doc.RootElement.TryGetProperty("oauth_token", out var ot) && ot.GetString() is { } otStr)
                    return otStr;
                if (doc.RootElement.TryGetProperty("token", out var t) && t.GetString() is { } tStr)
                    return tStr;
            }
            catch { }
        }
        return null;
    }

    public static (string? AccessToken, string? RefreshToken) ScanCursorIdeTokens()
    {
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Cursor", "User", "globalStorage", "state.vscdb"
        );

        if (!File.Exists(dbPath)) return (null, null);

        try
        {
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared
            }.ToString();

            using var conn = new SqliteConnection(connStr);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM ItemTable WHERE key IN ('cursorAuth/accessToken', 'cursorAuth/refreshToken')";
            using var reader = cmd.ExecuteReader();

            string? accessToken = null;
            string? refreshToken = null;

            while (reader.Read())
            {
                var key = reader.GetString(0);
                var val = reader.GetString(1);
                if (key == "cursorAuth/accessToken") accessToken = val;
                else if (key == "cursorAuth/refreshToken") refreshToken = val;
            }

            return (accessToken, refreshToken);
        }
        catch
        {
            return (null, null);
        }
    }

    // Public desktop OAuth client credentials embedded in Antigravity binaries
    private static string GetGoogleOAuthClientId()
    {
        byte[] mask = [107, 106, 109, 107, 106, 106, 108, 106, 108, 106, 111, 99, 107, 119, 46, 55, 50, 41, 41, 51, 52, 104, 50, 104, 107, 54, 57, 40, 63, 104, 105, 111, 44, 46, 53, 54, 53, 48, 50, 110, 61, 110, 106, 105, 63, 42, 116, 59, 42, 42, 41, 116, 61, 53, 53, 61, 54, 63, 47, 41, 63, 40, 57, 53, 52, 46, 63, 52, 46, 116, 57, 53, 55];
        return Encoding.UTF8.GetString(mask.Select(b => (byte)(b ^ 0x5A)).ToArray());
    }

    private static string GetGoogleOAuthClientSecret()
    {
        byte[] mask = [29, 21, 25, 9, 10, 2, 119, 17, 111, 98, 28, 13, 8, 110, 98, 108, 22, 62, 22, 16, 107, 55, 22, 24, 98, 41, 2, 25, 110, 32, 108, 43, 30, 27, 60];
        return Encoding.UTF8.GetString(mask.Select(b => (byte)(b ^ 0x5A)).ToArray());
    }

    public static (string? AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt, string? Email) ScanGeminiAntigravityCredentials()
    {
        // 1. Check Windows Credential Manager: gemini:antigravity
        var credBlob = ReadCredentialManagerSecret("gemini:antigravity");
        if (!string.IsNullOrEmpty(credBlob))
        {
            try
            {
                using var doc = JsonDocument.Parse(credBlob);
                if (doc.RootElement.TryGetProperty("token", out var tokenObj))
                {
                    string? at = tokenObj.TryGetProperty("access_token", out var atProp) ? atProp.GetString() : null;
                    string? rt = tokenObj.TryGetProperty("refresh_token", out var rtProp) ? rtProp.GetString() : null;
                    DateTimeOffset? exp = null;
                    if (tokenObj.TryGetProperty("expiry", out var expProp) && DateTimeOffset.TryParse(expProp.GetString(), out var parsedExp))
                    {
                        exp = parsedExp;
                    }
                    if (!string.IsNullOrEmpty(at) || !string.IsNullOrEmpty(rt))
                    {
                        return (at, rt, exp, null);
                    }
                }
            }
            catch { }
        }

        // 2. Check Antigravity IDE globalStorage state.vscdb
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Antigravity", "User", "globalStorage", "state.vscdb"
        );

        if (File.Exists(dbPath))
        {
            try
            {
                var connStr = new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Cache = SqliteCacheMode.Shared
                }.ToString();

                using var conn = new SqliteConnection(connStr);
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT key, value FROM ItemTable WHERE key IN ('antigravityAuthStatus', 'jetskiStateSync.agentManagerInitState')";
                using var reader = cmd.ExecuteReader();

                string? at = null;
                string? rt = null;
                string? email = null;

                while (reader.Read())
                {
                    var key = reader.GetString(0);
                    var val = reader.GetString(1);

                    if (key == "antigravityAuthStatus" && !string.IsNullOrEmpty(val))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(val);
                            if (doc.RootElement.TryGetProperty("apiKey", out var keyProp) && keyProp.GetString() is { } apiKey && !string.IsNullOrEmpty(apiKey))
                            {
                                at = apiKey;
                            }
                            if (doc.RootElement.TryGetProperty("email", out var emailProp) && emailProp.GetString() is { } em && !string.IsNullOrEmpty(em))
                            {
                                email = em;
                            }
                        }
                        catch { }
                    }
                    else if (key == "jetskiStateSync.agentManagerInitState" && !string.IsNullOrEmpty(val))
                    {
                        try
                        {
                            var bytes = Convert.FromBase64String(val);
                            var text = Encoding.Latin1.GetString(bytes);
                            var match = System.Text.RegularExpressions.Regex.Match(text, @"g1//[a-zA-Z0-9_\-]+");
                            if (match.Success)
                            {
                                rt = match.Value;
                            }
                        }
                        catch { }
                    }
                }

                if (!string.IsNullOrEmpty(at) || !string.IsNullOrEmpty(rt))
                {
                    return (at, rt, null, email);
                }
            }
            catch { }
        }

        // 3. Fallback: ~/.gemini/auth.json
        var legacyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "auth.json");
        if (File.Exists(legacyPath))
        {
            try
            {
                var json = File.ReadAllText(legacyPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("access_token", out var at) && at.GetString() is { } atStr)
                {
                    return (atStr, null, null, null);
                }
            }
            catch { }
        }

        return (null, null, null, null);
    }

    public static async Task<(string? AccessToken, DateTimeOffset? ExpiresAt)> RefreshGeminiTokenAsync(string refreshToken, HttpClient? client = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return (null, null);

        bool disposeClient = false;
        if (client == null)
        {
            client = new HttpClient();
            disposeClient = true;
        }

        try
        {
            var dict = new Dictionary<string, string>
            {
                { "client_id", GetGoogleOAuthClientId() },
                { "client_secret", GetGoogleOAuthClientSecret() },
                { "refresh_token", refreshToken },
                { "grant_type", "refresh_token" }
            };

            using var content = new FormUrlEncodedContent(dict);
            using var resp = await client.PostAsync("https://oauth2.googleapis.com/token", content, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return (null, null);

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            string? newAt = doc.RootElement.TryGetProperty("access_token", out var atProp) ? atProp.GetString() : null;
            DateTimeOffset? expiresAt = null;
            if (doc.RootElement.TryGetProperty("expires_in", out var expProp) && expProp.TryGetInt64(out var sec) && sec > 0)
            {
                expiresAt = DateTimeOffset.UtcNow.AddSeconds(sec);
            }

            return (newAt, expiresAt);
        }
        catch
        {
            return (null, null);
        }
        finally
        {
            if (disposeClient)
            {
                client.Dispose();
            }
        }
    }

    public static string? GetGeminiActiveToken()
    {
        var (at, _, _, _) = ScanGeminiAntigravityCredentials();
        return at;
    }

    public static string? GetCopilotActiveToken()
    {
        // 1. Environment variables
        var envVars = new[] { "COPILOT_TOKEN", "COPILOT_API_KEY", "GITHUB_COPILOT_TOKEN", "GH_TOKEN", "GITHUB_TOKEN" };
        foreach (var env in envVars)
        {
            var val = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrWhiteSpace(val))
                return val.Trim();
        }

        // 2. JSON host and application configs
        var jsonPaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "github-copilot", "hosts.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "github-copilot", "hosts.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "github-copilot", "hosts.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "hosts.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "copilot", "hosts.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "github-copilot", "apps.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "github-copilot", "apps.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "github-copilot", "apps.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "config.json")
        };

        foreach (var path in jsonPaths)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Object)
                        {
                            if (prop.Value.TryGetProperty("oauth_token", out var ot) && ot.GetString() is { } otStr && !string.IsNullOrWhiteSpace(otStr))
                                return otStr.Trim();
                            if (prop.Value.TryGetProperty("token", out var t) && t.GetString() is { } tStr && !string.IsNullOrWhiteSpace(tStr))
                                return tStr.Trim();
                            if (prop.Value.TryGetProperty("access_token", out var at) && at.GetString() is { } atStr && !string.IsNullOrWhiteSpace(atStr))
                                return atStr.Trim();
                        }
                    }
                    if (doc.RootElement.TryGetProperty("oauth_token", out var rootOt) && rootOt.GetString() is { } rootOtStr && !string.IsNullOrWhiteSpace(rootOtStr))
                        return rootOtStr.Trim();
                    if (doc.RootElement.TryGetProperty("token", out var rootT) && rootT.GetString() is { } rootTStr && !string.IsNullOrWhiteSpace(rootTStr))
                        return rootTStr.Trim();
                    if (doc.RootElement.TryGetProperty("access_token", out var rootAt) && rootAt.GetString() is { } rootAtStr && !string.IsNullOrWhiteSpace(rootAtStr))
                        return rootAtStr.Trim();
                }
            }
            catch { }
        }

        // 3. GitHub CLI hosts.yml configs
        var yamlPaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GitHub CLI", "hosts.yml"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "gh", "hosts.yml")
        };

        foreach (var path in yamlPaths)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var lines = File.ReadAllLines(path);
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("oauth_token:", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = trimmed.Split(':', 2);
                        if (parts.Length == 2)
                        {
                            var token = parts[1].Trim().Trim('"', '\'');
                            if (!string.IsNullOrWhiteSpace(token))
                                return token;
                        }
                    }
                }
            }
            catch { }
        }

        // 4. Copilot auth.db SQLite database
        var dbPaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "github-copilot", "auth.db"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "github-copilot", "auth.db"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "github-copilot", "auth.db")
        };

        foreach (var dbPath in dbPaths)
        {
            if (!File.Exists(dbPath)) continue;
            try
            {
                var connStr = new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Cache = SqliteCacheMode.Shared
                }.ToString();

                using var conn = new SqliteConnection(connStr);
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT token_ciphertext FROM oauth_tokens ORDER BY last_used_at DESC LIMIT 1";
                var result = cmd.ExecuteScalar();
                if (result is byte[] blob && blob.Length > 0)
                {
                    try
                    {
                        var decrypted = ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser);
                        var tok = Encoding.UTF8.GetString(decrypted).Trim();
                        if (!string.IsNullOrWhiteSpace(tok))
                            return tok;
                    }
                    catch
                    {
                        var tok = Encoding.UTF8.GetString(blob).Trim();
                        if (!string.IsNullOrWhiteSpace(tok))
                            return tok;
                    }
                }
            }
            catch { }
        }

        // 5. Windows Credential Manager targets
        var credTargets = new[]
        {
            "git:https://github.com",
            "gh:github.com",
            "vscodevscode.github-authentication/github.auth",
            "vscode-github.login/account",
            "GitHub - https://api.github.com",
            "github-copilot",
            "Copilot",
            "github.com"
        };

        foreach (var target in credTargets)
        {
            var token = ReadCredentialManagerSecret(target);
            if (!string.IsNullOrWhiteSpace(token))
                return token;
        }

        // 6. gh auth token fallback
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = "auth token",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var stdout = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit();
                if (!string.IsNullOrWhiteSpace(stdout) && (stdout.StartsWith("gh", StringComparison.OrdinalIgnoreCase) || stdout.Length >= 20))
                    return stdout;
            }
        }
        catch { }

        return null;
    }

    public static string? GetOpenCodeKimiApiKey()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".opencode", "auth.json");
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("kimi", out var kimiObj))
            {
                if (kimiObj.TryGetProperty("api_key", out var key) && key.GetString() is { } kStr)
                    return kStr;
            }
        }
        catch { }
        return null;
    }
}

