using System.Text.Json.Serialization;
using PowerQuota.Core.Models;

namespace PowerQuota.Core.Storage;

public enum DockDisplayMode
{
    Percentage,
    Bars,
    LogoAndPercentage = Percentage,
    PercentageOnly = Percentage,
    BarsOnly = Bars
}

public class AccountConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("provider")]
    public ProviderId Provider { get; set; }

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("provider_account_id")]
    public string? ProviderAccountId { get; set; }

    [JsonPropertyName("api_key_source")]
    public string? ApiKeySource { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("last_authenticated_at")]
    public DateTimeOffset? LastAuthenticatedAt { get; set; }

    public AccountConfig Clone() => new()
    {
        Id = Id,
        Provider = Provider,
        Label = Label,
        Email = Email,
        ProviderAccountId = ProviderAccountId,
        ApiKeySource = ApiKeySource,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        LastAuthenticatedAt = LastAuthenticatedAt
    };
}

public class PowerQuotaConfig
{
    [JsonPropertyName("refresh_interval_minutes")]
    public int RefreshIntervalMinutes { get; set; } = 5;

    [JsonPropertyName("display_remaining_not_used")]
    public bool DisplayRemainingNotUsed { get; set; } = false;

    [JsonPropertyName("show_relative_reset_times")]
    public bool ShowRelativeResetTimes { get; set; } = true;

    [JsonPropertyName("dock_display_mode")]
    public DockDisplayMode DockDisplayMode { get; set; } = DockDisplayMode.LogoAndPercentage;

    [JsonPropertyName("accounts")]
    public List<AccountConfig> Accounts { get; set; } = new();

    [JsonPropertyName("enabled_providers")]
    public HashSet<ProviderId> EnabledProviders { get; set; } = new(Enum.GetValues<ProviderId>());

    public PowerQuotaConfig Clone() => new()
    {
        RefreshIntervalMinutes = RefreshIntervalMinutes,
        DisplayRemainingNotUsed = DisplayRemainingNotUsed,
        ShowRelativeResetTimes = ShowRelativeResetTimes,
        DockDisplayMode = DockDisplayMode,
        Accounts = Accounts.Select(a => a.Clone()).ToList(),
        EnabledProviders = new HashSet<ProviderId>(EnabledProviders)
    };
}


