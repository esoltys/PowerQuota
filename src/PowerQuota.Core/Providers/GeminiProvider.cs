using System.Net.Http.Headers;
using System.Text.Json;
using PowerQuota.Core.Models;
using PowerQuota.Core.Storage;

namespace PowerQuota.Core.Providers;

public class GeminiProvider : IProviderAdapter
{
    private const string LoadCodeAssistUrl = "https://cloudaicompanion.googleapis.com/v1:loadCodeAssist";
    private const string RetrieveQuotaUrl = "https://cloudaicompanion.googleapis.com/v1/projects/{0}:retrieveUserQuota";

    public ProviderId Id => ProviderId.Gemini;

    public async Task<UsageSnapshot> FetchAsync(AccountConfig account, WindowsCredentialVault vault, HttpClient client, CancellationToken ct = default)
    {
        var tokens = vault.GetTokens(account.Id) ?? new StoredTokens();
        if (string.IsNullOrEmpty(tokens.AccessToken))
        {
            var hostToken = HostCliScanner.GetGeminiActiveToken();
            if (!string.IsNullOrEmpty(hostToken))
            {
                tokens.AccessToken = hostToken;
            }
            else
            {
                throw new InvalidOperationException("Gemini login required");
            }
        }

        string? project = null;
        string? tier = null;

        // Step 1: Load code assist to get active project and tier
        using (var loadReq = new HttpRequestMessage(HttpMethod.Post, LoadCodeAssistUrl))
        {
            loadReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
            loadReq.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

            using var loadResp = await client.SendAsync(loadReq, ct);
            if (loadResp.StatusCode == System.Net.HttpStatusCode.Unauthorized || loadResp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                throw new UnauthorizedAccessException("Gemini session expired");
            }

            loadResp.EnsureSuccessStatusCode();
            var loadJson = await loadResp.Content.ReadAsStringAsync(ct);
            using var loadDoc = JsonDocument.Parse(loadJson);

            if (loadDoc.RootElement.TryGetProperty("cloudaicompanionProject", out var cp)) project = cp.GetString();
            if (loadDoc.RootElement.TryGetProperty("currentTier", out var ctObj) && ctObj.TryGetProperty("id", out var tid)) tier = tid.GetString();
        }

        if (string.IsNullOrEmpty(project))
        {
            project = "default";
        }

        // Step 2: Retrieve quota buckets
        var quotaUrl = string.Format(RetrieveQuotaUrl, project);
        using var quotaReq = new HttpRequestMessage(HttpMethod.Post, quotaUrl);
        quotaReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        quotaReq.Content = new StringContent($"{{\"project\":\"{project}\"}}", System.Text.Encoding.UTF8, "application/json");

        using var quotaResp = await client.SendAsync(quotaReq, ct);
        if (quotaResp.StatusCode == System.Net.HttpStatusCode.Unauthorized || quotaResp.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException("Gemini session expired");
        }
        quotaResp.EnsureSuccessStatusCode();
        var quotaJson = await quotaResp.Content.ReadAsStringAsync(ct);

        return ParseUsage(quotaJson, tier, account);
    }

    public Task<string?> GetSystemActiveAccountIdAsync(IReadOnlyList<AccountConfig> accounts, WindowsCredentialVault vault)
    {
        var hostToken = HostCliScanner.GetGeminiActiveToken();
        if (string.IsNullOrEmpty(hostToken)) return Task.FromResult<string?>(null);

        var match = accounts.FirstOrDefault(a => a.Provider == ProviderId.Gemini && vault.GetTokens(a.Id)?.AccessToken == hostToken);
        return Task.FromResult(match?.Id);
    }

    public static UsageSnapshot ParseUsage(string json, string? tier, AccountConfig? account = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var windows = new List<UsageWindow>();

        if (root.TryGetProperty("buckets", out var buckets))
        {
            foreach (var bucket in buckets.EnumerateArray())
            {
                string modelId = bucket.TryGetProperty("modelId", out var mid) ? mid.GetString() ?? "" : "";
                float remainingFraction = bucket.TryGetProperty("remainingFraction", out var rf) ? rf.GetSingle() : 1f;
                float usedPercent = (1f - remainingFraction) * 100f;

                DateTimeOffset? resetTime = null;
                if (bucket.TryGetProperty("resetTime", out var rt) && DateTimeOffset.TryParse(rt.GetString(), out var parsedRt))
                {
                    resetTime = parsedRt;
                }

                string label = modelId.Contains("flash-lite", StringComparison.OrdinalIgnoreCase) ? "Lite" :
                               modelId.Contains("flash", StringComparison.OrdinalIgnoreCase) ? "Flash" :
                               modelId.Contains("pro", StringComparison.OrdinalIgnoreCase) ? "Pro" : modelId;

                windows.Add(new UsageWindow
                {
                    Label = label,
                    UsedPercent = Math.Clamp(usedPercent, 0f, 100f),
                    ResetAt = resetTime,
                    ResetDescription = $"{label} Quota"
                });
            }
        }

        string plan = tier switch
        {
            "free-tier" => "Free",
            "standard-tier" => "Standard",
            "enterprise-tier" => "Enterprise",
            _ => "Gemini Code Assist"
        };

        return new UsageSnapshot
        {
            Provider = ProviderId.Gemini,
            Source = "OAuth",
            UpdatedAt = DateTimeOffset.UtcNow,
            HeadlineIndex = 0,
            Windows = windows.Count > 0 ? windows : new List<UsageWindow> { new UsageWindow { Label = "Gemini", UsedPercent = 0f } },
            Identity = new ProviderIdentity
            {
                Email = account?.Email,
                Plan = plan
            }
        };
    }
}

