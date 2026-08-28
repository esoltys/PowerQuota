using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using PowerQuota.Core.Models;
using PowerQuota.Core.Storage;
using PowerQuota.Core.Utilities;

namespace PowerQuota.Core.Providers;

public class CopilotProvider : IProviderAdapter
{
    private const string UsageEndpoint = "https://api.github.com/copilot_internal/user";

    public ProviderId Id => ProviderId.Copilot;

    public async Task<UsageSnapshot> FetchAsync(AccountConfig account, WindowsCredentialVault vault, HttpClient client, CancellationToken ct = default)
    {
        var tokens = vault.GetTokens(account.Id) ?? new StoredTokens();
        if (string.IsNullOrEmpty(tokens.AccessToken))
        {
            var hostToken = HostCliScanner.GetCopilotActiveToken();
            if (!string.IsNullOrEmpty(hostToken))
            {
                tokens.AccessToken = hostToken;
            }
            else
            {
                throw new InvalidOperationException("GitHub Copilot token required");
            }
        }

        async Task<(System.Net.HttpStatusCode StatusCode, string? Json)> SendCopilotRequestAsync(string editorVer, string pluginVer, bool allowFallbackStatus)
        {
            using var request = CreateCopilotRequest(tokens.AccessToken, editorVer, pluginVer);
            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                throw new UnauthorizedAccessException("GitHub Copilot session expired");
            }

            if (allowFallbackStatus && (response.StatusCode == System.Net.HttpStatusCode.BadRequest || response.StatusCode == System.Net.HttpStatusCode.UpgradeRequired))
            {
                return (response.StatusCode, null);
            }

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            return (response.StatusCode, json);
        }

        var (statusCode, json) = await SendCopilotRequestAsync("vscode/1.107.0", "copilot-chat/0.35.0", allowFallbackStatus: true);

        // Header fallback if rejected with client error
        if (json == null && (statusCode == System.Net.HttpStatusCode.BadRequest || statusCode == System.Net.HttpStatusCode.UpgradeRequired))
        {
            (_, json) = await SendCopilotRequestAsync("vscode/1.108.0", "copilot-chat/0.36.0", allowFallbackStatus: false);
        }

        return ParseUsage(json!, account);
    }

    private static HttpRequestMessage CreateCopilotRequest(string token, string editorVersion, string pluginVersion)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("token", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("Editor-Version", editorVersion);
        request.Headers.Add("Editor-Plugin-Version", pluginVersion);
        var pluginVerNum = pluginVersion.Contains('/') ? pluginVersion.Split('/')[1] : pluginVersion;
        request.Headers.Add("User-Agent", $"GitHubCopilotChat/{pluginVerNum}");
        request.Headers.Add("X-Github-Api-Version", "2026-03-10");
        return request;
    }

    public Task<string?> GetSystemActiveAccountIdAsync(IReadOnlyList<AccountConfig> accounts, WindowsCredentialVault vault)
    {
        var hostToken = HostCliScanner.GetCopilotActiveToken();
        if (string.IsNullOrEmpty(hostToken)) return Task.FromResult<string?>(null);

        var match = accounts.FirstOrDefault(a => a.Provider == ProviderId.Copilot && vault.GetTokens(a.Id)?.AccessToken == hostToken);
        return Task.FromResult(match?.Id);
    }

    public static UsageSnapshot ParseUsage(string json, AccountConfig? account = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var windows = new List<UsageWindow>();

        DateTimeOffset? resetDate = null;
        if (root.TryGetProperty("quota_reset_date_utc", out var rd) && DateTimeOffset.TryParse(rd.GetString(), out var parsedRd))
        {
            resetDate = parsedRd;
        }

        if (root.TryGetProperty("quota_snapshots", out var snapshots))
        {
            if (snapshots.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in snapshots.EnumerateObject())
                {
                    var snapshotObj = prop.Value;
                    if (snapshotObj.ValueKind != JsonValueKind.Object) continue;

                    string rawKey = prop.Name;
                    string label = FormatSnapshotLabel(rawKey);

                    snapshotObj.TryGetPropertyInt32("entitlement", out var entitlement);
                    snapshotObj.TryGetPropertyInt32("remaining", out var remaining);

                    // Skip inactive quota items where user has no quota/entitlement
                    if (snapshotObj.TryGetProperty("has_quota", out var hq) && !hq.GetBoolean() && entitlement == 0 && remaining == 0)
                    {
                        continue;
                    }

                    float usedPct = 0f;
                    if (entitlement > 0)
                    {
                        usedPct = (float)(entitlement - remaining) / entitlement * 100f;
                    }
                    else if (snapshotObj.TryGetPropertySingle("percent_remaining", out var pr))
                    {
                        usedPct = 100f - pr;
                    }
                    else if (snapshotObj.TryGetPropertySingle("used_percent", out var up))
                    {
                        usedPct = up;
                    }

                    string unit = rawKey.Contains("completion", StringComparison.OrdinalIgnoreCase) ? "completions"
                        : rawKey.Contains("chat", StringComparison.OrdinalIgnoreCase) ? "messages"
                        : "requests";

                    string desc = entitlement > 0
                        ? $"{entitlement - remaining} / {entitlement} {unit}"
                        : $"{remaining} {unit} remaining";

                    windows.Add(new UsageWindow
                    {
                        Label = label,
                        UsedPercent = Math.Clamp(usedPct, 0f, 100f),
                        ResetAt = resetDate,
                        ResetDescription = desc
                    });
                }
            }
        }

        string? login = root.TryGetProperty("login", out var l) ? l.GetString() : null;
        string? sku = root.TryGetProperty("access_type_sku", out var s) ? s.GetString() : null;
        if (string.IsNullOrEmpty(sku) && root.TryGetProperty("copilot_plan", out var cp))
        {
            sku = cp.GetString();
        }

        return new UsageSnapshot
        {
            Provider = ProviderId.Copilot,
            Source = "GitHub Token",
            UpdatedAt = DateTimeOffset.UtcNow,
            HeadlineIndex = 0,
            Windows = windows.Count > 0 ? windows : new List<UsageWindow> { new UsageWindow { Label = "Copilot", UsedPercent = 0f } },
            Identity = new ProviderIdentity
            {
                DisplayName = login,
                Email = account?.Email,
                Plan = FormatPlan(sku)
            }
        };
    }

    private static string FormatPlan(string? sku)
    {
        if (string.IsNullOrWhiteSpace(sku)) return "Copilot";
        return sku.ToLowerInvariant() switch
        {
            "free_limited_copilot" => "Copilot Free",
            "copilot_for_business" or "business" => "Copilot Business",
            "copilot_enterprise" or "enterprise" => "Copilot Enterprise",
            "copilot_pro" or "pro" => "Copilot Pro",
            "copilot_individual" or "individual" => "Copilot Individual",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(sku.Replace("_", " ").Replace("-", " "))
        };
    }

    private static string FormatSnapshotLabel(string key)
    {
        return key.ToLowerInvariant() switch
        {
            "chat" => "Chat",
            "completions" => "Completions",
            "claude_3_7_sonnet" or "claude-3-7-sonnet" or "claude_3.7_sonnet" => "Claude 3.7 Sonnet",
            "claude_3_5_sonnet" or "claude-3-5-sonnet" or "claude_3.5_sonnet" => "Claude 3.5 Sonnet",
            "gpt_4o" or "gpt-4o" => "GPT-4o",
            "gpt_4o_mini" or "gpt-4o-mini" => "GPT-4o mini",
            "o1" => "o1",
            "o1_mini" or "o1-mini" => "o1-mini",
            "o3_mini" or "o3-mini" => "o3-mini",
            "gemini_2_0_flash" or "gemini-2-0-flash" or "gemini_2.0_flash" => "Gemini 2.0 Flash",
            "premium_interactions" => "Premium Interactions",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(key.Replace("_", " ").Replace("-", " "))
        };
    }
}
