using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerQuota.CommandPalette.Providers;
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
        Title = "PowerQuota";
        PlaceholderText = "Search quotas...";

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
                    string baseLabel = accountStates.Count > 1 ? $"{pid.GetLabel()} Quota ({acc.Label})" : $"{pid.GetLabel()} Quota";
                    string title = isCliActive ? $"{baseLabel} [CLI Active]" : baseLabel;

                    var page = new ProviderDetailsPage(pid, _refreshService, _configStorage, _vault);
                    items.Add(new ListItem(new CommandItem(page))
                    {
                        Title = title,
                        Subtitle = statusLine,
                        Icon = ProviderIcons.GetIcon(pid),
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
        }

        if (!items.Any(i => i.Section == "Active AI Quotas"))
        {
            var addAccountPage = new AddAccountFormPage(_refreshService, _configStorage, _vault);
            items.Add(new ListItem(new CommandItem(addAccountPage))
            {
                Title = "No Accounts Configured",
                Subtitle = "Select 'Add AI Account...' below to connect a provider account",
                Icon = new IconInfo("\uE946"),
                Section = "Active AI Quotas"
            });
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

        var addAccPage = new AddAccountFormPage(_refreshService, _configStorage, _vault);
        items.Add(new ListItem(new CommandItem(addAccPage))
        {
            Title = "Add Account...",
            Subtitle = "Connect a new provider account to PowerQuota",
            Icon = new IconInfo("\uE710"),
            Section = "Actions"
        });

        var settingsPage = new SettingsFormPage(_configStorage, _refreshService);
        items.Add(new ListItem(new CommandItem(settingsPage))
        {
            Title = "Settings",
            Subtitle = "Configure refresh intervals, quota display style, and dock bands",
            Icon = new IconInfo("\uE713"),
            Section = "Actions"
        });

        items.Add(new ListItem(new AnonymousCommand(() =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://github.com/esoltys/PowerQuota",
                    UseShellExecute = true
                });
            }
            catch { }
        }))
        {
            Title = "GitHub Repository",
            Subtitle = "Open github.com/esoltys/PowerQuota in browser",
            Icon = new IconInfo("\uE8A7"),
            Section = "Actions"
        });

        return items.ToArray();
    }
}

