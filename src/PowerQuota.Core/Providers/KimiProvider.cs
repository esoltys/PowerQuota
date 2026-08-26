using System.Net.Http.Headers;
using System.Text.Json;
using PowerQuota.Core.Models;
using PowerQuota.Core.Storage;

namespace PowerQuota.Core.Providers;

public class KimiProvider : IProviderAdapter
{
    private const string UsageEndpoint = "https://api.kimi.com/coding/v1/usages";

    public ProviderId Id => ProviderId.Kimi;

    public async Task<UsageSnapshot> FetchAsync(AccountConfig account, WindowsCredentialVault vault, HttpClient client, CancellationToken ct = default)
    {
        var apiKey = vault.GetApiKey(account.Id) ??
                     HostCliScanner.GetOpenCodeKimiApiKey() ??
                     Environment.GetEnvironmentVariable("KIMI_API_KEY");

        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException("Kimi API Key required");
        }

        var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await client.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException("Invalid Kimi API key");
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseUsage(json, account);
    }

    public Task<string?> GetSystemActiveAccountIdAsync(IReadOnlyList<AccountConfig> accounts, WindowsCredentialVault vault)
    {
        var openCodeKey = HostCliScanner.GetOpenCodeKimiApiKey();
        if (string.IsNullOrEmpty(openCodeKey)) return Task.FromResult<string?>(null);

        var match = accounts.FirstOrDefault(a => a.Provider == ProviderId.Kimi && vault.GetApiKey(a.Id) == openCodeKey);
        return Task.FromResult(match?.Id);
    }

    public static UsageSnapshot ParseUsage(string json, AccountConfig? account = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var windows = new List<UsageWindow>();

        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("limit", out var limProp) &&
                double.TryParse(limProp.GetString(), out var limit) && limit > 0)
            {
                double used = usage.TryGetProperty("used", out var u) && double.TryParse(u.GetString(), out var uVal) ? uVal : 0;
                float usedPct = (float)(used / limit * 100.0);

                DateTimeOffset? resetAt = null;
                if (usage.TryGetProperty("resetTime", out var rt) && DateTimeOffset.TryParse(rt.GetString(), out var parsedRt))
                {
                    resetAt = parsedRt;
                }

                windows.Add(new UsageWindow
                {
                    Label = "Weekly",
                    UsedPercent = Math.Clamp(usedPct, 0f, 100f),
                    ResetAt = resetAt,
                    ResetDescription = $"{used} / {limit} used"
                });
            }
        }

        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var lim in limits.EnumerateArray())
            {
                if (lim.TryGetProperty("detail", out var detail) &&
                    detail.TryGetProperty("limit", out var lProp) &&
                    double.TryParse(lProp.GetString(), out var limit) && limit > 0)
                {
                    double used = detail.TryGetProperty("used", out var u) && double.TryParse(u.GetString(), out var uVal) ? uVal : 0;
                    float usedPct = (float)(used / limit * 100.0);

                    long durationMin = lim.TryGetProperty("window", out var win) && win.TryGetProperty("duration", out var dur) ? dur.GetInt64() : 300;

                    windows.Add(new UsageWindow
                    {
                        Label = $"Rate Limit ({durationMin}m)",
                        UsedPercent = Math.Clamp(usedPct, 0f, 100f),
                        WindowSeconds = durationMin * 60,
                        ResetDescription = $"Rate limit window"
                    });
                }
            }
        }

        string? plan = root.TryGetProperty("user", out var user) &&
                       user.TryGetProperty("membership", out var mem) &&
                       mem.TryGetProperty("level", out var lvl) ? lvl.GetString() : "Kimi for Coding";

        return new UsageSnapshot
        {
            Provider = ProviderId.Kimi,
            Source = "API Key",
            UpdatedAt = DateTimeOffset.UtcNow,
            HeadlineIndex = 0,
            Windows = windows.Count > 0 ? windows : new List<UsageWindow> { new UsageWindow { Label = "Kimi", UsedPercent = 0f } },
            Identity = new ProviderIdentity
            {
                Plan = plan
            }
        };
    }
}

