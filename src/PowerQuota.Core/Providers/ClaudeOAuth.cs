using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PowerQuota.Core.Storage;

namespace PowerQuota.Core.Providers;

public sealed record ClaudeLoginResult(StoredTokens Tokens, string? Email);

/// <summary>
/// PowerQuota's own Claude OAuth session: an authorization-code + PKCE browser login with a loopback
/// callback, and refresh-token rotation. The tokens this issues belong to PowerQuota alone, so refreshing
/// them never touches (or revokes) the Claude Code CLI or Claude Desktop sessions on the same machine.
/// </summary>
public static class ClaudeOAuth
{
    public const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    internal const string AuthorizeEndpoint = "https://claude.ai/oauth/authorize";
    internal const string Scopes = "user:profile user:inference";
    internal const string CallbackPath = "/callback";

    // Claude Code currently uses platform.claude.com; console.anthropic.com is the legacy host of the same API.
    internal static readonly string[] TokenEndpoints =
    {
        "https://platform.claude.com/v1/oauth/token",
        "https://console.anthropic.com/v1/oauth/token"
    };

    internal static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Runs the interactive browser login: opens the Claude authorize page via <paramref name="openBrowser"/>,
    /// waits (up to five minutes) for the redirect to a one-shot localhost listener, then exchanges the code.
    /// </summary>
    public static async Task<ClaudeLoginResult> LoginAsync(HttpClient client, Action<Uri> openBrowser, CancellationToken ct = default)
    {
        var verifier = CreateRandomToken();
        var challenge = CreateCodeChallenge(verifier);
        var state = CreateRandomToken();

        // Bind loopback only (no firewall prompt, no http.sys URL ACL). Browsers may resolve "localhost" to
        // either ::1 or 127.0.0.1, so also listen on ::1 at the same port when IPv6 is available.
        var listeners = new List<TcpListener>();
        var v4 = new TcpListener(IPAddress.Loopback, 0);
        v4.Start();
        listeners.Add(v4);
        int port = ((IPEndPoint)v4.LocalEndpoint).Port;
        try
        {
            var v6 = new TcpListener(IPAddress.IPv6Loopback, port);
            v6.Start();
            listeners.Add(v6);
        }
        catch (SocketException)
        {
        }

        try
        {
            var redirectUri = $"http://localhost:{port}{CallbackPath}";
            openBrowser(BuildAuthorizeUrl(challenge, state, redirectUri));

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(LoginTimeout);
            string code;
            try
            {
                code = await WaitForAuthorizationCodeAsync(listeners, state, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("Claude login timed out waiting for the browser.");
            }

            return await ExchangeCodeAsync(client, code, state, verifier, redirectUri, ct);
        }
        finally
        {
            foreach (var listener in listeners) listener.Stop();
        }
    }

    /// <summary>
    /// Exchanges the refresh token for a new access token. Returns null when the session has been revoked or
    /// the refresh token is no longer valid (the user must log in again); network failures and server errors
    /// propagate so callers can treat them as transient.
    /// </summary>
    public static async Task<StoredTokens?> RefreshAsync(HttpClient client, StoredTokens tokens, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tokens.RefreshToken)) return null;

        var (status, json) = await PostTokenRequestAsync(client, new
        {
            grant_type = "refresh_token",
            refresh_token = tokens.RefreshToken,
            client_id = ClientId
        }, ct);

        if (json == null)
        {
            if (IsDefinitiveAuthFailure(status)) return null;
            throw new HttpRequestException($"Claude token refresh failed ({(int)status})", null, status);
        }

        try
        {
            var refreshed = ParseTokenResponse(json).Tokens;
            // Refresh responses normally rotate the refresh token; keep the old one if this one didn't.
            refreshed.RefreshToken ??= tokens.RefreshToken;
            refreshed.ExpiresAt ??= tokens.ExpiresAt;
            return refreshed;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    internal static Uri BuildAuthorizeUrl(string codeChallenge, string state, string redirectUri)
    {
        var query = string.Join("&", new[]
        {
            "code=true",
            $"client_id={Uri.EscapeDataString(ClientId)}",
            "response_type=code",
            $"redirect_uri={Uri.EscapeDataString(redirectUri)}",
            $"scope={Uri.EscapeDataString(Scopes)}",
            $"code_challenge={Uri.EscapeDataString(codeChallenge)}",
            "code_challenge_method=S256",
            $"state={Uri.EscapeDataString(state)}"
        });
        return new Uri($"{AuthorizeEndpoint}?{query}");
    }

    internal enum CallbackKind { NotCallback, Code, Error, StateMismatch }

    /// <summary>
    /// Interprets the HTTP request line the browser sends to the loopback listener
    /// (e.g. "GET /callback?code=…&amp;state=… HTTP/1.1").
    /// </summary>
    internal static (CallbackKind Kind, string? Value) ParseCallbackRequestLine(string? requestLine, string expectedState)
    {
        var parts = requestLine?.Split(' ');
        if (parts is not { Length: >= 2 } || parts[0] != "GET") return (CallbackKind.NotCallback, null);

        var target = parts[1];
        var queryStart = target.IndexOf('?');
        var path = queryStart >= 0 ? target[..queryStart] : target;
        if (!string.Equals(path, CallbackPath, StringComparison.Ordinal)) return (CallbackKind.NotCallback, null);

        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        if (queryStart >= 0)
        {
            foreach (var pair in target[(queryStart + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var key = Uri.UnescapeDataString((eq >= 0 ? pair[..eq] : pair).Replace('+', ' '));
                var value = eq >= 0 ? Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')) : string.Empty;
                query[key] = value;
            }
        }

        if (!query.TryGetValue("state", out var state) || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(expectedState)))
        {
            return (CallbackKind.StateMismatch, null);
        }

        if (query.TryGetValue("error", out var error))
        {
            var description = query.TryGetValue("error_description", out var desc) && !string.IsNullOrEmpty(desc) ? desc : error;
            return (CallbackKind.Error, description);
        }

        return query.TryGetValue("code", out var code) && !string.IsNullOrEmpty(code)
            ? (CallbackKind.Code, code)
            : (CallbackKind.Error, "No authorization code in callback");
    }

    internal static async Task<ClaudeLoginResult> ExchangeCodeAsync(HttpClient client, string code, string state, string codeVerifier, string redirectUri, CancellationToken ct)
    {
        var (status, json) = await PostTokenRequestAsync(client, new
        {
            grant_type = "authorization_code",
            code,
            state,
            client_id = ClientId,
            redirect_uri = redirectUri,
            code_verifier = codeVerifier
        }, ct);

        if (json == null)
        {
            throw new HttpRequestException($"Claude token exchange failed ({(int)status})", null, status);
        }

        var result = ParseTokenResponse(json);
        if (string.IsNullOrEmpty(result.Tokens.RefreshToken))
        {
            throw new InvalidOperationException("Claude token exchange returned no refresh token");
        }
        return result;
    }

    internal static ClaudeLoginResult ParseTokenResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("access_token", out var atProp) || atProp.GetString() is not { Length: > 0 } accessToken)
        {
            throw new InvalidOperationException("Claude token response has no access_token");
        }

        var tokens = new StoredTokens { AccessToken = accessToken };
        if (root.TryGetProperty("refresh_token", out var rtProp) && rtProp.GetString() is { Length: > 0 } refreshToken)
        {
            tokens.RefreshToken = refreshToken;
        }
        if (root.TryGetProperty("expires_in", out var expIn) && expIn.ValueKind == JsonValueKind.Number && expIn.TryGetInt64(out var expInSec) && expInSec > 0)
        {
            tokens.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expInSec);
        }

        string? email = null;
        if (root.TryGetProperty("account", out var account) && account.ValueKind == JsonValueKind.Object &&
            account.TryGetProperty("email_address", out var emailProp) && emailProp.ValueKind == JsonValueKind.String)
        {
            email = emailProp.GetString();
        }

        return new ClaudeLoginResult(tokens, email);
    }

    /// <summary>
    /// POSTs to the token endpoint, falling back to the legacy host when the primary one is unreachable or
    /// doesn't serve the route. Returns the body on success, or null with the final status code.
    /// </summary>
    private static async Task<(HttpStatusCode Status, string? Json)> PostTokenRequestAsync(HttpClient client, object payload, CancellationToken ct)
    {
        HttpStatusCode lastStatus = HttpStatusCode.ServiceUnavailable;
        for (int i = 0; i < TokenEndpoints.Length; i++)
        {
            bool isLast = i == TokenEndpoints.Length - 1;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoints[i])
                {
                    Content = JsonContent.Create(payload)
                };
                using var response = await client.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                {
                    return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
                }

                lastStatus = response.StatusCode;
                bool tryNext = response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed || (int)response.StatusCode >= 500;
                if (!tryNext || isLast) return (lastStatus, null);
            }
            catch (HttpRequestException) when (!isLast)
            {
            }
        }
        return (lastStatus, null);
    }

    private static bool IsDefinitiveAuthFailure(HttpStatusCode status) =>
        status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private static async Task<string> WaitForAuthorizationCodeAsync(List<TcpListener> listeners, string expectedState, CancellationToken ct)
    {
        var pending = listeners.ToDictionary(l => l.AcceptTcpClientAsync(ct).AsTask(), l => l);
        while (true)
        {
            var completed = await Task.WhenAny(pending.Keys);
            var listener = pending[completed];
            pending.Remove(completed);

            using var tcp = await completed;
            pending[listener.AcceptTcpClientAsync(ct).AsTask()] = listener;

            var (kind, value) = await HandleCallbackConnectionAsync(tcp, expectedState, ct);
            switch (kind)
            {
                case CallbackKind.Code:
                    return value!;
                case CallbackKind.Error:
                    throw new InvalidOperationException($"Claude login failed: {value}");
            }
            // NotCallback (favicon etc.) or a request with a forged state: keep waiting for the real redirect.
        }
    }

    private static async Task<(CallbackKind Kind, string? Value)> HandleCallbackConnectionAsync(TcpClient tcp, string expectedState, CancellationToken ct)
    {
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(TimeSpan.FromSeconds(10));
        var stream = tcp.GetStream();

        (CallbackKind Kind, string? Value) result;
        try
        {
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(readCts.Token);
            result = ParseCallbackRequestLine(requestLine, expectedState);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return (CallbackKind.NotCallback, null);
        }

        var (statusLine, message) = result.Kind switch
        {
            CallbackKind.Code => ("200 OK", "PowerQuota is now signed in to Claude. You can close this tab."),
            CallbackKind.Error => ("400 Bad Request", $"Claude login failed: {WebUtility.HtmlEncode(result.Value)}"),
            CallbackKind.StateMismatch => ("400 Bad Request", "Invalid login request."),
            _ => ("404 Not Found", "Not found.")
        };
        var body = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>PowerQuota</title></head>" +
                   $"<body style=\"font-family:Segoe UI,sans-serif;margin:3em\"><p>{message}</p></body></html>";
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var header = $"HTTP/1.1 {statusLine}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), readCts.Token);
            await stream.WriteAsync(bodyBytes, readCts.Token);
            await stream.FlushAsync(readCts.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
        }
        return result;
    }

    private static string CreateRandomToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    internal static string CreateCodeChallenge(string codeVerifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
