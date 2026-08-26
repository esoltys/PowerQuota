using System.Net.Http.Headers;
using System.Text.Json;
using PowerQuota.Core.Models;
using PowerQuota.Core.Storage;

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

        var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("token", tokens.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("Editor-Version", "vscode/1.107.0");
        request.Headers.Add("Editor-Plugin-Version", "copilot-chat/0.35.0");
        request.Headers.Add("User-Agent", "GitHubCopilotChat/0.35.0");
        request.Headers.Add("X-Github-Api-Version", "2026-03-10");

        var response = await client.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException("GitHub Copilot session expired");
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseUsage(json, account);
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
            if (snapshots.TryGetProperty("chat", out var chat))
            {
                int entitlement = chat.TryGetProperty("entitlement", out var ent) ? ent.GetInt32() : 0;
                int remaining = chat.TryGetProperty("remaining", out var rem) ? rem.GetInt32() : 0;
                float usedPct = entitlement > 0 ? ((float)(entitlement - remaining) / entitlement * 100f) : 0f;

                windows.Add(new UsageWindow
                {
                    Label = "Chat",
                    UsedPercent = Math.Clamp(usedPct, 0f, 100f),
                    ResetAt = resetDate,
                    ResetDescription = $"{entitlement - remaining} / {entitlement} messages"
                });
            }

            if (snapshots.TryGetProperty("completions", out var comp))
            {
                int entitlement = comp.TryGetProperty("entitlement", out var ent) ? ent.GetInt32() : 0;
                int remaining = comp.TryGetProperty("remaining", out var rem) ? rem.GetInt32() : 0;
                float usedPct = entitlement > 0 ? ((float)(entitlement - remaining) / entitlement * 100f) : 0f;

                windows.Add(new UsageWindow
                {
                    Label = "Completions",
                    UsedPercent = Math.Clamp(usedPct, 0f, 100f),
                    ResetAt = resetDate,
                    ResetDescription = $"{entitlement - remaining} / {entitlement} completions"
                });
            }
        }

        string? login = root.TryGetProperty("login", out var l) ? l.GetString() : null;
        string? plan = root.TryGetProperty("access_type_sku", out var sku) ? sku.GetString() : "Copilot";

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
                Plan = plan
            }
        };
    }
}

