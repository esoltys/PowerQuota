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

                var snapshot = await provider.FetchAsync(account, vault, client);
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

                var snapshot = await provider.FetchAsync(account, vault, client);
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
        var vault = new WindowsCredentialVault();
        var account = new AccountConfig { Id = "test-claude", Provider = ProviderId.Claude };
        vault.SaveTokens(account.Id, new StoredTokens { AccessToken = "test-token" });

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
}


