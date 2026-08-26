using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
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
        Title = $"{_provider.GetLabel()} Quota & Accounts";
        PlaceholderText = $"Filter {_provider.GetLabel()} quotas...";

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
                        Icon = new IconInfo(window.UsedPercent > 90 ? "\uE783" : "\uE945"),
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
                items.Add(new ListItem(new AnonymousCommand(() =>
                {
                    _ = _refreshService.RefreshProviderAsync(_provider);
                }))
                {
                    Title = acc.Label,
                    Subtitle = acc.Error ?? "Not refreshed yet",
                    Icon = new IconInfo("\uE783")
                });
            }
        }

        // Action items
        items.Add(new ListItem(new AnonymousCommand(() =>
        {
            _ = _refreshService.RefreshProviderAsync(_provider);
        }))
        {
            Title = "Refresh Quota Now",
            Subtitle = "Query the provider API for updated quotas",
            Icon = new IconInfo("\uE72C")
        });

        return items.ToArray();
    }
}

