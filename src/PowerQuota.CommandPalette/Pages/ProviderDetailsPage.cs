using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerQuota.CommandPalette.Providers;
using PowerQuota.Core.Engine;
using PowerQuota.Core.Models;
using PowerQuota.Core.Storage;

namespace PowerQuota.CommandPalette.Pages;

public class ProviderDetailsPage : ListPage
{
    private readonly ProviderId _provider;
    private readonly QuotaRefreshService _refreshService;
    private readonly ConfigStorage _configStorage;
    private readonly WindowsCredentialVault _vault;

    public ProviderDetailsPage(ProviderId provider, QuotaRefreshService refreshService, ConfigStorage configStorage, WindowsCredentialVault vault)
    {
        _provider = provider;
        _refreshService = refreshService;
        _configStorage = configStorage;
        _vault = vault;
        Title = $"{provider.GetLabel()} AI Quotas";
        PlaceholderText = $"Filter {provider.GetLabel()} quotas...";

        _refreshService.StateChanged += (_, _) => RaiseItemsChanged(GetItems().Length);
    }

    public override IListItem[] GetItems()
    {
        var items = new List<IListItem>();
        var config = _configStorage.Current;
        var pState = _refreshService.State.Providers.FirstOrDefault(p => p.Provider == _provider);
        var accounts = _refreshService.State.ProviderAccounts.Where(a => a.Provider == _provider).ToList();

        if (accounts.Count == 0)
        {
            items.Add(new ListItem(new AnonymousCommand(() => { }))
            {
                Title = "No accounts configured",
                Subtitle = "Click 'Add Account' to set up credentials for this provider.",
                Icon = new IconInfo("\uE711")
            });
        }

        foreach (var acc in accounts)
        {
            var isSystemActive = pState?.SystemActiveAccountId == acc.AccountId;
            string title = isSystemActive ? $"{acc.Label} [CLI Active]" : acc.Label;

            if (acc.Snapshot is { } snapshot)
            {
                foreach (var window in snapshot.Windows)
                {
                    float percent = config.DisplayRemainingNotUsed ? Math.Clamp(100f - window.UsedPercent, 0f, 100f) : window.UsedPercent;
                    string pctLabel = config.DisplayRemainingNotUsed ? $"{percent:0}% left" : $"{percent:0}% used";

                    string resetText = string.Empty;
                    if (window.ResetAt.HasValue)
                    {
                        var span = window.ResetAt.Value - DateTimeOffset.UtcNow;
                        resetText = span.TotalSeconds > 0
                            ? $" • Resets in {(int)span.TotalHours}h {span.Minutes}m"
                            : " • Resetting soon";
                    }

                    items.Add(new ListItem(new AnonymousCommand(() =>
                    {
                        _ = _refreshService.RefreshProviderAsync(_provider);
                    }))
                    {
                        Title = $"{window.Label}: {pctLabel}",
                        Subtitle = $"{title}{resetText} • {window.ResetDescription ?? ""}",
                        Icon = ProviderIcons.GetIcon(_provider),
                        MoreCommands = new IContextItem[]
                        {
                            new CommandContextItem(new CopyTextCommand($"{window.Label}: {pctLabel}{resetText}"))
                            {
                                Title = "Copy Quota Text"
                            },
                            new CommandContextItem(new AnonymousCommand(() =>
                            {
                                _ = _refreshService.RefreshProviderAsync(_provider);
                            }))
                            {
                                Title = "Refresh Quota"
                            }
                        }
                    });
                }

                if (snapshot.ProviderCost is { } cost)
                {
                    items.Add(new ListItem(new NoOpCommand())
                    {
                        Title = $"Credits / Cost: {cost.Used:N2} {cost.Units}",
                        Subtitle = $"Account: {snapshot.Identity.Email ?? acc.Label}",
                        Icon = new IconInfo("\uE825")
                    });
                }

                if (snapshot.Identity.Plan is { } plan && !string.IsNullOrEmpty(plan))
                {
                    items.Add(new ListItem(new NoOpCommand())
                    {
                        Title = $"Plan: {plan}",
                        Subtitle = $"Email: {snapshot.Identity.Email ?? "N/A"}",
                        Icon = new IconInfo("\uE77B")
                    });
                }
            }
            else
            {
                string guidance = GetLoginGuidance(_provider, acc.Error);
                string itemTitle = accounts.Count > 1 ? $"{_provider.GetLabel()} Quota ({acc.Label})" : $"{_provider.GetLabel()} Quota";
                items.Add(new ListItem(new AnonymousCommand(() =>
                {
                    _ = _refreshService.RefreshProviderAsync(_provider);
                }))
                {
                    Title = itemTitle,
                    Subtitle = guidance,
                    Icon = ProviderIcons.GetIcon(_provider),
                    MoreCommands = new IContextItem[]
                    {
                        new CommandContextItem(new AnonymousCommand(() =>
                        {
                            _ = _refreshService.RefreshProviderAsync(_provider);
                        }))
                        {
                            Title = "Refresh / Scan"
                        },
                        new CommandContextItem(new AnonymousCommand(() =>
                        {
                            config.Accounts.RemoveAll(a => a.Id == acc.AccountId);
                            _configStorage.Save(config);
                            _vault.RemoveAccount(acc.AccountId);
                            _ = _refreshService.RefreshAllAsync();
                        }))
                        {
                            Title = "Remove Account"
                        }
                    }
                });
            }
        }

        return items.ToArray();
    }

    private static string GetLoginGuidance(ProviderId provider, string? error)
    {
        if (!string.IsNullOrEmpty(error) && error.StartsWith("Rate limited", StringComparison.OrdinalIgnoreCase))
        {
            return $"API Rate Limit Cooldown • {error}";
        }

        return provider switch
        {
            ProviderId.Claude => $"Claude Code CLI: Run 'claude' in terminal to login ({error ?? "Login required"})",
            ProviderId.Codex => $"ChatGPT / Codex CLI: Run 'codex login' in terminal ({error ?? "Login required"})",
            ProviderId.Cursor => $"Cursor IDE: Sign into Cursor to refresh session ({error ?? "Session expired"})",
            ProviderId.Gemini => $"Gemini: Run 'gemini' in terminal ({error ?? "Login required"})",
            ProviderId.Copilot => $"GitHub Copilot: Sign in via VS Code or Copilot CLI ({error ?? "Login required"})",
            ProviderId.Minimax => $"Minimax: Set API key ({error ?? "Key required"})",
            ProviderId.Kimi => $"Kimi: Configure API key or OpenCode auth ({error ?? "Key required"})",
            _ => error ?? "Login required"
        };
    }
}

