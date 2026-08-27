using System.Runtime.InteropServices;
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

                return (at, rt, exp);
            }
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

    public static string? GetGeminiActiveToken()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "auth.json");
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("access_token", out var at) && at.GetString() is { } atStr)
                return atStr;
        }
        catch { }
        return null;
    }

    public static string? GetCopilotActiveToken()
    {
        var localPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "github-copilot", "hosts.json");
        var configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "github-copilot", "hosts.json");

        foreach (var path in new[] { localPath, configPath })
        {
            if (!File.Exists(path)) continue;
            try
            {
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.TryGetProperty("oauth_token", out var ot) && ot.GetString() is { } otStr)
                        return otStr;
                }
            }
            catch { }
        }
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

