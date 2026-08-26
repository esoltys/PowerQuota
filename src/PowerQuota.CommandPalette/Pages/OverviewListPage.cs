using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerQuota.Core.Engine;
using PowerQuota.Core.Models;
using PowerQuota.Core.Storage;

namespace PowerQuota.CommandPalette.Pages;

public class OverviewListPage : ListPage
{
    private readonly QuotaRefreshService _refreshService;
    private readonly ConfigStorage _configStorage;
    private readonly WindowsCredentialVault _vault;

    public OverviewListPage(QuotaRefreshService refreshService, ConfigStorage configStorage, WindowsCredentialVault vault)
    {
        _refreshService = refreshService;
        _configStorage = configStorage;
        _vault = vault;
        Title = "PowerQuota AI Quotas";
        PlaceholderText = "Search AI coding quotas (Claude, Codex, Cursor, Gemini, Copilot, Minimax, Kimi)...";

        _refreshService.StateChanged += (_, _) => RaiseItemsChanged(GetItems().Length);
    }

    public override IListItem[] GetItems()
    {
        var items = new List<IListItem>();
        var config = _configStorage.Current;

        // Section 1: Active Provider Quotas
        foreach (var pid in ProviderIdExtensions.All)
        {
            if (!config.EnabledProviders.Contains(pid)) continue;

            var pState = _refreshService.State.Providers.FirstOrDefault(p => p.Provider == pid);
            var accountStates = _refreshService.State.ProviderAccounts.Where(a => a.Provider == pid).ToList();

            if (accountStates.Count > 0)
            {
                foreach (var acc in accountStates)
                {
                    string statusLine = acc.GetStatusLine(config.DisplayRemainingNotUsed);
                    bool isCliActive = pState?.SystemActiveAccountId == acc.AccountId;
                    string title = isCliActive ? $"{pid.GetLabel()} ({acc.Label}) [CLI Active]" : $"{pid.GetLabel()} ({acc.Label})";

                    string iconGlyph = acc.Health == ProviderHealth.Error ? "\uE783" :
                                       acc.Snapshot?.HeadlineWindow?.UsedPercent > 80 ? "\uE7BA" : "\uE945";

                    items.Add(new ListItem(new AnonymousCommand(() =>
                    {
                        // Open details page for this provider
                    }))
                    {
                        Title = title,
                        Subtitle = statusLine,
                        Icon = new IconInfo(iconGlyph),
                        Section = "Active AI Quotas",
                        MoreCommands = new IContextItem[]
                        {
                            new CommandContextItem(new CopyTextCommand($"{title}: {statusLine}"))
                            {
                                Title = "Copy Status"
                            },
                            new CommandContextItem(new AnonymousCommand(() =>
                            {
                                _ = _refreshService.RefreshProviderAsync(pid);
                            }))
                            {
                                Title = "Refresh"
                            }
                        }
                    });
                }
            }
            else
            {
                items.Add(new ListItem(new AnonymousCommand(() =>
                {
                    _ = _refreshService.RefreshProviderAsync(pid);
                }))
                {
                    Title = pid.GetLabel(),
                    Subtitle = "No accounts configured • Click to scan local session",
                    Icon = new IconInfo("\uE710"),
                    Section = "Active AI Quotas"
                });
            }
        }

        // Section 2: Management & Actions
        items.Add(new ListItem(new AnonymousCommand(() =>
        {
            _ = _refreshService.RefreshAllAsync();
        }))
        {
            Title = "Refresh All Quotas",
            Subtitle = "Update usage metrics from all active AI providers",
            Icon = new IconInfo("\uE72C"),
            Section = "Actions"
        });

        items.Add(new ListItem(new AnonymousCommand(() => { }))
        {
            Title = "Add Account...",
            Subtitle = "Connect a new account for Claude, Codex, Cursor, Gemini, Copilot, Minimax, or Kimi",
            Icon = new IconInfo("\uE710"),
            Section = "Actions"
        });

        items.Add(new ListItem(new AnonymousCommand(() => { }))
        {
            Title = "Settings",
            Subtitle = "Configure refresh intervals, quota display style, and dock bands",
            Icon = new IconInfo("\uE713"),
            Section = "Actions"
        });

        return items.ToArray();
    }
}

