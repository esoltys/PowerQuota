using System.Net.Http.Headers;
using System.Text.Json;
using PowerQuota.Core.Models;
using PowerQuota.Core.Storage;
using PowerQuota.Core.Utilities;

namespace PowerQuota.Core.Providers;

public class MinimaxProvider : IProviderAdapter
{
    private const string UsageEndpoint = "https://www.minimax.io/v1/token_plan/remains";

    public ProviderId Id => ProviderId.Minimax;

    public async Task<UsageSnapshot> FetchAsync(AccountConfig account, WindowsCredentialVault vault, HttpClient client, CancellationToken ct = default)
    {
        var apiKey = vault.GetApiKey(account.Id) ?? Environment.GetEnvironmentVariable("MINIMAX_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException("Minimax API Key required");
        }

        var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await client.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException("Invalid Minimax API key");
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseUsage(json, account);
    }

    public Task<string?> GetSystemActiveAccountIdAsync(IReadOnlyList<AccountConfig> accounts, WindowsCredentialVault vault)
    {
        return Task.FromResult<string?>(null);
    }

    public static UsageSnapshot ParseUsage(string json, AccountConfig? account = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var windows = new List<UsageWindow>();

        if (root.TryGetProperty("model_remains", out var remains) && remains.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in remains.EnumerateArray())
            {
                string modelName = item.TryGetProperty("model_name", out var mn) ? mn.GetString() ?? "general" : "general";

                if (item.TryGetPropertySingle("current_interval_remaining_percent", out var remPct))
                {
                    float used = 100f - remPct;
                    windows.Add(new UsageWindow
                    {
                        Label = $"{modelName} (5h)",
                        UsedPercent = Math.Clamp(used, 0f, 100f),
                        WindowSeconds = 5 * 3600,
                        ResetDescription = "5-hour rate window"
                    });
                }

                if (item.TryGetPropertySingle("current_weekly_remaining_percent", out var wkPct))
                {
                    float used = 100f - wkPct;
                    windows.Add(new UsageWindow
                    {
                        Label = $"{modelName} (Weekly)",
                        UsedPercent = Math.Clamp(used, 0f, 100f),
                        WindowSeconds = 7 * 24 * 3600,
                        ResetDescription = "Weekly token quota"
                    });
                }
            }
        }

        return new UsageSnapshot
        {
            Provider = ProviderId.Minimax,
            Source = "API Key",
            UpdatedAt = DateTimeOffset.UtcNow,
            HeadlineIndex = 0,
            Windows = windows.Count > 0 ? windows : new List<UsageWindow> { new UsageWindow { Label = "Minimax", UsedPercent = 0f } },
            Identity = new ProviderIdentity
            {
                Plan = "Minimax Plan"
            }
        };
    }
}

