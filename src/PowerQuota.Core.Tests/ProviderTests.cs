using Xunit;
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
        Assert.Equal("plus", snapshot.Identity.Plan);
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
        Assert.Equal("team", snapshot.Identity.Plan);
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
        Assert.Equal(13814.0f, snapshot.ExtraUsage.UsedPercent);
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
    public void HostCliScanner_GetCopilotActiveToken_DetectsToken()
    {
        var token = HostCliScanner.GetCopilotActiveToken();
        Assert.NotNull(token);
        Assert.NotEmpty(token);
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
}


