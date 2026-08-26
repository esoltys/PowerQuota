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
        PlaceholderText = "Select a setting to configure...";
    }

    public override IListItem[] GetItems()
    {
        var config = _configStorage.Current;
        var items = new List<IListItem>();

        // 1. Dock Band Style
        string dockModeLabel = config.DockDisplayMode switch
        {
            DockDisplayMode.LogoAndPercentage => "Logo and Percentage",
            DockDisplayMode.PercentageOnly => "Percentage Only",
            DockDisplayMode.BarsOnly => "Usage Bars Only",
            _ => "Logo and Percentage"
        };

        var dockStylePage = new SettingChoicePage(
            title: "Dock Band Style",
            choices: new[]
            {
                new SettingChoice("Logo and Percentage", "Display provider icon and percentage text (e.g. ✦ 76% left)", config.DockDisplayMode == DockDisplayMode.LogoAndPercentage, () =>
                {
                    config.DockDisplayMode = DockDisplayMode.LogoAndPercentage;
                    Save(config);
                }),
                new SettingChoice("Percentage Only", "Display percentage text without brand icon", config.DockDisplayMode == DockDisplayMode.PercentageOnly, () =>
                {
                    config.DockDisplayMode = DockDisplayMode.PercentageOnly;
                    Save(config);
                }),
                new SettingChoice("Usage Bars Only", "Display compact visual progress bars only", config.DockDisplayMode == DockDisplayMode.BarsOnly, () =>
                {
                    config.DockDisplayMode = DockDisplayMode.BarsOnly;
                    Save(config);
                })
            }
        );

        items.Add(new ListItem(new CommandItem(dockStylePage))
        {
            Title = "Dock Band Style",
            Subtitle = $"Current: {dockModeLabel} • Click to configure",
            Icon = new IconInfo("\uE7B5")
        });

        // 2. Quota Display Format
        string quotaFormatLabel = config.DisplayRemainingNotUsed ? "Remaining Quota (e.g. 76% left)" : "Used Quota (e.g. 24% used)";
        var quotaFormatPage = new SettingChoicePage(
            title: "Quota Percentage Format",
            choices: new[]
            {
                new SettingChoice("Remaining Quota (e.g. 76% left)", "Display how much quota is available", config.DisplayRemainingNotUsed, () =>
                {
                    config.DisplayRemainingNotUsed = true;
                    Save(config);
                }),
                new SettingChoice("Used Quota (e.g. 24% used)", "Display how much quota has been consumed", !config.DisplayRemainingNotUsed, () =>
                {
                    config.DisplayRemainingNotUsed = false;
                    Save(config);
                })
            }
        );

        items.Add(new ListItem(new CommandItem(quotaFormatPage))
        {
            Title = "Quota Percentage Format",
            Subtitle = $"Current: {quotaFormatLabel} • Click to configure",
            Icon = new IconInfo("\uE945")
        });

        // 3. Reset Time Format
        string resetTimeLabel = config.ShowRelativeResetTimes ? "Relative Countdown (e.g. in 2h 15m)" : "Absolute Clock Time (e.g. Friday at 2:00 PM)";
        var resetTimePage = new SettingChoicePage(
            title: "Reset Time Format",
            choices: new[]
            {
                new SettingChoice("Relative Countdown (e.g. in 2h 15m)", "Show dynamic countdown until quota resets", config.ShowRelativeResetTimes, () =>
                {
                    config.ShowRelativeResetTimes = true;
                    Save(config);
                }),
                new SettingChoice("Absolute Clock Time (e.g. Friday at 2:00 PM)", "Show local clock time when quota resets", !config.ShowRelativeResetTimes, () =>
                {
                    config.ShowRelativeResetTimes = false;
                    Save(config);
                })
            }
        );

        items.Add(new ListItem(new CommandItem(resetTimePage))
        {
            Title = "Reset Time Format",
            Subtitle = $"Current: {resetTimeLabel} • Click to configure",
            Icon = new IconInfo("\uE823")
        });

        // 4. Auto-Refresh Interval
        var refreshIntervalPage = new SettingChoicePage(
            title: "Auto-Refresh Interval",
            choices: new[]
            {
                new SettingChoice("Every 1 Minute", "Poll provider APIs every minute", config.RefreshIntervalMinutes == 1, () => { config.RefreshIntervalMinutes = 1; Save(config); }),
                new SettingChoice("Every 5 Minutes", "Poll provider APIs every 5 minutes", config.RefreshIntervalMinutes == 5, () => { config.RefreshIntervalMinutes = 5; Save(config); }),
                new SettingChoice("Every 15 Minutes", "Poll provider APIs every 15 minutes (recommended)", config.RefreshIntervalMinutes == 15, () => { config.RefreshIntervalMinutes = 15; Save(config); }),
                new SettingChoice("Every 30 Minutes", "Poll provider APIs every 30 minutes", config.RefreshIntervalMinutes == 30, () => { config.RefreshIntervalMinutes = 30; Save(config); }),
                new SettingChoice("Every 60 Minutes", "Poll provider APIs every 60 minutes", config.RefreshIntervalMinutes == 60, () => { config.RefreshIntervalMinutes = 60; Save(config); })
            }
        );

        items.Add(new ListItem(new CommandItem(refreshIntervalPage))
        {
            Title = "Auto-Refresh Interval",
            Subtitle = $"Current: Every {config.RefreshIntervalMinutes} minute(s) • Click to configure",
            Icon = new IconInfo("\uE72C")
        });

        return items.ToArray();
    }

    private void Save(PowerQuotaConfig config)
    {
        _configStorage.Save(config);
        RaiseItemsChanged(GetItems().Length);
        _ = _refreshService.RefreshAllAsync();
    }
}

public class SettingChoice
{
    public string Title { get; }
    public string Subtitle { get; }
    public bool IsSelected { get; }
    public Action SelectAction { get; }

    public SettingChoice(string title, string subtitle, bool isSelected, Action selectAction)
    {
        Title = title;
        Subtitle = subtitle;
        IsSelected = isSelected;
        SelectAction = selectAction;
    }
}

public class SettingChoicePage : ListPage
{
    private readonly IEnumerable<SettingChoice> _choices;

    public SettingChoicePage(string title, IEnumerable<SettingChoice> choices)
    {
        Title = title;
        PlaceholderText = $"Select an option for {title}...";
        _choices = choices;
    }

    public override IListItem[] GetItems()
    {
        var items = new List<IListItem>();
        foreach (var choice in _choices)
        {
            items.Add(new ListItem(new AnonymousCommand(() =>
            {
                choice.SelectAction();
            }))
            {
                Title = choice.Title,
                Subtitle = choice.Subtitle,
                Icon = new IconInfo(choice.IsSelected ? "\uE73E" : "\uE739") // Checkmark badge
            });
        }
        return items.ToArray();
    }
}
