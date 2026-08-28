using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using PowerQuota.Core.Models;
using PowerQuota.Core.Storage;
using PowerQuota.Core.Utilities;

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

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        request.Headers.Add("Cookie", $"WorkosCursorSessionToken={tokens.AccessToken}");

        using var response = await client.SendAsync(request, ct);
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

        DateTimeOffset? nextMonth = null;
        if (root.TryGetProperty("startOfMonth", out var somStr) && DateTimeOffset.TryParse(somStr.GetString(), out var som))
        {
            nextMonth = som.AddMonths(1);
        }
        else if (root.TryGetProperty("billing_cycle_end", out var bceStr) && DateTimeOffset.TryParse(bceStr.GetString(), out var bce))
        {
            nextMonth = bce;
        }

        UsageWindow ParseRequestWindow(JsonElement element, string label)
        {
            element.TryGetPropertyInt32("numRequests", out var numRequests);
            if (numRequests == 0) element.TryGetPropertyInt32("used", out numRequests);

            element.TryGetPropertyInt32("maxRequestUsage", out var maxRequests);
            if (maxRequests == 0) element.TryGetPropertyInt32("limit", out maxRequests);
            if (maxRequests <= 0) maxRequests = 500;

            float usedPct = maxRequests > 0 ? ((float)numRequests / maxRequests * 100f) : 0f;
            int remaining = Math.Max(0, maxRequests - numRequests);

            return new UsageWindow
            {
                Label = label,
                UsedPercent = Math.Clamp(usedPct, 0f, 100f),
                ResetAt = nextMonth,
                ResetDescription = $"{numRequests} / {maxRequests} requests ({remaining} left)"
            };
        }

        // 1. Fast / Composer / GPT-4 requests
        if (root.TryGetProperty("gpt4", out var gpt4) && gpt4.ValueKind == JsonValueKind.Object)
        {
            windows.Add(ParseRequestWindow(gpt4, "Fast / Composer"));
        }
        else if (root.TryGetProperty("fast", out var fast) && fast.ValueKind == JsonValueKind.Object)
        {
            windows.Add(ParseRequestWindow(fast, "Fast / Composer"));
        }

        // 2. Composer specific pool if distinct
        if (root.TryGetProperty("composer", out var composer) && composer.ValueKind == JsonValueKind.Object)
        {
            windows.Add(ParseRequestWindow(composer, "Composer"));
        }

        // 3. Custom models / multi-model pools
        if (root.TryGetProperty("custom_models", out var customModels) || root.TryGetProperty("customModels", out customModels))
        {
            if (customModels.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in customModels.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        string label = FormatCursorModelName(prop.Name);
                        windows.Add(ParseRequestWindow(prop.Value, label));
                    }
                }
            }
            else if (customModels.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in customModels.EnumerateArray())
                {
                    string label = item.TryGetProperty("name", out var n) || item.TryGetProperty("model", out n)
                        ? n.GetString() ?? "Custom Model"
                        : "Custom Model";
                    windows.Add(ParseRequestWindow(item, FormatCursorModelName(label)));
                }
            }
        }

        // 4. Token pools or models object
        if (root.TryGetProperty("models", out var modelsObj) && modelsObj.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in modelsObj.EnumerateObject())
            {
                string formattedLabel = FormatCursorModelName(prop.Name);
                if (prop.Value.ValueKind == JsonValueKind.Object && !windows.Any(w => w.Label.Equals(formattedLabel, StringComparison.OrdinalIgnoreCase)))
                {
                    windows.Add(ParseRequestWindow(prop.Value, formattedLabel));
                }
            }
        }

        // 5. Usage-based pricing / spend
        ProviderCost? cost = null;
        ExtraUsageState? extraUsage = null;

        JsonElement spendElem = default;
        bool hasSpend = root.TryGetProperty("usage_based_spend", out spendElem) ||
                        root.TryGetProperty("usageBasedSpend", out spendElem) ||
                        root.TryGetProperty("spend", out spendElem) ||
                        root.TryGetProperty("ondemand", out spendElem);

        if (hasSpend)
        {
            if (spendElem.ValueKind == JsonValueKind.Object)
            {
                spendElem.TryGetPropertyDouble("spend", out var spend);
                if (spend == 0) spendElem.TryGetPropertyDouble("used", out spend);

                double? limit = null;
                if (spendElem.TryGetPropertyDouble("limit", out var limVal) && limVal > 0) limit = limVal;
                else if (spendElem.TryGetPropertyDouble("hardLimit", out var hlVal) && hlVal > 0) limit = hlVal;
                else if (spendElem.TryGetPropertyDouble("hard_limit", out var hlVal2) && hlVal2 > 0) limit = hlVal2;

                string currency = spendElem.TryGetProperty("currency", out var curr) ? curr.GetString() ?? "USD" : "USD";

                cost = new ProviderCost
                {
                    Used = spend,
                    Limit = limit,
                    Units = currency
                };

                extraUsage = new ExtraUsageState
                {
                    IsActive = spend > 0,
                    UsedPercent = limit.HasValue && limit.Value > 0 ? (float)(spend / limit.Value * 100.0) : 0f,
                    Cost = cost
                };
            }
            else if (spendElem.TryGetDoubleValue(out var spendVal))
            {
                cost = new ProviderCost
                {
                    Used = spendVal,
                    Units = "USD"
                };
            }
        }

        string? email = root.TryGetProperty("email", out var em) ? em.GetString() : account?.Email;
        string? plan = root.TryGetProperty("plan", out var pl) ? pl.GetString() :
                       root.TryGetProperty("membershipType", out var mt) ? mt.GetString() : "Pro";

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
            ProviderCost = cost,
            ExtraUsage = extraUsage,
            Identity = new ProviderIdentity
            {
                Email = email,
                Plan = plan
            }
        };
    }

    private static string FormatCursorModelName(string name)
    {
        return name.ToLowerInvariant() switch
        {
            "claude-3-7-sonnet" or "claude_3_7_sonnet" or "claude-3.7-sonnet" => "Claude 3.7 Sonnet",
            "claude-3-5-sonnet" or "claude_3_5_sonnet" or "claude-3.5-sonnet" => "Claude 3.5 Sonnet",
            "gpt-4" or "gpt4" => "GPT-4",
            "gpt-4o" or "gpt4o" => "GPT-4o",
            "composer" => "Composer",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.Replace("_", " ").Replace("-", " "))
        };
    }
}
