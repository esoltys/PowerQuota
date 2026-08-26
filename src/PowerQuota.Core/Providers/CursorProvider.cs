using System.Net.Http.Headers;
using System.Text.Json;
using PowerQuota.Core.Models;
using PowerQuota.Core.Storage;

namespace PowerQuota.Core.Providers;

public class CursorProvider : IProviderAdapter
{
    private const string UsageEndpoint = "https://www.cursor.com/api/usage";

    public ProviderId Id => ProviderId.Cursor;

    public async Task<UsageSnapshot> FetchAsync(AccountConfig account, WindowsCredentialVault vault, HttpClient client, CancellationToken ct = default)
    {
        var tokens = vault.GetTokens(account.Id) ?? new StoredTokens();
        if (string.IsNullOrEmpty(tokens.AccessToken))
        {
            var (scannedAt, scannedRt) = HostCliScanner.ScanCursorIdeTokens();
            if (!string.IsNullOrEmpty(scannedAt))
            {
                tokens.AccessToken = scannedAt;
                tokens.RefreshToken = scannedRt;
                vault.SaveTokens(account.Id, tokens);
            }
            else
            {
                throw new InvalidOperationException("Cursor token not found in local IDE state");
            }
        }

        var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        request.Headers.Add("Cookie", $"WorkosCursorSessionToken={tokens.AccessToken}");

        var response = await client.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException("Cursor session expired");
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseUsage(json, account);
    }

    public Task<string?> GetSystemActiveAccountIdAsync(IReadOnlyList<AccountConfig> accounts, WindowsCredentialVault vault)
    {
        var (activeAt, _) = HostCliScanner.ScanCursorIdeTokens();
        if (string.IsNullOrEmpty(activeAt)) return Task.FromResult<string?>(null);

        var match = accounts.FirstOrDefault(a => a.Provider == ProviderId.Cursor && vault.GetTokens(a.Id)?.AccessToken == activeAt);
        return Task.FromResult(match?.Id);
    }

    public static UsageSnapshot ParseUsage(string json, AccountConfig? account = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var windows = new List<UsageWindow>();

        // 1. Fast/Composer requests (GPT-4 / Claude / Composer usage)
        if (root.TryGetProperty("gpt4", out var gpt4))
        {
            int numRequests = gpt4.TryGetProperty("numRequests", out var nr) ? nr.GetInt32() : 0;
            int maxRequests = gpt4.TryGetProperty("maxRequestUsage", out var mr) && mr.GetInt32() > 0 ? mr.GetInt32() : 500;
            float usedPct = maxRequests > 0 ? ((float)numRequests / maxRequests * 100f) : 0f;

            windows.Add(new UsageWindow
            {
                Label = "Fast / Composer",
                UsedPercent = Math.Clamp(usedPct, 0f, 100f),
                ResetDescription = $"{numRequests} / {maxRequests} requests"
            });
        }

        // 2. Total requests (if available)
        if (root.TryGetProperty("startOfMonth", out var somStr) && DateTimeOffset.TryParse(somStr.GetString(), out var som))
        {
            var nextMonth = som.AddMonths(1);
            if (windows.Count > 0)
            {
                windows[0].ResetAt = nextMonth;
            }
        }

        // 3. User plan / email
        string? email = account?.Email;
        string? plan = "Pro";

        return new UsageSnapshot
        {
            Provider = ProviderId.Cursor,
            Source = "Local IDE Scan",
            UpdatedAt = DateTimeOffset.UtcNow,
            HeadlineIndex = 0,
            Windows = windows.Count > 0 ? windows : new List<UsageWindow>
            {
                new UsageWindow { Label = "Usage", UsedPercent = 0f, ResetDescription = "Active" }
            },
            Identity = new ProviderIdentity
            {
                Email = email,
                Plan = plan
            }
        };
    }
}

