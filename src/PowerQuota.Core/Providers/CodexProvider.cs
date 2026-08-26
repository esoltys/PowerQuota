using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using PowerQuota.Core.Models;
using PowerQuota.Core.Storage;

namespace PowerQuota.Core.Providers;

public class CodexProvider : IProviderAdapter
{
    private const string UsageEndpoint = "https://chatgpt.com/backend-api/wham/usage";
    private const string TokenEndpoint = "https://auth0.openai.com/oauth/token";

    public ProviderId Id => ProviderId.Codex;

    public async Task<UsageSnapshot> FetchAsync(AccountConfig account, WindowsCredentialVault vault, HttpClient client, CancellationToken ct = default)
    {
        var tokens = vault.GetTokens(account.Id) ?? new StoredTokens();
        if (string.IsNullOrEmpty(tokens.AccessToken))
        {
            // Check if CLI host token is available
            var hostToken = HostCliScanner.GetCodexActiveToken();
            if (!string.IsNullOrEmpty(hostToken))
            {
                tokens.AccessToken = hostToken;
            }
            else
            {
                throw new InvalidOperationException("Codex login required");
            }
        }

        var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        if (!string.IsNullOrEmpty(account.ProviderAccountId))
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", account.ProviderAccountId);
        }

        var response = await client.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException("Codex session expired or unauthorized");
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseUsage(json, account);
    }

    public Task<string?> GetSystemActiveAccountIdAsync(IReadOnlyList<AccountConfig> accounts, WindowsCredentialVault vault)
    {
        var hostToken = HostCliScanner.GetCodexActiveToken();
        if (string.IsNullOrEmpty(hostToken)) return Task.FromResult<string?>(null);

        // Find match by token or email
        var match = accounts.FirstOrDefault(a => a.Provider == ProviderId.Codex && vault.GetTokens(a.Id)?.AccessToken == hostToken);
        return Task.FromResult(match?.Id);
    }

    public static UsageSnapshot ParseUsage(string json, AccountConfig? account = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var windows = new List<UsageWindow>();

        if (root.TryGetProperty("rate_limit", out var rateLimit))
        {
            if (rateLimit.TryGetProperty("primary_window", out var pw))
            {
                var used = pw.GetProperty("used_percent").GetSingle();
                var resetEpoch = pw.GetProperty("reset_at").GetInt64();
                long? windowSec = pw.TryGetProperty("limit_window_seconds", out var lws) ? lws.GetInt64() : null;

                windows.Add(new UsageWindow
                {
                    Label = "Session",
                    UsedPercent = used,
                    ResetAt = DateTimeOffset.FromUnixTimeSeconds(resetEpoch),
                    WindowSeconds = windowSec,
                    ResetDescription = "5-hour window"
                });
            }

            if (rateLimit.TryGetProperty("secondary_window", out var sw))
            {
                var used = sw.GetProperty("used_percent").GetSingle();
                var resetEpoch = sw.GetProperty("reset_at").GetInt64();
                long? windowSec = sw.TryGetProperty("limit_window_seconds", out var lws) ? lws.GetInt64() : null;

                windows.Add(new UsageWindow
                {
                    Label = "Weekly",
                    UsedPercent = used,
                    ResetAt = DateTimeOffset.FromUnixTimeSeconds(resetEpoch),
                    WindowSeconds = windowSec,
                    ResetDescription = "Weekly window"
                });
            }
        }

        ProviderCost? cost = null;
        if (root.TryGetProperty("credits", out var credits) && credits.TryGetProperty("balance", out var bal))
        {
            double balanceVal = bal.ValueKind == JsonValueKind.Number ? bal.GetDouble() :
                (bal.ValueKind == JsonValueKind.String && double.TryParse(bal.GetString(), out var parsed) ? parsed : 0);

            cost = new ProviderCost
            {
                Used = balanceVal,
                Units = "credits"
            };
        }

        string? email = root.TryGetProperty("email", out var em) ? em.GetString() : account?.Email;
        string? plan = root.TryGetProperty("plan_type", out var pt) ? pt.GetString() : null;
        string? accountId = root.TryGetProperty("account_id", out var aid) ? aid.GetString() : account?.ProviderAccountId;

        return new UsageSnapshot
        {
            Provider = ProviderId.Codex,
            Source = "OAuth",
            UpdatedAt = DateTimeOffset.UtcNow,
            HeadlineIndex = 0,
            Windows = windows,
            ProviderCost = cost,
            Identity = new ProviderIdentity
            {
                Email = email,
                AccountId = accountId,
                Plan = plan
            }
        };
    }
}

