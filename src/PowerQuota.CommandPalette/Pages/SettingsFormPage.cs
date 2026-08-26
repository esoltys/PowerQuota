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

        // Section 1: Dock Band Style
        items.Add(CreateOptionItem(
            title: "Logo and Percentage",
            subtitle: "Display provider icon and percentage text (e.g. ✦ 76% left)",
            isSelected: config.DockDisplayMode == DockDisplayMode.LogoAndPercentage,
            section: "Dock Band Style",
            action: () =>
            {
                config.DockDisplayMode = DockDisplayMode.LogoAndPercentage;
                SaveAndRefresh(config);
            }
        ));

        items.Add(CreateOptionItem(
            title: "Percentage Only",
            subtitle: "Display percentage text without brand icon",
            isSelected: config.DockDisplayMode == DockDisplayMode.PercentageOnly,
            section: "Dock Band Style",
            action: () =>
            {
                config.DockDisplayMode = DockDisplayMode.PercentageOnly;
                SaveAndRefresh(config);
            }
        ));

        items.Add(CreateOptionItem(
            title: "Usage Bars Only",
            subtitle: "Display compact visual progress bars only",
            isSelected: config.DockDisplayMode == DockDisplayMode.BarsOnly,
            section: "Dock Band Style",
            action: () =>
            {
                config.DockDisplayMode = DockDisplayMode.BarsOnly;
                SaveAndRefresh(config);
            }
        ));

        // Section 2: Quota Display Format
        items.Add(CreateOptionItem(
            title: "Show Remaining Quota (e.g. 76% left)",
            subtitle: "Display how much quota is available",
            isSelected: config.DisplayRemainingNotUsed,
            section: "Quota Percentage Format",
            action: () =>
            {
                config.DisplayRemainingNotUsed = true;
                SaveAndRefresh(config);
            }
        ));

        items.Add(CreateOptionItem(
            title: "Show Used Quota (e.g. 24% used)",
            subtitle: "Display how much quota has been consumed",
            isSelected: !config.DisplayRemainingNotUsed,
            section: "Quota Percentage Format",
            action: () =>
            {
                config.DisplayRemainingNotUsed = false;
                SaveAndRefresh(config);
            }
        ));

        // Section 3: Reset Time Format
        items.Add(CreateOptionItem(
            title: "Relative Countdown (e.g. Resets in 2h 15m)",
            subtitle: "Show dynamic countdown until quota resets",
            isSelected: config.ShowRelativeResetTimes,
            section: "Reset Time Format",
            action: () =>
            {
                config.ShowRelativeResetTimes = true;
                SaveAndRefresh(config);
            }
        ));

        items.Add(CreateOptionItem(
            title: "Absolute Time (e.g. Resets at 4:00 PM)",
            subtitle: "Show local clock time when quota resets",
            isSelected: !config.ShowRelativeResetTimes,
            section: "Reset Time Format",
            action: () =>
            {
                config.ShowRelativeResetTimes = false;
                SaveAndRefresh(config);
            }
        ));

        // Section 4: Auto-Refresh Interval
        int[] intervals = { 1, 5, 15, 30, 60 };
        foreach (var interval in intervals)
        {
            items.Add(CreateOptionItem(
                title: $"Every {interval} Minute{(interval > 1 ? "s" : "")}",
                subtitle: $"Poll provider APIs in background every {interval} minute(s)",
                isSelected: config.RefreshIntervalMinutes == interval,
                section: "Auto-Refresh Interval",
                action: () =>
                {
                    config.RefreshIntervalMinutes = interval;
                    SaveAndRefresh(config);
                }
            ));
        }

        return items.ToArray();
    }

    private ListItem CreateOptionItem(string title, string subtitle, bool isSelected, string section, Action action)
    {
        string displayTitle = isSelected ? $"✓  {title}" : $"    {title}";
        string iconGlyph = isSelected ? "\uE73E" : "\uE739"; // Checked vs Unchecked box/radio

        return new ListItem(new AnonymousCommand(() =>
        {
            action();
            return CommandResult.KeepOpen();
        }))
        {
            Title = displayTitle,
            Subtitle = subtitle,
            Icon = new IconInfo(iconGlyph),
            Section = section
        };
    }

    private void SaveAndRefresh(PowerQuotaConfig config)
    {
        _configStorage.Save(config);
        RaiseItemsChanged(GetItems().Length);
        _ = _refreshService.RefreshAllAsync();
    }
}
