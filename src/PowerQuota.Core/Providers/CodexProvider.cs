using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using PowerQuota.Core.Models;
using PowerQuota.Core.Storage;
using PowerQuota.Core.Utilities;

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
            var (scannedAt, scannedRt, scannedExp) = HostCliScanner.ScanCodexTokens();
            if (!string.IsNullOrEmpty(scannedAt))
            {
                tokens.AccessToken = scannedAt;
                tokens.RefreshToken = scannedRt;
                tokens.ExpiresAt = scannedExp;
                vault.SaveTokens(account.Id, tokens);
            }
            else
            {
                throw new InvalidOperationException("Codex login required");
            }
        }

        // Proactive token refresh if expired or about to expire in < 2 minutes
        if (tokens.ExpiresAt.HasValue && tokens.ExpiresAt.Value <= DateTimeOffset.UtcNow.AddMinutes(2) && !string.IsNullOrEmpty(tokens.RefreshToken))
        {
            var refreshed = await RefreshTokenAsync(account.Id, tokens, vault, client, ct);
            if (refreshed != null)
            {
                tokens = refreshed;
            }
        }

        var request = CreateUsageRequest(tokens.AccessToken, account.ProviderAccountId);
        var response = await client.SendAsync(request, ct);

        // Reactive token refresh on 401 Unauthorized
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && !string.IsNullOrEmpty(tokens.RefreshToken))
        {
            var refreshed = await RefreshTokenAsync(account.Id, tokens, vault, client, ct);
            if (refreshed != null)
            {
                tokens = refreshed;
                request = CreateUsageRequest(tokens.AccessToken, account.ProviderAccountId);
                response = await client.SendAsync(request, ct);
            }
        }

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException("Codex session expired or unauthorized");
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseUsage(json, account);
    }

    private static HttpRequestMessage CreateUsageRequest(string accessToken, string? accountId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (!string.IsNullOrEmpty(accountId))
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);
        }
        return request;
    }

    private async Task<StoredTokens?> RefreshTokenAsync(string accountId, StoredTokens tokens, WindowsCredentialVault vault, HttpClient client, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(tokens.RefreshToken)) return null;

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = "pdlLIX2Y72MIlIKGarALjhRDuqitoAqJY7",
                    ["refresh_token"] = tokens.RefreshToken
                })
            };

            var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("access_token", out var atProp) && atProp.GetString() is { } newAt && !string.IsNullOrEmpty(newAt))
            {
                tokens.AccessToken = newAt;
                if (root.TryGetProperty("refresh_token", out var rtProp) && rtProp.GetString() is { } newRt && !string.IsNullOrEmpty(newRt))
                {
                    tokens.RefreshToken = newRt;
                }
                if (root.TryGetPropertyInt64("expires_in", out var expIn))
                {
                    tokens.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expIn);
                }

                vault.SaveTokens(accountId, tokens);
                return tokens;
            }
        }
        catch
        {
            // Allow caller to fallback
        }

        return null;
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
                if (ParseWindow(pw, "Session", 18000) is { } w) windows.Add(w);
            }

            if (rateLimit.TryGetProperty("secondary_window", out var sw))
            {
                if (ParseWindow(sw, "Weekly", 604800) is { } w) windows.Add(w);
            }

            // Dynamic array-based discovery under rate_limit
            if (rateLimit.TryGetProperty("windows", out var winArr) && winArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in winArr.EnumerateArray())
                {
                    if (ParseWindow(item, "Window") is { } w) windows.Add(w);
                }
            }
            else if (rateLimit.TryGetProperty("limits", out var limArr) && limArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in limArr.EnumerateArray())
                {
                    if (ParseWindow(item, "Limit") is { } w) windows.Add(w);
                }
            }

            // Multi-model objects under rate_limit (e.g. gpt-4o, o1, o3-mini)
            if (rateLimit.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in rateLimit.EnumerateObject())
                {
                    if (prop.NameEquals("primary_window") || prop.NameEquals("secondary_window") ||
                        prop.NameEquals("windows") || prop.NameEquals("limits"))
                    {
                        continue;
                    }

                    if (prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        string friendlyLabel = FormatModelLabel(prop.Name);
                        if (ParseWindow(prop.Value, friendlyLabel) is { } w)
                        {
                            windows.Add(w);
                        }
                    }
                }
            }
        }

        // Fallback to top-level windows / limits arrays if no rate_limit windows discovered
        if (windows.Count == 0)
        {
            if (root.TryGetProperty("windows", out var rootWins) && rootWins.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in rootWins.EnumerateArray())
                {
                    if (ParseWindow(item, "Window") is { } w) windows.Add(w);
                }
            }
            else if (root.TryGetProperty("limits", out var rootLimits) && rootLimits.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in rootLimits.EnumerateArray())
                {
                    if (ParseWindow(item, "Limit") is { } w) windows.Add(w);
                }
            }
        }

        ProviderCost? cost = null;
        if (root.TryGetProperty("credits", out var credits) && credits.TryGetPropertyDouble("balance", out var balanceVal))
        {
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
                Plan = FormatPlan(plan)
            }
        };
    }

    private static UsageWindow? ParseWindow(JsonElement element, string defaultLabel, long? defaultWindowSec = null)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        float usedPercent = 0f;
        if (element.TryGetPropertySingle("used_percent", out var up))
        {
            usedPercent = up;
        }
        else if (element.TryGetPropertySingle("utilization", out var ut))
        {
            usedPercent = ut;
        }
        else if (element.TryGetPropertySingle("percent", out var pct))
        {
            usedPercent = pct;
        }

        DateTimeOffset? resetAt = null;
        if (element.TryGetPropertyInt64("reset_at", out var raEpoch) && raEpoch > 0)
        {
            resetAt = DateTimeOffset.FromUnixTimeSeconds(raEpoch);
        }
        else if (element.TryGetProperty("reset_at", out var raProp) && DateTimeOffset.TryParse(raProp.GetString(), out var parsedRa))
        {
            resetAt = parsedRa;
        }
        else if (element.TryGetProperty("resets_at", out var rsaProp) && DateTimeOffset.TryParse(rsaProp.GetString(), out var parsedRsa))
        {
            resetAt = parsedRsa;
        }

        long? windowSec = null;
        if (element.TryGetPropertyInt64("limit_window_seconds", out var lws))
        {
            windowSec = lws;
        }
        else
        {
            windowSec = defaultWindowSec;
        }

        string label = defaultLabel;
        if (element.TryGetProperty("label", out var lblProp) && lblProp.GetString() is { } lStr && !string.IsNullOrWhiteSpace(lStr))
        {
            label = lStr;
        }
        else if (element.TryGetProperty("name", out var nameProp) && nameProp.GetString() is { } nStr && !string.IsNullOrWhiteSpace(nStr))
        {
            label = nStr;
        }
        else if (element.TryGetProperty("model", out var modelProp) && modelProp.GetString() is { } mStr && !string.IsNullOrWhiteSpace(mStr))
        {
            label = FormatModelLabel(mStr);
        }
        else if (defaultLabel == "Session" || defaultLabel == "Window" || defaultLabel == "Limit")
        {
            if (windowSec.HasValue)
            {
                if (windowSec.Value >= 2000000) label = "Monthly";
                else if (windowSec.Value >= 500000) label = "Weekly";
                else if (windowSec.Value >= 80000) label = "Daily";
                else label = "Session";
            }
        }

        string? desc = null;
        if (windowSec.HasValue)
        {
            if (windowSec.Value >= 2000000) desc = "Monthly window";
            else if (windowSec.Value >= 500000) desc = "Weekly window";
            else if (windowSec.Value >= 80000) desc = "Daily window";
            else if (windowSec.Value > 0) desc = $"{windowSec.Value / 3600}h window";
        }

        return new UsageWindow
        {
            Label = label,
            UsedPercent = Math.Clamp(usedPercent, 0f, 100f),
            ResetAt = resetAt,
            WindowSeconds = windowSec,
            ResetDescription = desc ?? "Rate limit"
        };
    }

    private static string FormatPlan(string? plan)
    {
        if (string.IsNullOrWhiteSpace(plan)) return "ChatGPT";
        return plan.ToLowerInvariant() switch
        {
            "free" => "ChatGPT Free",
            "plus" => "ChatGPT Plus",
            "pro" => "ChatGPT Pro",
            "team" => "ChatGPT Team",
            "enterprise" => "ChatGPT Enterprise",
            _ => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(plan.Replace("_", " ").Replace("-", " "))
        };
    }

    private static string FormatModelLabel(string key)
    {
        return key switch
        {
            "gpt_4o" or "gpt-4o" => "GPT-4o",
            "gpt_4" or "gpt-4" => "GPT-4",
            "o1" => "o1",
            "o1_preview" or "o1-preview" => "o1-preview",
            "o1_mini" or "o1-mini" => "o1-mini",
            "o3_mini" or "o3-mini" => "o3-mini",
            _ => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(key.Replace("_", " ").Replace("-", " "))
        };
    }
}
