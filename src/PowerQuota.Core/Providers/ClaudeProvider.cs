using System.Net.Http.Headers;
using System.Text.Json;
using PowerQuota.Core.Models;
using PowerQuota.Core.Storage;

namespace PowerQuota.Core.Providers;

public class ClaudeProvider : IProviderAdapter
{
    private const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";

    public ProviderId Id => ProviderId.Claude;

    public async Task<UsageSnapshot> FetchAsync(AccountConfig account, WindowsCredentialVault vault, HttpClient client, CancellationToken ct = default)
    {
        var tokens = vault.GetTokens(account.Id) ?? new StoredTokens();
        if (string.IsNullOrEmpty(tokens.AccessToken))
        {
            var hostToken = HostCliScanner.GetClaudeActiveToken();
            if (!string.IsNullOrEmpty(hostToken))
            {
                tokens.AccessToken = hostToken;
            }
            else
            {
                throw new InvalidOperationException("Claude login required");
            }
        }

        var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

        var response = await client.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException("Claude session expired");
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseUsage(json, account);
    }

    public Task<string?> GetSystemActiveAccountIdAsync(IReadOnlyList<AccountConfig> accounts, WindowsCredentialVault vault)
    {
        var hostToken = HostCliScanner.GetClaudeActiveToken();
        if (string.IsNullOrEmpty(hostToken)) return Task.FromResult<string?>(null);

        var match = accounts.FirstOrDefault(a => a.Provider == ProviderId.Claude && vault.GetTokens(a.Id)?.AccessToken == hostToken);
        return Task.FromResult(match?.Id);
    }

    public static UsageSnapshot ParseUsage(string json, AccountConfig? account = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var windows = new List<UsageWindow>();

        if (root.ValueKind == JsonValueKind.Array)
        {
            ParseLimitsArray(root, windows);
        }
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("limits", out var limitsArray))
        {
            ParseLimitsArray(limitsArray, windows);
        }

        // Extra usage / spend limits if present
        ExtraUsageState? extraUsage = null;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("extra_usage", out var eu))
        {
            var active = eu.TryGetProperty("is_active", out var act) && act.GetBoolean();
            var usedPct = eu.TryGetProperty("used_percent", out var up) ? up.GetSingle() : 0f;
            extraUsage = new ExtraUsageState
            {
                IsActive = active,
                UsedPercent = usedPct
            };
        }

        return new UsageSnapshot
        {
            Provider = ProviderId.Claude,
            Source = "OAuth",
            UpdatedAt = DateTimeOffset.UtcNow,
            HeadlineIndex = 0,
            Windows = windows,
            ExtraUsage = extraUsage,
            Identity = new ProviderIdentity
            {
                Email = account?.Email,
                Plan = "Claude Code"
            }
        };
    }

    private static void ParseLimitsArray(JsonElement array, List<UsageWindow> windows)
    {
        var seen = new HashSet<string>();
        foreach (var item in array.EnumerateArray())
        {
            string? group = item.TryGetProperty("group", out var g) ? g.GetString() : null;
            string? kind = item.TryGetProperty("kind", out var k) ? k.GetString() : null;
            float? percent = item.TryGetProperty("percent", out var p) ? p.GetSingle() : null;
            string? resetsAtStr = item.TryGetProperty("resets_at", out var ra) ? ra.GetString() : null;

            DateTimeOffset? resetAt = null;
            if (resetsAtStr != null && DateTimeOffset.TryParse(resetsAtStr, out var parsedDt))
            {
                resetAt = parsedDt;
            }

            if (group == "session" || kind == "five_hour")
            {
                windows.Add(new UsageWindow
                {
                    Label = "Session",
                    UsedPercent = percent ?? 0f,
                    ResetAt = resetAt,
                    WindowSeconds = 5 * 3600,
                    ResetDescription = "5-hour session window"
                });
            }
            else if (group == "weekly")
            {
                string label = "Weekly";
                if (item.TryGetProperty("scope", out var scope) && scope.TryGetProperty("model", out var model))
                {
                    if (model.TryGetProperty("display_name", out var dn) && dn.GetString() is { } name && !string.IsNullOrEmpty(name))
                    {
                        label = name;
                    }
                }

                if (seen.Add(label))
                {
                    windows.Add(new UsageWindow
                    {
                        Label = label,
                        UsedPercent = percent ?? 0f,
                        ResetAt = resetAt,
                        WindowSeconds = 7 * 24 * 3600,
                        ResetDescription = "Weekly quota"
                    });
                }
            }
        }
    }
}

