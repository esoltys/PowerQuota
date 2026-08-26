using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerQuota.Core.Engine;
using PowerQuota.Core.Models;
using PowerQuota.Core.Storage;

namespace PowerQuota.CommandPalette.Pages;

public class SettingsFormPage : ListPage
{
    private readonly ConfigStorage _configStorage;
    private readonly QuotaRefreshService _refreshService;

    public SettingsFormPage(ConfigStorage configStorage, QuotaRefreshService refreshService)
    {
        _configStorage = configStorage;
        _refreshService = refreshService;
        Title = "PowerQuota Settings";
        PlaceholderText = "Filter settings...";
    }

    public override IListItem[] GetItems()
    {
        var config = _configStorage.Current;
        var items = new List<IListItem>();

        // 1. Quota Display Mode toggle (Used % vs Remaining %)
        items.Add(new ListItem(new AnonymousCommand(() =>
        {
            config.DisplayRemainingNotUsed = !config.DisplayRemainingNotUsed;
            _configStorage.Save(config);
            RaiseItemsChanged(GetItems().Length);
            _ = _refreshService.RefreshAllAsync();
        }))
        {
            Title = config.DisplayRemainingNotUsed ? "Display Mode: Remaining Quota %" : "Display Mode: Used Quota %",
            Subtitle = "Click to toggle between percentage used vs percentage remaining",
            Icon = new IconInfo(config.DisplayRemainingNotUsed ? "\uE945" : "\uE713")
        });

        // 2. Relative vs Absolute reset times
        items.Add(new ListItem(new AnonymousCommand(() =>
        {
            config.ShowRelativeResetTimes = !config.ShowRelativeResetTimes;
            _configStorage.Save(config);
            RaiseItemsChanged(GetItems().Length);
        }))
        {
            Title = config.ShowRelativeResetTimes ? "Reset Times: Relative (e.g. in 2h 15m)" : "Reset Times: Absolute (e.g. 4:00 PM)",
            Subtitle = "Click to toggle relative countdowns vs absolute clock times",
            Icon = new IconInfo("\uE823")
        });

        // 3. Dock display mode
        items.Add(new ListItem(new AnonymousCommand(() =>
        {
            config.DockDisplayMode = config.DockDisplayMode switch
            {
                DockDisplayMode.LogoAndPercentage => DockDisplayMode.PercentageOnly,
                DockDisplayMode.PercentageOnly => DockDisplayMode.BarsOnly,
                _ => DockDisplayMode.LogoAndPercentage
            };
            _configStorage.Save(config);
            RaiseItemsChanged(GetItems().Length);
        }))
        {
            Title = $"Dock Band Style: {config.DockDisplayMode}",
            Subtitle = "Click to cycle through Logo+Percentage, Percentage Only, and Bars Only",
            Icon = new IconInfo("\uE7B5")
        });

        // 4. Refresh Interval toggle
        items.Add(new ListItem(new AnonymousCommand(() =>
        {
            config.RefreshIntervalMinutes = config.RefreshIntervalMinutes switch
            {
                1 => 5,
                5 => 15,
                15 => 30,
                30 => 60,
                _ => 1
            };
            _configStorage.Save(config);
            RaiseItemsChanged(GetItems().Length);
        }))
        {
            Title = $"Auto-Refresh Interval: Every {config.RefreshIntervalMinutes} minute(s)",
            Subtitle = "Click to cycle: 1m -> 5m -> 15m -> 30m -> 60m",
            Icon = new IconInfo("\uE72C")
        });

        return items.ToArray();
    }
}

