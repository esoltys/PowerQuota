using System.Text.Json.Serialization;

namespace PowerQuota.Core.Models;

public class UsageWindow
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("used_percent")]
    public float UsedPercent { get; set; }

    [JsonPropertyName("reset_at")]
    public DateTimeOffset? ResetAt { get; set; }

    [JsonPropertyName("window_seconds")]
    public long? WindowSeconds { get; set; }

    [JsonPropertyName("reset_description")]
    public string? ResetDescription { get; set; }
}

public class ProviderCost
{
    [JsonPropertyName("used")]
    public double Used { get; set; }

    [JsonPropertyName("limit")]
    public double? Limit { get; set; }

    [JsonPropertyName("units")]
    public string Units { get; set; } = string.Empty;
}

public class ExtraUsageState
{
    [JsonPropertyName("is_active")]
    public bool IsActive { get; set; }

    [JsonPropertyName("used_percent")]
    public float UsedPercent { get; set; }

    [JsonPropertyName("cost")]
    public ProviderCost? Cost { get; set; }
}

public class ProviderIdentity
{
    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("account_id")]
    public string? AccountId { get; set; }

    [JsonPropertyName("plan")]
    public string? Plan { get; set; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }
}

public class UsageSnapshot
{
    [JsonPropertyName("provider")]
    public ProviderId Provider { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("headline_index")]
    public int HeadlineIndex { get; set; }

    [JsonPropertyName("windows")]
    public List<UsageWindow> Windows { get; set; } = new();

    [JsonPropertyName("provider_cost")]
    public ProviderCost? ProviderCost { get; set; }

    [JsonPropertyName("extra_usage")]
    public ExtraUsageState? ExtraUsage { get; set; }

    [JsonPropertyName("identity")]
    public ProviderIdentity Identity { get; set; } = new();

    [JsonIgnore]
    public UsageWindow? HeadlineWindow => Windows.Count > HeadlineIndex && HeadlineIndex >= 0 ? Windows[HeadlineIndex] : Windows.FirstOrDefault();
}

public enum ProviderHealth
{
    Ok,
    Error
}

public enum AuthState
{
    Ready,
    ActionRequired,
    Error
}

public enum AccountSelectionStatus
{
    Ready,
    LoginRequired,
    SelectionRequired,
    Unavailable
}

public class ProviderAccountRuntimeState
{
    public ProviderId Provider { get; set; }
    public string AccountId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? SourceLabel { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public UsageSnapshot? Snapshot { get; set; }
    public ProviderHealth Health { get; set; } = ProviderHealth.Ok;
    public AuthState AuthState { get; set; } = AuthState.Ready;
    public string? Error { get; set; }
    public DateTimeOffset? RetryAfter { get; set; }
    public uint ConsecutiveFailures { get; set; }

    public bool IsBackingOff => RetryAfter.HasValue && RetryAfter.Value > DateTimeOffset.UtcNow;

    public string GetStatusLine(bool displayRemaining = false)
    {
        if (Snapshot?.HeadlineWindow is { } window)
        {
            float percent = displayRemaining ? Math.Clamp(100f - window.UsedPercent, 0f, 100f) : window.UsedPercent;
            string prefix = displayRemaining ? $"{window.Label} left" : window.Label;
            string line = $"{prefix} {percent:0}%";
            bool isStale = Health == ProviderHealth.Error || !LastSuccessAt.HasValue || (DateTimeOffset.UtcNow - LastSuccessAt.Value > TimeSpan.FromMinutes(15));
            return isStale ? $"{line} (stale)" : line;
        }

        return Error ?? "No usage data yet";
    }
}

public class ProviderRuntimeState
{
    public ProviderId Provider { get; set; }
    public bool Enabled { get; set; } = true;
    public List<string> SelectedAccountIds { get; set; } = new();
    public string? ActiveAccountId { get; set; }
    public string? SystemActiveAccountId { get; set; }
    public AccountSelectionStatus AccountStatus { get; set; } = AccountSelectionStatus.Ready;
    public bool IsRefreshing { get; set; }
    public DateTimeOffset? RefreshStartedAt { get; set; }
    public string? Error { get; set; }

    public static ProviderRuntimeState Create(ProviderId provider) => new()
    {
        Provider = provider,
        Enabled = true,
        AccountStatus = AccountSelectionStatus.Ready
    };
}

public class AppState
{
    public List<ProviderRuntimeState> Providers { get; set; } = new();
    public List<ProviderAccountRuntimeState> ProviderAccounts { get; set; } = new();
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

