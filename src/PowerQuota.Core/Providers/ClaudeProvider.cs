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

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

        using var response = await client.SendAsync(request, ct);
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
        else if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("limits", out var limitsArray) && limitsArray.ValueKind == JsonValueKind.Array)
            {
                ParseLimitsArray(limitsArray, windows);
            }

            // Fallback to top-level five_hour and seven_day objects if limits array was empty or missing
            if (windows.Count == 0)
            {
                if (root.TryGetProperty("five_hour", out var fh) && fh.ValueKind == JsonValueKind.Object)
                {
                    float util = 0f;
                    if (fh.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number)
                    {
                        u.TryGetSingle(out util);
                    }
                    DateTimeOffset? resetAt = null;
                    if (fh.TryGetProperty("resets_at", out var ra) && ra.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(ra.GetString(), out var dt))
                    {
                        resetAt = dt;
                    }

                    windows.Add(new UsageWindow
                    {
                        Label = "Session",
                        UsedPercent = util,
                        ResetAt = resetAt,
                        WindowSeconds = 5 * 3600,
                        ResetDescription = "5-hour session window"
                    });
                }

                if (root.TryGetProperty("seven_day", out var sd) && sd.ValueKind == JsonValueKind.Object)
                {
                    float util = 0f;
                    if (sd.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number)
                    {
                        u.TryGetSingle(out util);
                    }
                    DateTimeOffset? resetAt = null;
                    if (sd.TryGetProperty("resets_at", out var ra) && ra.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(ra.GetString(), out var dt))
                    {
                        resetAt = dt;
                    }

                    windows.Add(new UsageWindow
                    {
                        Label = "Weekly",
                        UsedPercent = util,
                        ResetAt = resetAt,
                        WindowSeconds = 7 * 24 * 3600,
                        ResetDescription = "Weekly quota"
                    });
                }
            }
        }

        // The API doesn't guarantee the "all models" aggregate entry comes first —
        // a per-model entry can precede it. HeadlineIndex assumes index 0 is the
        // aggregate, so make sure it actually is when one is present.
        PromoteAggregateWindowsToFront(windows);

        // Extra usage / spend limits if present
        ExtraUsageState? extraUsage = null;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("extra_usage", out var eu) && eu.ValueKind == JsonValueKind.Object)
        {
            var isEnabled = eu.TryGetProperty("is_enabled", out var act) && act.ValueKind == JsonValueKind.True;
            float usedCredits = 0f;
            if (eu.TryGetProperty("used_credits", out var uc) && uc.ValueKind == JsonValueKind.Number)
            {
                uc.TryGetSingle(out usedCredits);
            }
            extraUsage = new ExtraUsageState
            {
                IsActive = isEnabled,
                UsedPercent = usedCredits
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

    private static void PromoteAggregateWindowsToFront(List<UsageWindow> windows)
    {
        windows.Sort((a, b) =>
        {
            int Rank(UsageWindow w) => w.Label switch
            {
                "Session" => 0,
                "Weekly" => 1,
                _ => 2
            };
            return Rank(a).CompareTo(Rank(b));
        });
    }

    private static void ParseLimitsArray(JsonElement array, List<UsageWindow> windows)
    {
        var seen = new HashSet<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            string? group = item.TryGetProperty("group", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : null;
            string? kind = item.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;

            float percent = 0f;
            if (item.TryGetProperty("percent", out var p) && p.ValueKind == JsonValueKind.Number)
            {
                p.TryGetSingle(out percent);
            }

            string? resetsAtStr = item.TryGetProperty("resets_at", out var ra) && ra.ValueKind == JsonValueKind.String ? ra.GetString() : null;
            DateTimeOffset? resetAt = null;
            if (resetsAtStr != null && DateTimeOffset.TryParse(resetsAtStr, out var parsedDt))
            {
                resetAt = parsedDt;
            }

            string? modelName = null;
            if (item.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object)
            {
                if (scope.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object)
                {
                    if (model.TryGetProperty("display_name", out var dn) && dn.ValueKind == JsonValueKind.String && dn.GetString() is { } name && !string.IsNullOrEmpty(name))
                    {
                        modelName = name;
                    }
                }
            }

            if (group == "session" || kind == "five_hour" || kind == "session")
            {
                // An unscoped entry is the "all models" aggregate limit shown by the
                // official app. Scoped (per-model) entries are additional, separate
                // limits — label them distinctly so they don't clobber the aggregate
                // or collide with a same-named weekly entry.
                string label = modelName is null ? "Session" : $"{modelName} (Session)";

                if (seen.Add(label))
                {
                    windows.Add(new UsageWindow
                    {
                        Label = label,
                        UsedPercent = percent,
                        ResetAt = resetAt,
                        WindowSeconds = 5 * 3600,
                        ResetDescription = "5-hour session window"
                    });
                }
            }
            else if (group == "weekly" || kind?.StartsWith("seven_day") == true || kind?.StartsWith("weekly") == true)
            {
                string label = modelName ?? "Weekly";

                if (seen.Add(label))
                {
                    windows.Add(new UsageWindow
                    {
                        Label = label,
                        UsedPercent = percent,
                        ResetAt = resetAt,
                        WindowSeconds = 7 * 24 * 3600,
                        ResetDescription = "Weekly quota"
                    });
                }
            }
        }
    }
}
