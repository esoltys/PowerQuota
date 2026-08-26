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
            Title = "Refresh All AI Quotas",
            Subtitle = "Query all providers for current quota metrics",
            Icon = new IconInfo("\uE72C")
        });

        commands.Add(new CommandItem(_addAccountPage)
        {
            Title = "Add AI Account...",
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
        // Pinned quota cards are driven directly by user-pinned ListItems from ProviderDetailsPage
        return Array.Empty<ICommandItem>();
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

