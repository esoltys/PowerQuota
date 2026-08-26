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
        // 1. Multi-day reset (e.g. 45 hours away)
        var futureReset = DateTimeOffset.UtcNow.AddHours(45.5);
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
}

