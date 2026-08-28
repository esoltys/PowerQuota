using System.Net.Http.Headers;
using System.Text.Json;
using PowerQuota.Core.Models;
using PowerQuota.Core.Storage;

namespace PowerQuota.Core.Providers;

public class GeminiProvider : IProviderAdapter
{
    private const string LoadCodeAssistUrl = "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist";
    private const string RetrieveQuotaSummaryUrl = "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary";

    public ProviderId Id => ProviderId.Gemini;

    public async Task<UsageSnapshot> FetchAsync(AccountConfig account, WindowsCredentialVault vault, HttpClient client, CancellationToken ct = default)
    {
        var tokens = vault.GetTokens(account.Id) ?? new StoredTokens();

        if (string.IsNullOrEmpty(tokens.AccessToken) && string.IsNullOrEmpty(tokens.RefreshToken))
        {
            var (scannedAt, scannedRt, scannedExp, scannedEmail) = HostCliScanner.ScanGeminiAntigravityCredentials();
            if (!string.IsNullOrEmpty(scannedAt) || !string.IsNullOrEmpty(scannedRt))
            {
                tokens.AccessToken = scannedAt ?? string.Empty;
                tokens.RefreshToken = scannedRt;
                tokens.ExpiresAt = scannedExp;
                vault.SaveTokens(account.Id, tokens);
            }
            else
            {
                throw new InvalidOperationException("Gemini login required");
            }
        }

        // Auto-refresh access token if close to expiry
        if (tokens.ExpiresAt.HasValue && tokens.ExpiresAt.Value <= DateTimeOffset.UtcNow.AddMinutes(2) && !string.IsNullOrEmpty(tokens.RefreshToken))
        {
            var (refreshedAt, refreshedExp) = await HostCliScanner.RefreshGeminiTokenAsync(tokens.RefreshToken, client, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(refreshedAt))
            {
                tokens.AccessToken = refreshedAt;
                tokens.ExpiresAt = refreshedExp;
                vault.SaveTokens(account.Id, tokens);
            }
        }

        if (string.IsNullOrEmpty(tokens.AccessToken))
        {
            throw new InvalidOperationException("Gemini login required");
        }

        string? planName = null;

        async Task<string> SendAuthorizedPostAsync(string url)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
            req.Headers.Add("User-Agent", "antigravity/2.11.0");
            req.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized || resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                if (!string.IsNullOrEmpty(tokens.RefreshToken))
                {
                    var (refreshedAt, refreshedExp) = await HostCliScanner.RefreshGeminiTokenAsync(tokens.RefreshToken, client, ct).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(refreshedAt))
                    {
                        tokens.AccessToken = refreshedAt;
                        tokens.ExpiresAt = refreshedExp;
                        vault.SaveTokens(account.Id, tokens);

                        using var retryReq = new HttpRequestMessage(HttpMethod.Post, url);
                        retryReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
                        retryReq.Headers.Add("User-Agent", "antigravity/2.11.0");
                        retryReq.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

                        using var retryResp = await client.SendAsync(retryReq, ct).ConfigureAwait(false);
                        if (retryResp.IsSuccessStatusCode)
                        {
                            return await retryResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        }
                    }
                }

                throw new UnauthorizedAccessException("Gemini session expired");
            }

            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }

        // Step 1: Load code assist for plan info
        try
        {
            var loadJson = await SendAuthorizedPostAsync(LoadCodeAssistUrl).ConfigureAwait(false);
            using var loadDoc = JsonDocument.Parse(loadJson);
            if (loadDoc.RootElement.TryGetProperty("paidTier", out var pt) && pt.TryGetProperty("name", out var ptName))
            {
                planName = ptName.GetString();
            }
            else if (loadDoc.RootElement.TryGetProperty("currentTier", out var ctObj) && ctObj.TryGetProperty("id", out var tid))
            {
                planName = tid.GetString();
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }
        catch
        {
            // Non-fatal, continue to retrieve quota summary
        }

        // Step 2: Retrieve quota summary
        var quotaJson = await SendAuthorizedPostAsync(RetrieveQuotaSummaryUrl).ConfigureAwait(false);

        return ParseUsage(quotaJson, planName, account);
    }

    public Task<string?> GetSystemActiveAccountIdAsync(IReadOnlyList<AccountConfig> accounts, WindowsCredentialVault vault)
    {
        var (at, rt, _, _) = HostCliScanner.ScanGeminiAntigravityCredentials();
        if (string.IsNullOrEmpty(at) && string.IsNullOrEmpty(rt)) return Task.FromResult<string?>(null);

        var match = accounts.FirstOrDefault(a => a.Provider == ProviderId.Gemini &&
            (vault.GetTokens(a.Id)?.AccessToken == at || (rt != null && vault.GetTokens(a.Id)?.RefreshToken == rt)));
        return Task.FromResult(match?.Id);
    }

    public static UsageSnapshot ParseUsage(string json, string? tier, AccountConfig? account = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var windows = new List<UsageWindow>();

        if (root.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in groups.EnumerateArray())
            {
                string groupName = group.TryGetProperty("displayName", out var gn) ? gn.GetString() ?? "" : "";
                bool isGeminiGroup = groupName.Contains("Gemini", StringComparison.OrdinalIgnoreCase);
                bool isClaudeGptGroup = groupName.Contains("Claude", StringComparison.OrdinalIgnoreCase) || groupName.Contains("GPT", StringComparison.OrdinalIgnoreCase);

                if (group.TryGetProperty("buckets", out var buckets) && buckets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var bucket in buckets.EnumerateArray())
                    {
                        string bucketId = bucket.TryGetProperty("bucketId", out var bi) ? bi.GetString() ?? "" : "";
                        string bucketWindow = bucket.TryGetProperty("window", out var bw) ? bw.GetString() ?? "" : "";
                        string bucketDisplayName = bucket.TryGetProperty("displayName", out var bdn) ? bdn.GetString() ?? "" : "";
                        string? description = bucket.TryGetProperty("description", out var bd) ? bd.GetString() : null;
                        float remainingFraction = bucket.TryGetProperty("remainingFraction", out var rf) ? rf.GetSingle() : 1f;
                        float usedPercent = (1f - remainingFraction) * 100f;

                        DateTimeOffset? resetTime = null;
                        if (bucket.TryGetProperty("resetTime", out var rt) && DateTimeOffset.TryParse(rt.GetString(), out var parsedRt))
                        {
                            resetTime = parsedRt;
                        }

                        string windowSuffix = bucketWindow.Equals("5h", StringComparison.OrdinalIgnoreCase) || bucketDisplayName.Contains("Five Hour", StringComparison.OrdinalIgnoreCase) || bucketId.Contains("5h", StringComparison.OrdinalIgnoreCase)
                            ? "5h"
                            : bucketWindow.Equals("weekly", StringComparison.OrdinalIgnoreCase) || bucketDisplayName.Contains("Weekly", StringComparison.OrdinalIgnoreCase) || bucketId.Contains("weekly", StringComparison.OrdinalIgnoreCase)
                                ? "Weekly"
                                : bucketWindow;

                        bool is3p = isClaudeGptGroup || bucketId.Contains("3p", StringComparison.OrdinalIgnoreCase) || bucketId.Contains("claude", StringComparison.OrdinalIgnoreCase) || bucketId.Contains("gpt", StringComparison.OrdinalIgnoreCase);
                        bool isGemini = (isGeminiGroup || bucketId.Contains("gemini", StringComparison.OrdinalIgnoreCase)) && !is3p;

                        string label;
                        string resetDesc;
                        long? windowSeconds = null;

                        if (isGemini)
                        {
                            if (windowSuffix == "5h")
                            {
                                label = "Session";
                                resetDesc = "5-hour session window";
                                windowSeconds = 5 * 3600;
                            }
                            else if (windowSuffix == "Weekly")
                            {
                                label = "Weekly";
                                resetDesc = "Weekly quota";
                                windowSeconds = 7 * 24 * 3600;
                            }
                            else
                            {
                                label = string.IsNullOrEmpty(windowSuffix) ? "Gemini" : $"Gemini ({windowSuffix})";
                                resetDesc = string.IsNullOrEmpty(description) ? $"Gemini {windowSuffix} Quota" : description;
                            }
                        }
                        else if (is3p)
                        {
                            if (windowSuffix == "5h")
                            {
                                label = "Claude/GPT (Session)";
                                resetDesc = "5-hour session window";
                                windowSeconds = 5 * 3600;
                            }
                            else if (windowSuffix == "Weekly")
                            {
                                label = "Claude/GPT (Weekly)";
                                resetDesc = "Weekly quota";
                                windowSeconds = 7 * 24 * 3600;
                            }
                            else
                            {
                                label = string.IsNullOrEmpty(windowSuffix) ? "Claude/GPT" : $"Claude/GPT ({windowSuffix})";
                                resetDesc = string.IsNullOrEmpty(description) ? $"Claude & GPT {windowSuffix} Quota" : description;
                            }
                        }
                        else
                        {
                            label = string.IsNullOrEmpty(groupName) ? bucketDisplayName : $"{groupName} ({windowSuffix})";
                            resetDesc = description ?? $"{label} Quota";
                        }

                        windows.Add(new UsageWindow
                        {
                            Label = label,
                            UsedPercent = Math.Clamp(usedPercent, 0f, 100f),
                            ResetAt = resetTime,
                            WindowSeconds = windowSeconds,
                            ResetDescription = resetDesc
                        });
                    }
                }
            }

            // Ensure Session limit is placed before Weekly limit, and Gemini group before 3P models
            windows.Sort((a, b) =>
            {
                bool aIs3p = a.Label.StartsWith("Claude/GPT", StringComparison.OrdinalIgnoreCase);
                bool bIs3p = b.Label.StartsWith("Claude/GPT", StringComparison.OrdinalIgnoreCase);
                if (aIs3p != bIs3p) return aIs3p ? 1 : -1;

                bool aIsSession = a.Label.Equals("Session", StringComparison.OrdinalIgnoreCase) || a.Label.Contains("Session", StringComparison.OrdinalIgnoreCase) || a.Label.Contains("5h", StringComparison.OrdinalIgnoreCase);
                bool bIsSession = b.Label.Equals("Session", StringComparison.OrdinalIgnoreCase) || b.Label.Contains("Session", StringComparison.OrdinalIgnoreCase) || b.Label.Contains("5h", StringComparison.OrdinalIgnoreCase);
                if (aIsSession != bIsSession) return aIsSession ? -1 : 1;

                return string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
            });
        }
        else if (root.TryGetProperty("buckets", out var legacyBuckets) && legacyBuckets.ValueKind == JsonValueKind.Array)
        {
            foreach (var bucket in legacyBuckets.EnumerateArray())
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
            "g1-pro-tier" => "Google AI Pro",
            "g1-ultra-tier" => "Google AI Ultra",
            null or "" => "Google AI",
            _ => tier
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

