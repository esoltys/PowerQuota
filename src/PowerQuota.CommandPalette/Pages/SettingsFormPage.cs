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
        string dockModeLabel = config.DockDisplayMode == DockDisplayMode.Bars ? "Usage Bars" : "Percentage";

        var dockStylePage = new SettingChoicePage(
            title: "Dock Band Style",
            choicesProvider: () =>
            {
                var cur = _configStorage.Current;
                return new[]
                {
                    new SettingChoice("Percentage", "Display percentage text (e.g. 76% left)", cur.DockDisplayMode == DockDisplayMode.Percentage, () =>
                    {
                        cur.DockDisplayMode = DockDisplayMode.Percentage;
                        Save(cur);
                    }),
                    new SettingChoice("Usage Bars", "Display visual progress bars (e.g. ▰▰▰▰▰▰▱▱)", cur.DockDisplayMode == DockDisplayMode.Bars, () =>
                    {
                        cur.DockDisplayMode = DockDisplayMode.Bars;
                        Save(cur);
                    })
                };
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
            choicesProvider: () =>
            {
                var cur = _configStorage.Current;
                return new[]
                {
                    new SettingChoice("Remaining Quota (e.g. 76% left)", "Display how much quota is available", cur.DisplayRemainingNotUsed, () =>
                    {
                        cur.DisplayRemainingNotUsed = true;
                        Save(cur);
                    }),
                    new SettingChoice("Used Quota (e.g. 24% used)", "Display how much quota has been consumed", !cur.DisplayRemainingNotUsed, () =>
                    {
                        cur.DisplayRemainingNotUsed = false;
                        Save(cur);
                    })
                };
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
            choicesProvider: () =>
            {
                var cur = _configStorage.Current;
                return new[]
                {
                    new SettingChoice("Relative Countdown (e.g. in 2h 15m)", "Show dynamic countdown until quota resets", cur.ShowRelativeResetTimes, () =>
                    {
                        cur.ShowRelativeResetTimes = true;
                        Save(cur);
                    }),
                    new SettingChoice("Absolute Clock Time (e.g. Friday at 2:00 PM)", "Show local clock time when quota resets", !cur.ShowRelativeResetTimes, () =>
                    {
                        cur.ShowRelativeResetTimes = false;
                        Save(cur);
                    })
                };
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
            choicesProvider: () =>
            {
                var cur = _configStorage.Current;
                return new[]
                {
                    new SettingChoice("Every 1 Minute", "Poll provider APIs every minute", cur.RefreshIntervalMinutes == 1, () => { cur.RefreshIntervalMinutes = 1; Save(cur); }),
                    new SettingChoice("Every 5 Minutes", "Poll provider APIs every 5 minutes", cur.RefreshIntervalMinutes == 5, () => { cur.RefreshIntervalMinutes = 5; Save(cur); }),
                    new SettingChoice("Every 15 Minutes", "Poll provider APIs every 15 minutes (recommended)", cur.RefreshIntervalMinutes == 15, () => { cur.RefreshIntervalMinutes = 15; Save(cur); }),
                    new SettingChoice("Every 30 Minutes", "Poll provider APIs every 30 minutes", cur.RefreshIntervalMinutes == 30, () => { cur.RefreshIntervalMinutes = 30; Save(cur); }),
                    new SettingChoice("Every 60 Minutes", "Poll provider APIs every 60 minutes", cur.RefreshIntervalMinutes == 60, () => { cur.RefreshIntervalMinutes = 60; Save(cur); })
                };
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
        _refreshService.NotifyStateChanged();
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
    private readonly Func<IReadOnlyList<SettingChoice>> _choicesProvider;

    public SettingChoicePage(string title, Func<IReadOnlyList<SettingChoice>> choicesProvider)
    {
        Title = title;
        PlaceholderText = $"Select an option for {title}...";
        _choicesProvider = choicesProvider;
    }

    public override IListItem[] GetItems()
    {
        var items = new List<IListItem>();
        foreach (var choice in _choicesProvider())
        {
            items.Add(new ListItem(new AnonymousCommand(() =>
            {
                choice.SelectAction();
                RaiseItemsChanged(GetItems().Length);
            }))
            {
                Title = choice.Title,
                Subtitle = choice.Subtitle,
                Icon = new IconInfo(choice.IsSelected ? "\uE73E" : "\uE739")
            });
        }
        return items.ToArray();
    }
}
