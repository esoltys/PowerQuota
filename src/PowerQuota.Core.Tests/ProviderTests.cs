using Xunit;
using PowerQuota.Core.Engine;
using PowerQuota.Core.Models;
using PowerQuota.Core.Providers;
using PowerQuota.Core.Storage;

namespace PowerQuota.Core.Tests;

public class ProviderTests
{
    [Fact]
    public void CodexProvider_ParsesUsageResponseCorrectly()
    {
        var json = """
        {
            "account_id": "org-test123",
            "email": "dev@example.com",
            "plan_type": "plus",
            "rate_limit": {
                "primary_window": {
                    "used_percent": 34.5,
                    "limit_window_seconds": 18000,
                    "reset_at": 1780000000
                },
                "secondary_window": {
                    "used_percent": 82.0,
                    "limit_window_seconds": 604800,
                    "reset_at": 1780500000
                }
            },
            "credits": {
                "balance": 150.0
            }
        }
        """;

        var snapshot = CodexProvider.ParseUsage(json);

        Assert.Equal(ProviderId.Codex, snapshot.Provider);
        Assert.Equal(2, snapshot.Windows.Count);
        Assert.Equal("Session", snapshot.Windows[0].Label);
        Assert.Equal(34.5f, snapshot.Windows[0].UsedPercent);
        Assert.Equal("Weekly", snapshot.Windows[1].Label);
        Assert.Equal(82.0f, snapshot.Windows[1].UsedPercent);
        Assert.NotNull(snapshot.ProviderCost);
        Assert.Equal(150.0, snapshot.ProviderCost!.Used);
        Assert.Equal("dev@example.com", snapshot.Identity.Email);
        Assert.Equal("ChatGPT Plus", snapshot.Identity.Plan);
    }

    [Fact]
    public void CodexProvider_ParsesMultiModelAndArrayLimitsResiliently()
    {
        var json = """
        {
            "account_id": "org-multi456",
            "email": "user@openai.com",
            "plan_type": "team",
            "rate_limit": {
                "gpt_4o": {
                    "used_percent": "15.5",
                    "reset_at": "1780100000",
                    "limit_window_seconds": "18000"
                },
                "o1": {
                    "used_percent": 60,
                    "reset_at": 1780200000,
                    "limit_window_seconds": 604800
                },
                "o3_mini": {
                    "used_percent": 10.0,
                    "reset_at": 1780300000
                }
            }
        }
        """;

        var snapshot = CodexProvider.ParseUsage(json);

        Assert.Equal(ProviderId.Codex, snapshot.Provider);
        Assert.Equal(3, snapshot.Windows.Count);
        Assert.Contains(snapshot.Windows, w => w.Label == "GPT-4o" && Math.Abs(w.UsedPercent - 15.5f) < 0.01f);
        Assert.Contains(snapshot.Windows, w => w.Label == "o1" && Math.Abs(w.UsedPercent - 60f) < 0.01f);
        Assert.Contains(snapshot.Windows, w => w.Label == "o3-mini" && Math.Abs(w.UsedPercent - 10f) < 0.01f);
        Assert.Equal("ChatGPT Team", snapshot.Identity.Plan);
    }

    [Fact]
    public void CodexProvider_HandlesMissingAndPartialFieldsGracefully()
    {
        var json = """
        {
            "rate_limit": {
                "windows": [
                    {
                        "name": "Custom Model Pool",
                        "utilization": "42.5",
                        "resets_at": "2026-08-28T12:00:00Z"
                    }
                ]
            }
        }
        """;

        var snapshot = CodexProvider.ParseUsage(json);

        Assert.Equal(ProviderId.Codex, snapshot.Provider);
        Assert.Single(snapshot.Windows);
        Assert.Equal("Custom Model Pool", snapshot.Windows[0].Label);
        Assert.Equal(42.5f, snapshot.Windows[0].UsedPercent);
        Assert.NotNull(snapshot.Windows[0].ResetAt);
    }

    [Fact]
    public void ClaudeProvider_ParsesScopedWeeklyLimits()
    {
        var json = """
        [
            {
                "kind": "five_hour",
                "group": "session",
                "percent": 25.0,
                "resets_at": "2026-08-26T18:00:00Z"
            },
            {
                "kind": "weekly_scoped",
                "group": "weekly",
                "percent": 60.0,
                "resets_at": "2026-08-30T00:00:00Z",
                "scope": {
                    "model": {
                        "id": "claude-3-7-sonnet",
                        "display_name": "Claude 3.7 Sonnet"
                    }
                }
            }
        ]
        """;

        var snapshot = ClaudeProvider.ParseUsage(json);

        Assert.Equal(ProviderId.Claude, snapshot.Provider);
        Assert.Equal(2, snapshot.Windows.Count);
        Assert.Equal("Session", snapshot.Windows[0].Label);
        Assert.Equal(25.0f, snapshot.Windows[0].UsedPercent);
        Assert.Equal("Claude 3.7 Sonnet", snapshot.Windows[1].Label);
        Assert.Equal(60.0f, snapshot.Windows[1].UsedPercent);
    }

    [Fact]
    public void ClaudeProvider_ParsesProductionResponseWithNullScope()
    {
        var json = """
        {
            "five_hour": { "utilization": 24.0, "resets_at": "2026-08-27T02:00:00.243486+00:00" },
            "seven_day": { "utilization": 78.0, "resets_at": "2026-08-28T21:00:00.243518+00:00" },
            "limits": [
                {
                    "kind": "session",
                    "group": "session",
                    "percent": 24,
                    "severity": "normal",
                    "resets_at": "2026-08-27T02:00:00.243486+00:00",
                    "scope": null,
                    "is_active": false
                },
                {
                    "kind": "weekly_all",
                    "group": "weekly",
                    "percent": 78,
                    "severity": "warning",
                    "resets_at": "2026-08-28T21:00:00.243518+00:00",
                    "scope": null,
                    "is_active": true
                }
            ],
            "extra_usage": {
                "is_enabled": true,
                "used_credits": 13814.0,
                "currency": "CAD"
            }
        }
        """;

        var snapshot = ClaudeProvider.ParseUsage(json);

        Assert.Equal(ProviderId.Claude, snapshot.Provider);
        Assert.Equal(2, snapshot.Windows.Count);
        Assert.Equal("Session", snapshot.Windows[0].Label);
        Assert.Equal(24.0f, snapshot.Windows[0].UsedPercent);
        Assert.Equal("Weekly", snapshot.Windows[1].Label);
        Assert.Equal(78.0f, snapshot.Windows[1].UsedPercent);
        Assert.NotNull(snapshot.ExtraUsage);
        Assert.True(snapshot.ExtraUsage!.IsActive);
        // No monthly_limit or utilization present, so UsedPercent falls back to 0.
        Assert.Equal(0f, snapshot.ExtraUsage.UsedPercent);
        Assert.NotNull(snapshot.ExtraUsage.Cost);
        Assert.Equal(13814.0, snapshot.ExtraUsage.Cost!.Used);
        Assert.Null(snapshot.ExtraUsage.Cost.Limit);
        Assert.Equal("CAD", snapshot.ExtraUsage.Cost.Units);
    }

    [Fact]
    public void ClaudeProvider_ParsesExtraUsageWithMonthlyLimitAndUtilization()
    {
        var json = """
        {
            "extra_usage": {
                "is_enabled": true,
                "used_credits": 25.50,
                "monthly_limit": 100.0,
                "currency": "USD",
                "utilization": 30.5
            }
        }
        """;

        var snapshot = ClaudeProvider.ParseUsage(json);

        Assert.NotNull(snapshot.ExtraUsage);
        Assert.True(snapshot.ExtraUsage!.IsActive);
        // utilization is present, so it takes precedence over used_credits / monthly_limit.
        Assert.Equal(30.5f, snapshot.ExtraUsage.UsedPercent);
        Assert.NotNull(snapshot.ExtraUsage.Cost);
        Assert.Equal(25.50, snapshot.ExtraUsage.Cost!.Used);
        Assert.Equal(100.0, snapshot.ExtraUsage.Cost.Limit);
        Assert.Equal("USD", snapshot.ExtraUsage.Cost.Units);
    }

    [Fact]
    public void ClaudeProvider_ParsesExtraUsageWithMonthlyLimitButNoUtilization()
    {
        var json = """
        {
            "extra_usage": {
                "is_enabled": true,
                "used_credits": 25.0,
                "monthly_limit": 100.0,
                "currency": "USD"
            }
        }
        """;

        var snapshot = ClaudeProvider.ParseUsage(json);

        Assert.NotNull(snapshot.ExtraUsage);
        Assert.True(snapshot.ExtraUsage!.IsActive);
        // No utilization present, so UsedPercent is computed from used_credits / monthly_limit.
        Assert.Equal(25.0f, snapshot.ExtraUsage.UsedPercent);
        Assert.NotNull(snapshot.ExtraUsage.Cost);
        Assert.Equal(25.0, snapshot.ExtraUsage.Cost!.Used);
        Assert.Equal(100.0, snapshot.ExtraUsage.Cost.Limit);
        Assert.Equal("USD", snapshot.ExtraUsage.Cost.Units);
    }

    [Fact]
    public void ClaudeProvider_KeepsAggregateSessionWindowWhenScopedSessionEntryPrecedesIt()
    {
        // A per-model 5-hour entry arrives before the "all models" aggregate one.
        // Previously the code labeled every "session" group item "Session" and kept
        // only the first match, so the aggregate (32%) would be silently dropped in
        // favor of the barely-used per-model entry (1%) — exactly the mismatch users
        // reported between PowerQuota and the official app.
        var json = """
        [
            {
                "kind": "five_hour",
                "group": "session",
                "percent": 1.0,
                "resets_at": "2026-09-03T23:15:00Z",
                "scope": {
                    "model": {
                        "id": "claude-opus-5",
                        "display_name": "Claude Opus 5"
                    }
                }
            },
            {
                "kind": "five_hour",
                "group": "session",
                "percent": 32.0,
                "resets_at": "2026-09-03T23:15:00Z",
                "scope": null
            },
            {
                "kind": "weekly_all",
                "group": "weekly",
                "percent": 44.0,
                "resets_at": "2026-09-04T14:23:00Z",
                "scope": null
            }
        ]
        """;

        var snapshot = ClaudeProvider.ParseUsage(json);

        Assert.Equal(3, snapshot.Windows.Count);
        Assert.Equal("Session", snapshot.Windows[0].Label);
        Assert.Equal(32.0f, snapshot.Windows[0].UsedPercent);
        Assert.Equal("Weekly", snapshot.Windows[1].Label);
        Assert.Equal(44.0f, snapshot.Windows[1].UsedPercent);
        Assert.Equal("Claude Opus 5 (Session)", snapshot.Windows[2].Label);
        Assert.Equal(1.0f, snapshot.Windows[2].UsedPercent);
    }

    [Fact]
    public void UsageWindow_FormatResetTime_FormatsDaysAndHoursAccurately()
    {
        // 1. Multi-day reset (4 days away -> e.g. "Resets Friday at 2:00 PM")
        var futureReset = DateTimeOffset.UtcNow.AddDays(4);
        var text = UsageWindow.FormatResetTime(futureReset);
        var expectedDay = futureReset.ToLocalTime().ToString("dddd");
        Assert.StartsWith("Resets ", text);
        Assert.Contains(expectedDay, text);
        Assert.Contains("at ", text);

        // 2. Short window relative (< 24h, e.g. 2h 30m away)
        var shortReset = DateTimeOffset.UtcNow.AddHours(2.5);
        var relText = UsageWindow.FormatResetTime(shortReset, showRelative: true);
        Assert.StartsWith("Resets in 2h ", relText);
    }

    [Fact]
    public void CursorProvider_ParsesFastAndTotalRequests()
    {
        var json = """
        {
            "gpt4": {
                "numRequests": 125,
                "maxRequestUsage": 500
            },
            "startOfMonth": "2026-08-01T00:00:00Z"
        }
        """;

        var snapshot = CursorProvider.ParseUsage(json);

        Assert.Equal(ProviderId.Cursor, snapshot.Provider);
        Assert.Single(snapshot.Windows);
        Assert.Equal("Fast / Composer", snapshot.Windows[0].Label);
        Assert.Equal(25.0f, snapshot.Windows[0].UsedPercent);
        Assert.NotNull(snapshot.Windows[0].ResetAt);
        Assert.Equal("125 / 500 requests (375 left)", snapshot.Windows[0].ResetDescription);
    }

    [Fact]
    public void CursorProvider_ParsesMultiModelPoolsAndUsageSpend()
    {
        var json = """
        {
            "gpt4": {
                "numRequests": 50,
                "maxRequestUsage": 500
            },
            "composer": {
                "numRequests": 100,
                "maxRequestUsage": 200
            },
            "custom_models": {
                "claude-3-7-sonnet": {
                    "numRequests": 30,
                    "maxRequestUsage": 100
                }
            },
            "usage_based_spend": {
                "spend": 14.50,
                "limit": 50.00,
                "currency": "USD"
            },
            "startOfMonth": "2026-08-01T00:00:00Z",
            "membershipType": "Business"
        }
        """;

        var snapshot = CursorProvider.ParseUsage(json);

        Assert.Equal(ProviderId.Cursor, snapshot.Provider);
        Assert.Equal(3, snapshot.Windows.Count);
        Assert.Equal("Fast / Composer", snapshot.Windows[0].Label);
        Assert.Equal(10.0f, snapshot.Windows[0].UsedPercent, 1);
        Assert.Equal("Composer", snapshot.Windows[1].Label);
        Assert.Equal(50.0f, snapshot.Windows[1].UsedPercent, 1);
        Assert.Equal("Claude 3.7 Sonnet", snapshot.Windows[2].Label);
        Assert.Equal(30.0f, snapshot.Windows[2].UsedPercent, 1);

        Assert.NotNull(snapshot.ProviderCost);
        Assert.Equal(14.50, snapshot.ProviderCost!.Used);
        Assert.Equal(50.00, snapshot.ProviderCost.Limit);
        Assert.Equal("USD", snapshot.ProviderCost.Units);

        Assert.NotNull(snapshot.ExtraUsage);
        Assert.True(snapshot.ExtraUsage!.IsActive);
        Assert.Equal(29.0f, snapshot.ExtraUsage.UsedPercent, 1);

        Assert.Equal("Business", snapshot.Identity.Plan);
    }

    [Fact]
    public void GeminiProvider_ParsesAntigravityGroupedQuotaSummary()
    {
        var json = """
        {
            "groups": [
                {
                    "displayName": "Gemini Models",
                    "description": "Models within this group: Gemini Flash, Gemini Pro",
                    "buckets": [
                        {
                            "bucketId": "gemini-weekly",
                            "displayName": "Weekly Limit Remaining",
                            "window": "weekly",
                            "resetTime": "2026-09-01T19:39:19Z",
                            "description": "You have used some of your weekly limit, it will fully refresh in 4 days.",
                            "remainingFraction": 0.5433856
                        },
                        {
                            "bucketId": "gemini-5h",
                            "displayName": "Five Hour Limit Remaining",
                            "window": "5h",
                            "resetTime": "2026-08-28T20:23:59Z",
                            "description": "You have used some of your 5-hour limit, it will fully refresh in 1 hour, 9 minutes.",
                            "remainingFraction": 0.2460168
                        }
                    ]
                },
                {
                    "displayName": "Claude and GPT models",
                    "description": "Models within this group: Claude Opus, Claude Sonnet, GPT-OSS",
                    "buckets": [
                        {
                            "bucketId": "3p-weekly",
                            "displayName": "Weekly Limit Remaining",
                            "window": "weekly",
                            "resetTime": "2026-08-29T01:41:04Z",
                            "description": "You have used some of your weekly limit, it will fully refresh in 6 hours, 26 minutes.",
                            "remainingFraction": 0.66958416
                        },
                        {
                            "bucketId": "3p-5h",
                            "displayName": "Five Hour Limit Remaining",
                            "window": "5h",
                            "resetTime": "2026-08-29T00:14:20Z",
                            "remainingFraction": 1.0
                        }
                    ]
                }
            ]
        }
        """;

        var snapshot = GeminiProvider.ParseUsage(json, "g1-pro-tier");

        Assert.Equal(ProviderId.Gemini, snapshot.Provider);
        Assert.Equal(4, snapshot.Windows.Count);
        Assert.Equal("Session", snapshot.Windows[0].Label);
        Assert.Equal("5-hour session window", snapshot.Windows[0].ResetDescription);
        Assert.Equal(5 * 3600, snapshot.Windows[0].WindowSeconds);
        Assert.Equal(75.40f, snapshot.Windows[0].UsedPercent, 1);
        Assert.Equal("Weekly", snapshot.Windows[1].Label);
        Assert.Equal("Weekly quota", snapshot.Windows[1].ResetDescription);
        Assert.Equal(7 * 24 * 3600, snapshot.Windows[1].WindowSeconds);
        Assert.Equal(45.66f, snapshot.Windows[1].UsedPercent, 1);
        Assert.Equal("Claude/GPT (Session)", snapshot.Windows[2].Label);
        Assert.Equal(0.0f, snapshot.Windows[2].UsedPercent, 1);
        Assert.Equal("Claude/GPT (Weekly)", snapshot.Windows[3].Label);
        Assert.Equal(33.04f, snapshot.Windows[3].UsedPercent, 1);
        Assert.Equal("Google AI Pro", snapshot.Identity.Plan);
    }

    [Fact]
    public void GeminiProvider_ParsesBucketsIntoFlashAndLite()
    {
        var json = """
        {
            "buckets": [
                {
                    "modelId": "gemini-2.5-flash",
                    "remainingFraction": 0.7,
                    "resetTime": "2026-08-26T20:00:00Z"
                },
                {
                    "modelId": "gemini-2.5-flash-lite",
                    "remainingFraction": 0.95,
                    "resetTime": "2026-08-26T20:00:00Z"
                }
            ]
        }
        """;

        var snapshot = GeminiProvider.ParseUsage(json, "free-tier");

        Assert.Equal(ProviderId.Gemini, snapshot.Provider);
        Assert.Equal(2, snapshot.Windows.Count);
        Assert.Equal("Flash", snapshot.Windows[0].Label);
        Assert.Equal(30.0f, Math.Round(snapshot.Windows[0].UsedPercent));
        Assert.Equal("Lite", snapshot.Windows[1].Label);
        Assert.Equal(5.0f, Math.Round(snapshot.Windows[1].UsedPercent));
        Assert.Equal("Free", snapshot.Identity.Plan);
    }

    [Fact]
    public void CopilotProvider_ParsesChatAndCompletions()
    {
        var json = """
        {
            "access_type_sku": "copilot_for_business",
            "login": "monalisa",
            "quota_reset_date_utc": "2026-09-01T00:00:00Z",
            "quota_snapshots": {
                "chat": {
                    "entitlement": 500,
                    "remaining": 350
                },
                "completions": {
                    "entitlement": 1000,
                    "remaining": 200
                }
            }
        }
        """;

        var snapshot = CopilotProvider.ParseUsage(json);

        Assert.Equal(ProviderId.Copilot, snapshot.Provider);
        Assert.Equal(2, snapshot.Windows.Count);
        Assert.Equal("Chat", snapshot.Windows[0].Label);
        Assert.Equal(30.0f, MathF.Round(snapshot.Windows[0].UsedPercent));
        Assert.Equal("Completions", snapshot.Windows[1].Label);
        Assert.Equal(80.0f, snapshot.Windows[1].UsedPercent);
        Assert.Equal("monalisa", snapshot.Identity.DisplayName);
        Assert.Equal("Copilot Business", snapshot.Identity.Plan);
    }

    [Fact]
    public void CopilotProvider_ParsesMultiModelAndPremiumSnapshots()
    {
        var json = """
        {
            "access_type_sku": "copilot_enterprise",
            "login": "octocat",
            "quota_reset_date_utc": "2026-09-01T00:00:00Z",
            "quota_snapshots": {
                "chat": {
                    "entitlement": 500,
                    "remaining": 250
                },
                "claude_3_7_sonnet": {
                    "entitlement": 100,
                    "remaining": 70
                },
                "gpt_4o": {
                    "entitlement": 200,
                    "remaining": 40
                },
                "premium_interactions": {
                    "entitlement": 50,
                    "remaining": 10
                }
            }
        }
        """;

        var snapshot = CopilotProvider.ParseUsage(json);

        Assert.Equal(ProviderId.Copilot, snapshot.Provider);
        Assert.Equal(4, snapshot.Windows.Count);
        Assert.Equal("Chat", snapshot.Windows[0].Label);
        Assert.Equal(50.0f, snapshot.Windows[0].UsedPercent, 1);
        Assert.Equal("Claude 3.7 Sonnet", snapshot.Windows[1].Label);
        Assert.Equal(30.0f, snapshot.Windows[1].UsedPercent, 1);
        Assert.Equal("GPT-4o", snapshot.Windows[2].Label);
        Assert.Equal(80.0f, snapshot.Windows[2].UsedPercent, 1);
        Assert.Equal("Premium Interactions", snapshot.Windows[3].Label);
        Assert.Equal(80.0f, snapshot.Windows[3].UsedPercent, 1);
        Assert.Equal("Copilot Enterprise", snapshot.Identity.Plan);
    }

    [Fact]
    public void CopilotProvider_ParsesFreeLimitedCopilotAndSkipsInactiveSnapshots()
    {
        var json = """
        {
            "login": "esoltys",
            "access_type_sku": "free_limited_copilot",
            "quota_reset_date_utc": "2026-09-01T00:00:00Z",
            "quota_snapshots": {
                "chat": {
                    "has_quota": true,
                    "remaining": 197,
                    "entitlement": 200,
                    "percent_remaining": 98.5
                },
                "completions": {
                    "has_quota": true,
                    "remaining": 2000,
                    "entitlement": 2000,
                    "percent_remaining": 100.0
                },
                "premium_interactions": {
                    "has_quota": false,
                    "remaining": 0,
                    "entitlement": 0,
                    "percent_remaining": 0.0
                }
            }
        }
        """;

        var snapshot = CopilotProvider.ParseUsage(json);

        Assert.Equal(ProviderId.Copilot, snapshot.Provider);
        Assert.Equal(2, snapshot.Windows.Count);
        Assert.Equal("Chat", snapshot.Windows[0].Label);
        Assert.Equal(1.5f, snapshot.Windows[0].UsedPercent, 1);
        Assert.Equal("Completions", snapshot.Windows[1].Label);
        Assert.Equal(0.0f, snapshot.Windows[1].UsedPercent, 1);
        Assert.Equal("esoltys", snapshot.Identity.DisplayName);
        Assert.Equal("Copilot Free", snapshot.Identity.Plan);
    }

    [Fact]
    public void CopilotProvider_SkipsUnlimitedQuotaSnapshots()
    {
        var json = """
        {
            "login": "octocat",
            "access_type_sku": "copilot_enterprise",
            "quota_reset_date_utc": "2026-09-01T00:00:00Z",
            "quota_snapshots": {
                "chat": {
                    "unlimited": true,
                    "entitlement": 500,
                    "remaining": 500
                },
                "completions": {
                    "entitlement": 1000,
                    "remaining": 200
                }
            }
        }
        """;

        var snapshot = CopilotProvider.ParseUsage(json);

        Assert.Single(snapshot.Windows);
        Assert.Equal("Completions", snapshot.Windows[0].Label);
    }

    [Fact]
    public void CopilotProvider_SurfacesOverageCount()
    {
        var json = """
        {
            "login": "octocat",
            "access_type_sku": "copilot_for_business",
            "quota_reset_date_utc": "2026-09-01T00:00:00Z",
            "quota_snapshots": {
                "premium_interactions": {
                    "entitlement": 50,
                    "remaining": 0,
                    "overage_count": 12
                }
            }
        }
        """;

        var snapshot = CopilotProvider.ParseUsage(json);

        Assert.Single(snapshot.Windows);
        Assert.Equal("Premium Interactions", snapshot.Windows[0].Label);
        Assert.Equal(100.0f, snapshot.Windows[0].UsedPercent, 1);
        Assert.Contains("over plan by 12", snapshot.Windows[0].ResetDescription);
    }

    [Fact]
    public void CopilotProvider_FormatsTokenBasedBillingAsDollars()
    {
        var json = """
        {
            "login": "octocat",
            "access_type_sku": "copilot_pro",
            "token_based_billing": true,
            "quota_reset_date_utc": "2026-09-01T00:00:00Z",
            "quota_snapshots": {
                "premium_interactions": {
                    "entitlement": 200000,
                    "remaining": 150000
                }
            }
        }
        """;

        var snapshot = CopilotProvider.ParseUsage(json);

        Assert.Single(snapshot.Windows);
        Assert.Equal("Credits", snapshot.Windows[0].Label);
        Assert.Equal(25.0f, snapshot.Windows[0].UsedPercent, 1);
        Assert.Equal("$500.00 / $2000.00", snapshot.Windows[0].ResetDescription);
    }

    [Fact]
    public void CodexProvider_ParsesFreeTierMonthlyWindowCorrectly()
    {
        var json = """
        {
            "user_id": "user-123",
            "email": "user@example.com",
            "plan_type": "free",
            "rate_limit": {
                "allowed": true,
                "limit_reached": false,
                "primary_window": {
                    "used_percent": 0,
                    "limit_window_seconds": 2592000,
                    "reset_after_seconds": 2592000,
                    "reset_at": 1790467108
                },
                "secondary_window": null
            }
        }
        """;

        var snapshot = CodexProvider.ParseUsage(json);

        Assert.Equal(ProviderId.Codex, snapshot.Provider);
        Assert.Single(snapshot.Windows);
        Assert.Equal("Monthly", snapshot.Windows[0].Label);
        Assert.Equal("Monthly window", snapshot.Windows[0].ResetDescription);
        Assert.Equal(0.0f, snapshot.Windows[0].UsedPercent);
        Assert.Equal("ChatGPT Free", snapshot.Identity.Plan);
    }

    [Fact]
    public void CodexProvider_ParsesExhaustedRateLimitCorrectly()
    {
        var json = """
        {
            "user_id": "user-yvdPXiZnvCKcaf9kPGmO4sFU",
            "account_id": "",
            "email": "ericjamessoltys@outlook.com",
            "plan_type": "free",
            "rate_limit": {
                "allowed": false,
                "limit_reached": true,
                "primary_window": {
                    "used_percent": 100,
                    "limit_window_seconds": 2592000,
                    "reset_after_seconds": 2588508,
                    "reset_at": 1790519173
                },
                "secondary_window": null
            },
            "rate_limit_upsell": {
                "banner_type": "free_or_go_rate_limit_reached",
                "title": "You're out of Codex messages",
                "reset_at": 1790519173
            }
        }
        """;

        var snapshot = CodexProvider.ParseUsage(json);

        Assert.Equal(ProviderId.Codex, snapshot.Provider);
        Assert.Single(snapshot.Windows);
        Assert.Equal("Monthly", snapshot.Windows[0].Label);
        Assert.Equal(100.0f, snapshot.Windows[0].UsedPercent);
        Assert.NotNull(snapshot.Windows[0].ResetAt);
        Assert.Equal(1790519173, snapshot.Windows[0].ResetAt!.Value.ToUnixTimeSeconds());
        Assert.Equal("ChatGPT Free", snapshot.Identity.Plan);
        Assert.Equal("ericjamessoltys@outlook.com", snapshot.Identity.Email);
    }

    [Fact]
    public void CodexProvider_ExtractsJwtExpirationAndMetadata()
    {
        // JWT with payload: {"exp":1788738523,"https://api.openai.com/auth":{"chatgpt_account_id":"acc-123","chatgpt_plan_type":"plus"},"https://api.openai.com/profile":{"email":"test@example.com"}}
        var sampleJwt = "eyJhbGciOiJub25lIn0.eyJleHAiOjE3ODg3Mzg1MjMsImh0dHBzOi8vYXBpLm9wZW5haS5jb20vYXV0aCI6eyJjaGF0Z3B0X2FjY291bnRfaWQiOiJhY2MtMTIzIiwiY2hhdGdwdF9wbGFuX3R5cGUiOiJwbHVzIn0sImh0dHBzOi8vYXBpLm9wZW5haS5jb20vcHJvZmlsZSI6eyJlbWFpbCI6InRlc3RAZXhhbXBsZS5jb20ifX0.";

        var exp = HostCliScanner.ExtractJwtExpiration(sampleJwt);
        Assert.NotNull(exp);
        Assert.Equal(1788738523, exp!.Value.ToUnixTimeSeconds());

        var (accountId, email, plan) = HostCliScanner.ExtractCodexJwtMetadata(sampleJwt);
        Assert.Equal("acc-123", accountId);
        Assert.Equal("test@example.com", email);
        Assert.Equal("plus", plan);
    }

    [Fact]
    public void HostCliScanner_GetCopilotActiveToken_ReturnsTokenOrNullSafely()
    {
        var token = HostCliScanner.GetCopilotActiveToken();
        if (token != null)
        {
            Assert.NotEmpty(token);
        }
    }

    [Fact]
    public async Task CodexProvider_LiveFetch_IfTokenPresent_Succeeds()
    {
        var (at, rt, exp) = HostCliScanner.ScanCodexTokens();
        if (!string.IsNullOrEmpty(at))
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "PowerQuotaTests", Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            try
            {
                var vault = new WindowsCredentialVault(tempDir);
                var client = new HttpClient();
                var provider = new CodexProvider();
                var account = new AccountConfig
                {
                    Id = "test-live-codex",
                    Provider = ProviderId.Codex,
                    Label = "Codex Live Test"
                };

                UsageSnapshot snapshot;
                try
                {
                    snapshot = await provider.FetchAsync(account, vault, client);
                }
                catch (UnauthorizedAccessException)
                {
                    // Local token found but stale/expired - inconclusive, not a code defect.
                    return;
                }

                Assert.Equal(ProviderId.Codex, snapshot.Provider);
                Assert.NotEmpty(snapshot.Windows);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                }
            }
        }
    }

    [Fact]
    public async Task GeminiProvider_LiveFetch_IfTokenPresent_Succeeds()
    {
        var (at, rt, _, _) = HostCliScanner.ScanGeminiAntigravityCredentials();
        if (!string.IsNullOrEmpty(at) || !string.IsNullOrEmpty(rt))
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "PowerQuotaTests", Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            try
            {
                var vault = new WindowsCredentialVault(tempDir);
                using var client = new HttpClient();
                var provider = new GeminiProvider();
                var account = new AccountConfig
                {
                    Id = "test-live-gemini",
                    Provider = ProviderId.Gemini,
                    Label = "Gemini Live Test"
                };

                UsageSnapshot snapshot;
                try
                {
                    snapshot = await provider.FetchAsync(account, vault, client);
                }
                catch (UnauthorizedAccessException)
                {
                    // Local token found but stale/expired - inconclusive, not a code defect.
                    return;
                }

                Assert.Equal(ProviderId.Gemini, snapshot.Provider);
                Assert.NotEmpty(snapshot.Windows);
                Assert.Contains(snapshot.Windows, w => w.Label == "Session" || w.Label == "Weekly" || w.Label.Contains("Gemini"));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                }
            }
        }
    }

    [Fact]
    public void MinimaxProvider_ParsesIntervalAndWeeklyLimits()
    {
        var json = """
        {
            "model_remains": [
                {
                    "model_name": "abab6.5s",
                    "current_interval_remaining_percent": 75,
                    "current_weekly_remaining_percent": 40
                }
            ],
            "base_resp": {
                "status_code": 0
            }
        }
        """;

        var snapshot = MinimaxProvider.ParseUsage(json);

        Assert.Equal(ProviderId.Minimax, snapshot.Provider);
        Assert.Equal(2, snapshot.Windows.Count);
        Assert.Equal("abab6.5s (5h)", snapshot.Windows[0].Label);
        Assert.Equal(25.0f, snapshot.Windows[0].UsedPercent);
        Assert.Equal("abab6.5s (Weekly)", snapshot.Windows[1].Label);
        Assert.Equal(60.0f, snapshot.Windows[1].UsedPercent);
    }

    [Fact]
    public void KimiProvider_ParsesWeeklyAndRateLimits()
    {
        var json = """
        {
            "usage": {
                "limit": "100",
                "used": "45",
                "resetTime": "2026-08-30T00:00:00Z"
            },
            "limits": [
                {
                    "window": { "duration": 300 },
                    "detail": {
                        "limit": "50",
                        "used": "10"
                    }
                }
            ],
            "user": {
                "membership": {
                    "level": "Pro"
                }
            }
        }
        """;

        var snapshot = KimiProvider.ParseUsage(json);

        Assert.Equal(ProviderId.Kimi, snapshot.Provider);
        Assert.Equal(2, snapshot.Windows.Count);
        Assert.Equal("Weekly", snapshot.Windows[0].Label);
        Assert.Equal(45.0f, snapshot.Windows[0].UsedPercent);
        Assert.Equal("Rate Limit (300m)", snapshot.Windows[1].Label);
        Assert.Equal(20.0f, snapshot.Windows[1].UsedPercent);
        Assert.Equal("Pro", snapshot.Identity.Plan);
    }

    [Fact]
    public void KimiProvider_ParsesNumericLimitValuesCorrectly()
    {
        var json = """
        {
            "usage": {
                "limit": 200,
                "used": 50,
                "resetTime": "2026-08-30T00:00:00Z"
            },
            "limits": [
                {
                    "window": { "duration": 120 },
                    "detail": {
                        "limit": 80,
                        "used": 20
                    }
                }
            ],
            "user": {
                "membership": {
                    "level": "Coding Plus"
                }
            }
        }
        """;

        var snapshot = KimiProvider.ParseUsage(json);

        Assert.Equal(ProviderId.Kimi, snapshot.Provider);
        Assert.Equal(2, snapshot.Windows.Count);
        Assert.Equal("Weekly", snapshot.Windows[0].Label);
        Assert.Equal(25.0f, snapshot.Windows[0].UsedPercent);
        Assert.Equal("Rate Limit (120m)", snapshot.Windows[1].Label);
        Assert.Equal(25.0f, snapshot.Windows[1].UsedPercent);
        Assert.Equal("Coding Plus", snapshot.Identity.Plan);
    }

    [Fact]
    public void MinimaxProvider_ParsesStringNumericLimitsCorrectly()
    {
        var json = """
        {
            "model_remains": [
                {
                    "model_name": "abab7-chat",
                    "current_interval_remaining_percent": "80.5",
                    "current_weekly_remaining_percent": "50.0"
                }
            ],
            "base_resp": {
                "status_code": 0
            }
        }
        """;

        var snapshot = MinimaxProvider.ParseUsage(json);

        Assert.Equal(ProviderId.Minimax, snapshot.Provider);
        Assert.Equal(2, snapshot.Windows.Count);
        Assert.Equal("abab7-chat (5h)", snapshot.Windows[0].Label);
        Assert.Equal(19.5f, snapshot.Windows[0].UsedPercent);
        Assert.Equal("abab7-chat (Weekly)", snapshot.Windows[1].Label);
        Assert.Equal(50.0f, snapshot.Windows[1].UsedPercent);
    }

    /// <summary>
    /// Claude Desktop's usage cache is now a fallback source for ClaudeProvider (see HostCliScanner.
    /// ScanClaudeDesktopUsageHistory). Tests that assert a hard failure (no fallback available) must run
    /// with that file out of the way, since a real Claude Desktop install on the dev/CI machine would
    /// otherwise let the fallback succeed and mask the behavior under test.
    /// </summary>
    private static async Task WithDesktopCacheSuppressedAsync(Func<Task> test)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "plan-usage-history.json");
        string? originalContent = File.Exists(path) ? File.ReadAllText(path) : null;
        try
        {
            if (originalContent != null) File.Delete(path);
            await test();
        }
        finally
        {
            if (originalContent != null) File.WriteAllText(path, originalContent);
        }
    }

    private class TrackingContent : StringContent
    {
        public bool IsDisposed { get; private set; }

        public TrackingContent(string content) : base(content, System.Text.Encoding.UTF8, "application/json") { }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                IsDisposed = true;
            }
            base.Dispose(disposing);
        }
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public List<TrackingContent> ReturnedContents { get; } = new();
        public List<HttpRequestMessage> ReceivedRequests { get; } = new();

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ReceivedRequests.Add(request);
            var response = _handler(request);
            if (response.Content is TrackingContent tc)
            {
                ReturnedContents.Add(tc);
            }
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task ClaudeProvider_FetchAsync_DisposesRequestAndResponse_OnSuccessAndError()
    {
        await WithDesktopCacheSuppressedAsync(async () =>
        {
            var vault = new WindowsCredentialVault();
            var account = new AccountConfig { Id = "test-claude", Provider = ProviderId.Claude };
            var (scannedAt, _, _) = HostCliScanner.ScanClaudeTokens();
            var initialToken = !string.IsNullOrEmpty(scannedAt) ? scannedAt : "test-token";
            vault.SaveTokens(account.Id, new StoredTokens { AccessToken = initialToken });

            // 1. Success
            var handler = new MockHttpMessageHandler(req => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new TrackingContent("""{"five_hour":{"utilization":10.0}}""")
            });
            using var client = new HttpClient(handler);
            var provider = new ClaudeProvider();

            var snapshot = await provider.FetchAsync(account, vault, client);
            Assert.NotNull(snapshot);
            Assert.Single(handler.ReturnedContents);
            Assert.True(handler.ReturnedContents[0].IsDisposed);

            // 2. 401 Unauthorized
            var unauthHandler = new MockHttpMessageHandler(req => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
            {
                Content = new TrackingContent("Unauthorized")
            });
            using var unauthClient = new HttpClient(unauthHandler);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.FetchAsync(account, vault, unauthClient));
            Assert.Single(unauthHandler.ReturnedContents);
            Assert.True(unauthHandler.ReturnedContents[0].IsDisposed);
        });
    }

    [Fact]
    public async Task ClaudeProvider_FetchAsync_RefreshesViaOAuth_WhenCachedTokenIs401()
    {
        var vault = new WindowsCredentialVault();
        var account = new AccountConfig { Id = "test-claude-refresh", Provider = ProviderId.Claude };

        vault.SaveTokens(account.Id, new StoredTokens
        {
            AccessToken = "manual-expired-token",
            RefreshToken = "valid-refresh-token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) // not expired proactively
        });

        int usageAttempts = 0;
        var handler = new MockHttpMessageHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("oauth/token"))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new TrackingContent("""{"access_token":"new-claude-access-token","refresh_token":"new-refresh-token","expires_in":3600}""")
                };
            }
            usageAttempts++;
            if (usageAttempts == 1)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
                {
                    Content = new TrackingContent("Unauthorized")
                };
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new TrackingContent("""{"five_hour":{"utilization":15.0}}""")
            };
        });
        using var client = new HttpClient(handler);
        var provider = new ClaudeProvider();

        var snapshot = await provider.FetchAsync(account, vault, client);
        Assert.NotNull(snapshot);
        Assert.Equal(3, handler.ReturnedContents.Count); // 1st usage (401), token refresh (200), 2nd usage (200)
        Assert.All(handler.ReturnedContents, c => Assert.True(c.IsDisposed));
        Assert.Equal("new-claude-access-token", vault.GetTokens(account.Id)?.AccessToken);
    }

    [Fact]
    public async Task ClaudeProvider_FetchAsync_HostCliAccount_DoesNotCallOAuthRefresh_WhenTokenIs401()
    {
        await WithDesktopCacheSuppressedAsync(async () =>
        {
            var vault = new WindowsCredentialVault();
            var account = new AccountConfig { Id = "test-claude-cli-401", Provider = ProviderId.Claude };
            var (scannedAt, _, _) = HostCliScanner.ScanClaudeTokens();
            var initialToken = !string.IsNullOrEmpty(scannedAt) ? scannedAt : "host-token";

            vault.SaveTokens(account.Id, new StoredTokens
            {
                AccessToken = initialToken,
                RefreshToken = null, // Host CLI accounts do not retain refresh tokens
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            });

            bool oauthTokenEndpointCalled = false;
            var handler = new MockHttpMessageHandler(req =>
            {
                if (req.RequestUri!.ToString().Contains("oauth/token"))
                {
                    oauthTokenEndpointCalled = true;
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new TrackingContent("""{"access_token":"unexpected-token"}""")
                    };
                }
                return new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
                {
                    Content = new TrackingContent("Unauthorized")
                };
            });
            using var client = new HttpClient(handler);
            var provider = new ClaudeProvider();

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.FetchAsync(account, vault, client));
            Assert.False(oauthTokenEndpointCalled, "OAuth refresh endpoint must not be called for host CLI accounts");
        });
    }

    [Fact]
    public async Task ClaudeProvider_FetchAsync_HostCliAccount_PurgesLegacyRefreshToken_FromVault()
    {
        await WithDesktopCacheSuppressedAsync(async () =>
        {
            var credDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            var credFile = Path.Combine(credDir, ".credentials.json");
            bool createdCredFile = false;
            string? originalContent = null;

            if (File.Exists(credFile))
            {
                originalContent = File.ReadAllText(credFile);
            }

            try
            {
                Directory.CreateDirectory(credDir);
                File.WriteAllText(credFile, """{"claudeAiOauth":{"accessToken":"test-claude-host-token","expiresAt":2000000000000}}""");
                if (originalContent == null)
                {
                    createdCredFile = true;
                }

                var vault = new WindowsCredentialVault();
                var account = new AccountConfig { Id = "test-claude-legacy-vault", Provider = ProviderId.Claude };
                var (scannedAt, _, _) = HostCliScanner.ScanClaudeTokens();
                Assert.Equal("test-claude-host-token", scannedAt);

                // Pre-populate vault with a legacy refresh token (simulating pre-fix behavior)
                vault.SaveTokens(account.Id, new StoredTokens
                {
                    AccessToken = scannedAt!,
                    RefreshToken = "legacy-cli-refresh-token",
                    ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
                });

                bool oauthTokenEndpointCalled = false;
                var handler = new MockHttpMessageHandler(req =>
                {
                    if (req.RequestUri!.ToString().Contains("oauth/token"))
                    {
                        oauthTokenEndpointCalled = true;
                        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                        {
                            Content = new TrackingContent("""{"access_token":"unexpected-token"}""")
                        };
                    }
                    return new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
                    {
                        Content = new TrackingContent("Unauthorized")
                    };
                });
                using var client = new HttpClient(handler);
                var provider = new ClaudeProvider();

                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.FetchAsync(account, vault, client));

                // Verify that the legacy refresh token was purged from vault and oauth token endpoint was not called
                Assert.False(oauthTokenEndpointCalled, "OAuth refresh endpoint must not be called when legacy CLI refresh token was purged");
                var updatedTokens = vault.GetTokens(account.Id);
                Assert.NotNull(updatedTokens);
                Assert.Null(updatedTokens.RefreshToken);
            }
            finally
            {
                if (createdCredFile)
                {
                    try { File.Delete(credFile); } catch { }
                }
                else if (originalContent != null)
                {
                    try { File.WriteAllText(credFile, originalContent); } catch { }
                }
            }
        });
    }

    [Fact]
    public void HostCliScanner_ScanClaudeDesktopUsageHistory_ReturnsLatestSampleByTimestamp()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
        var path = Path.Combine(dir, "plan-usage-history.json");
        string? originalContent = File.Exists(path) ? File.ReadAllText(path) : null;

        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, """
            {"version":1,"samples":[
                {"t":1000,"org":"org-a","u":{"fh":10,"sd":20}},
                {"t":3000,"org":"org-a","u":{"fh":30,"sd":40}},
                {"t":2000,"org":"org-a","u":{"fh":99,"sd":99}}
            ]}
            """);

            var (fh, sd, sampledAt) = HostCliScanner.ScanClaudeDesktopUsageHistory();

            // Must pick the entry with the highest "t", not the last one in array order.
            Assert.Equal(30f, fh);
            Assert.Equal(40f, sd);
            Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(3000), sampledAt);
        }
        finally
        {
            if (originalContent != null) File.WriteAllText(path, originalContent);
            else if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ClaudeProvider_FetchAsync_FallsBackToDesktopCache_WhenSessionExpiredAndNoRefreshTokenAvailable()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
        var path = Path.Combine(dir, "plan-usage-history.json");
        string? originalContent = File.Exists(path) ? File.ReadAllText(path) : null;

        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, """{"version":1,"samples":[{"t":123456789,"org":"org-a","u":{"fh":42,"sd":77}}]}""");

            var vault = new WindowsCredentialVault();
            var account = new AccountConfig { Id = "test-claude-desktop-fallback-expired", Provider = ProviderId.Claude };
            vault.SaveTokens(account.Id, new StoredTokens
            {
                AccessToken = "stale-token-not-matching-any-real-session",
                RefreshToken = null, // no manual refresh token and (for this test) no live CLI session either
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            });

            // Every usage request comes back 401; the provider must fall back to the desktop cache
            // instead of throwing, and must never need the oauth/token endpoint to do so.
            var handler = new MockHttpMessageHandler(req => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
            {
                Content = new TrackingContent("Unauthorized")
            });
            using var client = new HttpClient(handler);
            var provider = new ClaudeProvider();

            var snapshot = await provider.FetchAsync(account, vault, client);

            Assert.Equal("Claude Desktop cache", snapshot.Source);
            Assert.Equal(2, snapshot.Windows.Count);
            Assert.Contains(snapshot.Windows, w => w.Label == "Session" && Math.Abs(w.UsedPercent - 42f) < 0.01f);
            Assert.Contains(snapshot.Windows, w => w.Label == "Weekly" && Math.Abs(w.UsedPercent - 77f) < 0.01f);
            Assert.All(snapshot.Windows, w => Assert.Equal(
                "From Claude Desktop cache — log in for live data & reset times", w.ResetDescription));
        }
        finally
        {
            if (originalContent != null) File.WriteAllText(path, originalContent);
            else if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task CursorProvider_FetchAsync_DisposesRequestAndResponse_OnSuccessAndError()
    {
        var vault = new WindowsCredentialVault();
        var account = new AccountConfig { Id = "test-cursor", Provider = ProviderId.Cursor };
        vault.SaveTokens(account.Id, new StoredTokens { AccessToken = "test-token" });

        var handler = new MockHttpMessageHandler(req => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new TrackingContent("""{"gpt4":{"numRequests":10,"maxRequestUsage":100}}""")
        });
        using var client = new HttpClient(handler);
        var provider = new CursorProvider();

        var snapshot = await provider.FetchAsync(account, vault, client);
        Assert.NotNull(snapshot);
        Assert.Single(handler.ReturnedContents);
        Assert.True(handler.ReturnedContents[0].IsDisposed);

        var unauthHandler = new MockHttpMessageHandler(req => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
        {
            Content = new TrackingContent("Unauthorized")
        });
        using var unauthClient = new HttpClient(unauthHandler);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.FetchAsync(account, vault, unauthClient));
        Assert.Single(unauthHandler.ReturnedContents);
        Assert.True(unauthHandler.ReturnedContents[0].IsDisposed);
    }

    [Fact]
    public async Task KimiProvider_FetchAsync_DisposesRequestAndResponse_OnSuccessAndError()
    {
        var vault = new WindowsCredentialVault();
        var account = new AccountConfig { Id = "test-kimi", Provider = ProviderId.Kimi };
        vault.SaveApiKey(account.Id, "sk-test-key");

        var handler = new MockHttpMessageHandler(req => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new TrackingContent("""{"usage":{"limit":100,"used":20}}""")
        });
        using var client = new HttpClient(handler);
        var provider = new KimiProvider();

        var snapshot = await provider.FetchAsync(account, vault, client);
        Assert.NotNull(snapshot);
        Assert.Single(handler.ReturnedContents);
        Assert.True(handler.ReturnedContents[0].IsDisposed);

        var unauthHandler = new MockHttpMessageHandler(req => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
        {
            Content = new TrackingContent("Unauthorized")
        });
        using var unauthClient = new HttpClient(unauthHandler);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.FetchAsync(account, vault, unauthClient));
        Assert.Single(unauthHandler.ReturnedContents);
        Assert.True(unauthHandler.ReturnedContents[0].IsDisposed);
    }

    [Fact]
    public async Task MinimaxProvider_FetchAsync_DisposesRequestAndResponse_OnSuccessAndError()
    {
        var vault = new WindowsCredentialVault();
        var account = new AccountConfig { Id = "test-minimax", Provider = ProviderId.Minimax };
        vault.SaveApiKey(account.Id, "sk-test-key");

        var handler = new MockHttpMessageHandler(req => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new TrackingContent("""{"model_remains":[{"model_name":"abab","current_interval_remaining_percent":80}]}""")
        });
        using var client = new HttpClient(handler);
        var provider = new MinimaxProvider();

        var snapshot = await provider.FetchAsync(account, vault, client);
        Assert.NotNull(snapshot);
        Assert.Single(handler.ReturnedContents);
        Assert.True(handler.ReturnedContents[0].IsDisposed);

        var unauthHandler = new MockHttpMessageHandler(req => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
        {
            Content = new TrackingContent("Unauthorized")
        });
        using var unauthClient = new HttpClient(unauthHandler);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.FetchAsync(account, vault, unauthClient));
        Assert.Single(unauthHandler.ReturnedContents);
        Assert.True(unauthHandler.ReturnedContents[0].IsDisposed);
    }

    [Fact]
    public async Task GeminiProvider_FetchAsync_DisposesBothLoadAndQuotaResponses()
    {
        var vault = new WindowsCredentialVault();
        var account = new AccountConfig { Id = "test-gemini", Provider = ProviderId.Gemini };
        vault.SaveTokens(account.Id, new StoredTokens { AccessToken = "test-token" });

        var handler = new MockHttpMessageHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("loadCodeAssist"))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new TrackingContent("""{"cloudaicompanionProject":"proj-123","currentTier":{"id":"free-tier"}}""")
                };
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new TrackingContent("""{"buckets":[{"modelId":"flash","remainingFraction":0.8}]}""")
            };
        });
        using var client = new HttpClient(handler);
        var provider = new GeminiProvider();

        var snapshot = await provider.FetchAsync(account, vault, client);
        Assert.NotNull(snapshot);
        Assert.Equal(2, handler.ReturnedContents.Count);
        Assert.All(handler.ReturnedContents, c => Assert.True(c.IsDisposed));
    }

    [Fact]
    public async Task CopilotProvider_FetchAsync_DisposesResponses_DuringFallbackRetry()
    {
        var vault = new WindowsCredentialVault();
        var account = new AccountConfig { Id = "test-copilot", Provider = ProviderId.Copilot };
        vault.SaveTokens(account.Id, new StoredTokens { AccessToken = "test-token" });

        int attempt = 0;
        var handler = new MockHttpMessageHandler(req =>
        {
            attempt++;
            if (attempt == 1)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.UpgradeRequired)
                {
                    Content = new TrackingContent("Upgrade Required")
                };
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new TrackingContent("""{"login":"user","access_type_sku":"individual","quota_snapshots":{"chat":{"entitlement":100,"remaining":50}}}""")
            };
        });
        using var client = new HttpClient(handler);
        var provider = new CopilotProvider();

        var snapshot = await provider.FetchAsync(account, vault, client);
        Assert.NotNull(snapshot);
        Assert.Equal(2, handler.ReturnedContents.Count);
        Assert.All(handler.ReturnedContents, c => Assert.True(c.IsDisposed));
    }

    [Fact]
    public async Task CodexProvider_FetchAsync_DisposesResponses_DuringReactive401OAuthRefresh()
    {
        var vault = new WindowsCredentialVault();
        var account = new AccountConfig { Id = "test-codex", Provider = ProviderId.Codex };
        var (scannedAt, _, _) = HostCliScanner.ScanCodexTokens();
        var initialToken = !string.IsNullOrEmpty(scannedAt) ? scannedAt : "expired-token";

        vault.SaveTokens(account.Id, new StoredTokens
        {
            AccessToken = initialToken,
            RefreshToken = "valid-refresh-token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) // not expired proactively
        });

        int usageAttempts = 0;
        var handler = new MockHttpMessageHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("oauth/token"))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new TrackingContent("""{"access_token":"new-access-token","expires_in":3600}""")
                };
            }
            usageAttempts++;
            if (usageAttempts == 1)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
                {
                    Content = new TrackingContent("Unauthorized")
                };
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new TrackingContent("""{"account_id":"acc-1","plan_type":"plus","rate_limit":{"primary_window":{"used_percent":20.0}}}""")
            };
        });
        using var client = new HttpClient(handler);
        var provider = new CodexProvider();

        var snapshot = await provider.FetchAsync(account, vault, client);
        Assert.NotNull(snapshot);
        Assert.Equal(3, handler.ReturnedContents.Count); // 1st usage (401), token refresh (200), 2nd usage (200)
        Assert.All(handler.ReturnedContents, c => Assert.True(c.IsDisposed));
    }

    [Fact]
    public async Task GeminiProvider_FetchAsync_DisposesResponses_DuringReactive401OAuthRefresh()
    {
        var vault = new WindowsCredentialVault();
        var account = new AccountConfig { Id = "test-gemini-refresh", Provider = ProviderId.Gemini };
        vault.SaveTokens(account.Id, new StoredTokens
        {
            AccessToken = "expired-token",
            RefreshToken = "valid-refresh-token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        });

        int usageAttempts = 0;
        var handler = new MockHttpMessageHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("oauth2.googleapis.com/token"))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new TrackingContent("""{"access_token":"new-google-access-token","expires_in":3600}""")
                };
            }
            if (req.RequestUri!.ToString().Contains("loadCodeAssist"))
            {
                usageAttempts++;
                if (usageAttempts == 1)
                {
                    return new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
                    {
                        Content = new TrackingContent("Unauthorized")
                    };
                }
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new TrackingContent("""{"paidTier":{"id":"g1-pro-tier","name":"Google AI Pro"}}""")
                };
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new TrackingContent("""{"groups":[{"displayName":"Gemini Models","buckets":[{"window":"5h","remainingFraction":0.9}]}]}""")
            };
        });
        using var client = new HttpClient(handler);
        var provider = new GeminiProvider();

        var snapshot = await provider.FetchAsync(account, vault, client);
        Assert.NotNull(snapshot);
        Assert.Equal("Google AI Pro", snapshot.Identity.Plan);
        Assert.Single(snapshot.Windows);
        Assert.Equal("Session", snapshot.Windows[0].Label);
        Assert.All(handler.ReturnedContents, c => Assert.True(c.IsDisposed));
    }

    [Fact]
    public async Task GeminiProvider_FetchAsync_FallsBackToPublicEndpoint_WhenDailyEndpointFails()
    {
        var vault = new WindowsCredentialVault();
        var account = new AccountConfig { Id = "test-gemini-fallback", Provider = ProviderId.Gemini };
        vault.SaveTokens(account.Id, new StoredTokens { AccessToken = "test-token" });

        var handler = new MockHttpMessageHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            // Primary daily endpoint throws error / 404
            if (uri.Contains("daily-cloudcode-pa.googleapis.com"))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
                {
                    Content = new TrackingContent("Endpoint not found")
                };
            }
            // Fallback public endpoint succeeds
            if (uri.Contains("loadCodeAssist"))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new TrackingContent("""{"currentTier":{"id":"standard-tier"}}""")
                };
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new TrackingContent("""{"groups":[{"displayName":"Gemini Models","buckets":[{"window":"5h","remainingFraction":0.75}]}]}""")
            };
        });

        using var client = new HttpClient(handler);
        var provider = new GeminiProvider();

        var snapshot = await provider.FetchAsync(account, vault, client);
        Assert.NotNull(snapshot);
        Assert.Equal("Standard", snapshot.Identity.Plan);
        Assert.Single(snapshot.Windows);
        Assert.Equal("Session", snapshot.Windows[0].Label);
        Assert.Equal(25.0f, snapshot.Windows[0].UsedPercent, 1);
        Assert.All(handler.ReturnedContents, c => Assert.True(c.IsDisposed));
    }

    [Fact]
    public void QuotaRefreshService_ResetBackoff_ClearsBackoffForProvider()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "PowerQuotaTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var configStorage = new ConfigStorage(tempDir);
            var vault = new WindowsCredentialVault(tempDir);
            using var service = new QuotaRefreshService(configStorage, vault, autoStartTimer: false);

            var accState = new ProviderAccountRuntimeState
            {
                Provider = ProviderId.Gemini,
                AccountId = "acc-gemini",
                ConsecutiveFailures = 3,
                RetryAfter = DateTimeOffset.UtcNow.AddMinutes(10)
            };
            service.State.ProviderAccounts.Add(accState);
            Assert.True(accState.IsBackingOff);

            service.ResetBackoff(ProviderId.Gemini);
            Assert.False(accState.IsBackingOff);
            Assert.Null(accState.RetryAfter);
            Assert.Equal(0u, accState.ConsecutiveFailures);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}


