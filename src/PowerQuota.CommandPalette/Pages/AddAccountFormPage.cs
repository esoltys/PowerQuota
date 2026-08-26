using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerQuota.Core.Engine;
using PowerQuota.Core.Models;
using PowerQuota.Core.Storage;

namespace PowerQuota.CommandPalette.Pages;

public class AddAccountFormPage : ListPage
{
    private readonly QuotaRefreshService _refreshService;
    private readonly ConfigStorage _configStorage;
    private readonly WindowsCredentialVault _vault;

    public AddAccountFormPage(QuotaRefreshService refreshService, ConfigStorage configStorage, WindowsCredentialVault vault)
    {
        _refreshService = refreshService;
        _configStorage = configStorage;
        _vault = vault;
        Title = "Add AI Provider Account";
        PlaceholderText = "Select a provider to connect...";
    }

    public override IListItem[] GetItems()
    {
        var items = new List<IListItem>();

        foreach (var provider in ProviderIdExtensions.All)
        {
            items.Add(new ListItem(new AnonymousCommand(() =>
            {
                AutoScanOrConnect(provider);
            }))
            {
                Title = $"Connect {provider.GetLabel()}",
                Subtitle = GetProviderConnectDescription(provider),
                Icon = new IconInfo("\uE710")
            });
        }

        return items.ToArray();
    }

    private string GetProviderConnectDescription(ProviderId provider) => provider switch
    {
        ProviderId.Codex => "Scan local ~/.codex/auth.json or configure account",
        ProviderId.Claude => "Scan local ~/.claude/auth.json credentials",
        ProviderId.Cursor => "Scan local Cursor IDE database (state.vscdb)",
        ProviderId.Gemini => "Scan local ~/.gemini/auth.json credentials",
        ProviderId.Copilot => "Scan local GitHub Copilot credentials",
        ProviderId.Minimax => "Configure Minimax API key",
        ProviderId.Kimi => "Scan OpenCode ~/.opencode/auth.json or configure API key",
        _ => "Connect provider account"
    };

    private void AutoScanOrConnect(ProviderId provider)
    {
        var config = _configStorage.Current;
        var newAccount = new AccountConfig
        {
            Provider = provider,
            Label = $"{provider.GetLabel()} Account",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        config.Accounts.Add(newAccount);
        _configStorage.Save(config);
        _ = _refreshService.RefreshProviderAsync(provider);
    }
}

