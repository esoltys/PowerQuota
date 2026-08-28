using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerQuota.CommandPalette.Pages;
using PowerQuota.Core.Engine;
using PowerQuota.Core.Models;
using PowerQuota.Core.Storage;

namespace PowerQuota.CommandPalette.Providers;

public class PowerQuotaCommandProvider : CommandProvider
{
    private readonly ConfigStorage _configStorage;
    private readonly WindowsCredentialVault _vault;
    private readonly QuotaRefreshService _refreshService;
    private readonly OverviewListPage _overviewPage;
    private readonly AddAccountFormPage _addAccountPage;
    private readonly SettingsFormPage _settingsPage;
    private readonly Dictionary<ProviderId, ProviderDetailsPage> _providerPages = new();

    private ProviderDetailsPage GetOrCreateProviderPage(ProviderId pid)
    {
        if (!_providerPages.TryGetValue(pid, out var page))
        {
            page = new ProviderDetailsPage(pid, _refreshService, _configStorage, _vault);
            _providerPages[pid] = page;
        }
        return page;
    }

    public PowerQuotaCommandProvider()
        : this(new ConfigStorage(), new WindowsCredentialVault(), null)
    {
    }

    public PowerQuotaCommandProvider(ConfigStorage configStorage, WindowsCredentialVault vault, QuotaRefreshService? refreshService = null)
    {
        Id = "PowerQuota.CommandPalette";
        DisplayName = "PowerQuota";
        Icon = new IconInfo("\uE945");

        _configStorage = configStorage;
        _vault = vault;
        _refreshService = refreshService ?? new QuotaRefreshService(_configStorage, _vault);
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
        try
        {
            var dir = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "PowerQuota");
            System.IO.Directory.CreateDirectory(dir);
            var logPath = System.IO.Path.Combine(dir, "extension.log");
            System.IO.File.AppendAllText(logPath, $"[{System.DateTime.UtcNow:O}] TopLevelCommands called\n");
        }
        catch
        {
            // Diagnostic logging failures should never prevent command generation.
        }

        var commands = new List<ICommandItem>();
        var config = _configStorage.Current;

        // 1. Primary entrypoint: PowerQuota Overview
        _overviewPage.Id = "powerquota-overview";
        _overviewPage.Name = "PowerQuota";
        _overviewPage.Title = "PowerQuota";
        _overviewPage.Icon = ProviderIcons.GetIcon();
        commands.Add(new CommandItem(_overviewPage)
        {
            Title = "PowerQuota",
            Subtitle = "Monitor Claude, Codex, Cursor, Gemini, Copilot, Minimax, and Kimi usage",
            Icon = ProviderIcons.GetIcon()
        });

        // 2. Individual provider items and quota window cards for quick access and Dock pinning
        foreach (var pid in ProviderIdExtensions.All)
        {
            if (!config.EnabledProviders.Contains(pid)) continue;

            var accounts = _refreshService.State.ProviderAccounts.Where(a => a.Provider == pid).ToList();
            if (accounts.Count == 0 && !config.Accounts.Any(a => a.Provider == pid)) continue;

            var page = GetOrCreateProviderPage(pid);
            var primaryAcc = accounts.FirstOrDefault();
            string statusLine = primaryAcc?.GetStatusLine(config.DisplayRemainingNotUsed) ?? "Ready";
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
                    })
                    {
                        Id = $"refresh-{pid}",
                        Name = $"Refresh {pid.GetLabel()} Quota",
                        Icon = new IconInfo("\uE72C")
                    })
                    {
                        Title = "Refresh Quota"
                    }
                }
            });

        }

        // 3. Quick Action Commands
        var refreshAllIcon = new IconInfo("\uE72C");
        commands.Add(new CommandItem(new AnonymousCommand(() =>
        {
            _ = _refreshService.RefreshAllAsync();
        })
        {
            Id = "action-refresh-all",
            Name = "Refresh All Quotas",
            Icon = refreshAllIcon
        })
        {
            Title = "Refresh All Quotas",
            Subtitle = "Query all providers for current quota metrics",
            Icon = refreshAllIcon
        });

        _addAccountPage.Id = "action-add-account";
        _addAccountPage.Name = "Add Account";
        _addAccountPage.Icon = new IconInfo("\uE710");
        commands.Add(new CommandItem(_addAccountPage)
        {
            Title = "Add Account...",
            Subtitle = "Connect a new provider account to PowerQuota",
            Icon = new IconInfo("\uE710")
        });

        _settingsPage.Id = "action-settings";
        _settingsPage.Name = "PowerQuota Settings";
        _settingsPage.Icon = new IconInfo("\uE713");
        commands.Add(new CommandItem(_settingsPage)
        {
            Title = "PowerQuota Settings",
            Subtitle = "Configure display options and intervals",
            Icon = new IconInfo("\uE713")
        });

        var githubIcon = new IconInfo("\uE8A7");
        commands.Add(new CommandItem(new AnonymousCommand(() =>
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
        })
        {
            Id = "action-github",
            Name = "PowerQuota GitHub Repository",
            Icon = githubIcon
        })
        {
            Title = "PowerQuota GitHub Repository",
            Subtitle = "Open github.com/esoltys/PowerQuota in browser",
            Icon = githubIcon
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
            var configuredAccounts = config.Accounts.Where(a => a.Provider == pid).ToList();
            if (accounts.Count == 0 && configuredAccounts.Count == 0) continue;

            if (accounts.Count == 0)
            {
                foreach (var cfgAcc in configuredAccounts)
                {
                    string accountKey = string.IsNullOrEmpty(cfgAcc.Id) ? "default" : cfgAcc.Id;
                    var icon = ProviderIcons.GetIcon(pid);
                    var page = new ProviderDetailsPage(pid, _refreshService, _configStorage, _vault)
                    {
                        Id = $"dock-{pid}-{accountKey}-status",
                        Name = $"{pid.GetLabel()} Quota",
                        Title = $"{pid.GetLabel()} Quota",
                        Icon = icon
                    };
                    bands.Add(new CommandItem(page)
                    {
                        Title = $"{pid.GetLabel()} Quota",
                        Subtitle = "Ready",
                        Icon = icon
                    });
                }
                continue;
            }

            foreach (var acc in accounts)
            {
                string accountKey = string.IsNullOrEmpty(acc.AccountId) ? "default" : acc.AccountId;
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

                        string itemTitle = config.DockDisplayMode == DockDisplayMode.Bars
                            ? GetProgressBar(percent, 8)
                            : pctLabel;

                        var icon = ProviderIcons.GetIcon(pid);

                        string bandName = window.Label.StartsWith(pid.GetLabel(), StringComparison.OrdinalIgnoreCase)
                            ? $"{window.Label} Quota"
                            : $"{pid.GetLabel()} - {window.Label} Quota";

                        string sanitizedLabel = window.Label.Replace('/', '-').Replace(' ', '-').Replace("(", "").Replace(")", "");

                        var page = new ProviderDetailsPage(pid, _refreshService, _configStorage, _vault)
                        {
                            Id = $"dock-{pid}-{accountKey}-{sanitizedLabel}",
                            Name = bandName,
                            Title = bandName,
                            Icon = icon
                        };
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
                                })
                                {
                                    Id = $"dock-refresh-{pid}-{accountKey}-{sanitizedLabel}",
                                    Name = $"Refresh {bandName}",
                                    Icon = new IconInfo("\uE72C")
                                })
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
                    var icon = ProviderIcons.GetIcon(pid);
                    var page = new ProviderDetailsPage(pid, _refreshService, _configStorage, _vault)
                    {
                        Id = $"dock-{pid}-{accountKey}-status",
                        Name = $"{pid.GetLabel()} Quota",
                        Title = $"{pid.GetLabel()} Quota",
                        Icon = icon
                    };
                    bands.Add(new CommandItem(page)
                    {
                        Title = $"{pid.GetLabel()} Quota",
                        Subtitle = status,
                        Icon = icon
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
        if (string.IsNullOrWhiteSpace(id)) return null;

        var allItems = TopLevelCommands().Concat(GetDockBands()).ToList();

        // 1. Exact match on Command.Id
        var match = allItems.FirstOrDefault(c => string.Equals(c.Command?.Id, id, StringComparison.OrdinalIgnoreCase));
        if (match != null) return match;

        // 2. Suffix / prefix match on Command.Id (to handle host package/provider prefixing)
        match = allItems.FirstOrDefault(c => !string.IsNullOrEmpty(c.Command?.Id) &&
            (id.EndsWith(c.Command.Id, StringComparison.OrdinalIgnoreCase) || c.Command.Id.EndsWith(id, StringComparison.OrdinalIgnoreCase)));
        if (match != null) return match;

        // 3. Fallback: match by Title
        return allItems.FirstOrDefault(c => c.Title.Contains(id, StringComparison.OrdinalIgnoreCase) || id.Contains(c.Title, StringComparison.OrdinalIgnoreCase));
    }

    public override ICommand? GetCommand(string id)
    {
        return GetCommandItem(id)?.Command;
    }

    public override void Dispose()
    {
        _refreshService.Dispose();
        base.Dispose();
    }
}

