using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerQuota.CommandPalette.Pages;
using PowerQuota.Core.Engine;
using PowerQuota.Core.Models;
using PowerQuota.Core.Storage;

namespace PowerQuota.CommandPalette.Providers;

public class PowerQuotaCommandProvider : CommandProvider
{
    private readonly ConfigStorage _configStorage = new();
    private readonly WindowsCredentialVault _vault = new();
    private readonly QuotaRefreshService _refreshService;
    private readonly OverviewListPage _overviewPage;
    private readonly AddAccountFormPage _addAccountPage;
    private readonly SettingsFormPage _settingsPage;

    public PowerQuotaCommandProvider()
    {
        Id = "PowerQuota.CommandPalette";
        DisplayName = "PowerQuota";
        Icon = new IconInfo("\uE945");

        _refreshService = new QuotaRefreshService(_configStorage, _vault);
        _overviewPage = new OverviewListPage(_refreshService, _configStorage, _vault);
        _addAccountPage = new AddAccountFormPage(_refreshService, _configStorage, _vault);
        _settingsPage = new SettingsFormPage(_configStorage, _refreshService);

        _refreshService.StateChanged += (_, _) =>
        {
            RaiseItemsChanged(TopLevelCommands().Length);
        };
    }

    public override ICommandItem[] TopLevelCommands()
    {
        var logPath = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "PowerQuota", "extension.log");
        System.IO.File.AppendAllText(logPath, $"[{System.DateTime.UtcNow:O}] TopLevelCommands called\n");

        var commands = new List<ICommandItem>();
        var config = _configStorage.Current;

        // 1. Primary entrypoint: PowerQuota Overview
        commands.Add(new CommandItem(_overviewPage)
        {
            Title = "PowerQuota",
            Subtitle = "Monitor Claude, Codex, Cursor, Gemini, Copilot, Minimax, and Kimi usage",
            Icon = ProviderIcons.GetIcon()
        });

        // 2. Individual provider items for quick access (only if accounts exist)
        foreach (var pid in ProviderIdExtensions.All)
        {
            if (!config.EnabledProviders.Contains(pid)) continue;

            var acc = _refreshService.State.ProviderAccounts.FirstOrDefault(a => a.Provider == pid);
            if (acc == null && !config.Accounts.Any(a => a.Provider == pid)) continue;

            string statusLine = acc?.GetStatusLine(config.DisplayRemainingNotUsed) ?? "Ready";

            var page = new ProviderDetailsPage(pid, _refreshService, _configStorage, _vault);
            commands.Add(new CommandItem(page)
            {
                Title = $"{pid.GetLabel()} Quota",
                Subtitle = statusLine,
                Icon = ProviderIcons.GetIcon(pid),
                MoreCommands = new IContextItem[]
                {
                    new CommandContextItem(new AnonymousCommand(() =>
                    {
                        _ = _refreshService.RefreshProviderAsync(pid);
                    }))
                    {
                        Title = "Refresh Quota"
                    }
                }
            });
        }

        // 3. Quick Action Commands
        commands.Add(new CommandItem(new AnonymousCommand(() =>
        {
            _ = _refreshService.RefreshAllAsync();
        }))
        {
            Title = "Refresh All Quotas",
            Subtitle = "Query all providers for current quota metrics",
            Icon = new IconInfo("\uE72C")
        });

        commands.Add(new CommandItem(_addAccountPage)
        {
            Title = "Add Account...",
            Subtitle = "Connect a new provider account to PowerQuota",
            Icon = new IconInfo("\uE710")
        });

        commands.Add(new CommandItem(_settingsPage)
        {
            Title = "PowerQuota Settings",
            Subtitle = "Configure display options and intervals",
            Icon = new IconInfo("\uE713")
        });

        return commands.ToArray();
    }

    public override ICommandItem[] GetDockBands()
    {
        var bands = new List<ICommandItem>();
        var config = _configStorage.Current;

        foreach (var pid in ProviderIdExtensions.All)
        {
            if (!config.EnabledProviders.Contains(pid)) continue;

            var accounts = _refreshService.State.ProviderAccounts.Where(a => a.Provider == pid).ToList();
            if (accounts.Count == 0 && !config.Accounts.Any(a => a.Provider == pid)) continue;

            foreach (var acc in accounts)
            {
                if (acc.Snapshot is { } snapshot && snapshot.Windows.Count > 0)
                {
                    foreach (var window in snapshot.Windows)
                    {
                        float percent = config.DisplayRemainingNotUsed ? Math.Clamp(100f - window.UsedPercent, 0f, 100f) : window.UsedPercent;
                        string pctLabel = config.DisplayRemainingNotUsed ? $"{percent:0}% left" : $"{percent:0}% used";

                        string resetFormatted = window.FormatResetText(config.ShowRelativeResetTimes);
                        string resetPhrase;
                        if (string.IsNullOrEmpty(resetFormatted))
                        {
                            resetPhrase = window.Label;
                        }
                        else if (resetFormatted.StartsWith("Resets ", StringComparison.OrdinalIgnoreCase))
                        {
                            resetPhrase = $"{window.Label} resets {resetFormatted.Substring(7)}";
                        }
                        else
                        {
                            resetPhrase = $"{window.Label} {resetFormatted.ToLowerInvariant()}";
                        }

                        var subtitleParts = new List<string> { resetPhrase };
                        if (!string.IsNullOrEmpty(window.ResetDescription))
                        {
                            subtitleParts.Add(window.ResetDescription);
                        }
                        if (accounts.Count > 1)
                        {
                            subtitleParts.Add(acc.Label);
                        }

                        string subtitle = string.Join(" • ", subtitleParts);

                        string itemTitle = config.DockDisplayMode switch
                        {
                            DockDisplayMode.BarsOnly => GetProgressBar(percent, 8),
                            _ => pctLabel
                        };

                        IIconInfo? icon = config.DockDisplayMode switch
                        {
                            DockDisplayMode.PercentageOnly => null,
                            _ => ProviderIcons.GetIcon(pid)
                        };

                        var page = new ProviderDetailsPage(pid, _refreshService, _configStorage, _vault);
                        bands.Add(new CommandItem(page)
                        {
                            Title = itemTitle,
                            Subtitle = subtitle,
                            Icon = icon,
                            MoreCommands = new IContextItem[]
                            {
                                new CommandContextItem(new AnonymousCommand(() =>
                                {
                                    _ = _refreshService.RefreshProviderAsync(pid);
                                }))
                                {
                                    Title = "Refresh Quota"
                                }
                            }
                        });
                    }
                }
                else
                {
                    string status = acc?.GetStatusLine(config.DisplayRemainingNotUsed) ?? "Ready";
                    var page = new ProviderDetailsPage(pid, _refreshService, _configStorage, _vault);
                    bands.Add(new CommandItem(page)
                    {
                        Title = $"{pid.GetLabel()} Quota",
                        Subtitle = status,
                        Icon = ProviderIcons.GetIcon(pid)
                    });
                }
            }
        }

        return bands.ToArray();
    }

    private static string GetProgressBar(float percent, int totalBlocks = 8)
    {
        int filled = (int)Math.Round((percent / 100f) * totalBlocks);
        filled = Math.Clamp(filled, 0, totalBlocks);
        return new string('▰', filled) + new string('▱', totalBlocks - filled);
    }

    public override ICommandItem? GetCommandItem(string id)
    {
        return TopLevelCommands().FirstOrDefault(c => c.Title.Contains(id, StringComparison.OrdinalIgnoreCase));
    }

    public override void Dispose()
    {
        _refreshService.Dispose();
        base.Dispose();
    }
}

