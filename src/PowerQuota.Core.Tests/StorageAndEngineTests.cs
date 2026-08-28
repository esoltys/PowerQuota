using Xunit;
using PowerQuota.Core.Engine;
using PowerQuota.Core.Models;
using PowerQuota.Core.Storage;

namespace PowerQuota.Core.Tests;

public class StorageAndEngineTests
{
    [Fact]
    public void CredentialVault_EncryptsAndDecryptsTokensAndApiKeys()
    {
        var vault = new WindowsCredentialVault();
        var accountId = "test-acc-123";

        var tokens = new StoredTokens
        {
            AccessToken = "sec_access_token_xyz",
            RefreshToken = "sec_refresh_token_abc",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(2),
            TokenId = "user_456"
        };

        vault.SaveTokens(accountId, tokens);
        vault.SaveApiKey(accountId, "sk-minimax-key-789");

        var retrievedTokens = vault.GetTokens(accountId);
        var retrievedKey = vault.GetApiKey(accountId);

        Assert.NotNull(retrievedTokens);
        Assert.Equal("sec_access_token_xyz", retrievedTokens!.AccessToken);
        Assert.Equal("sec_refresh_token_abc", retrievedTokens.RefreshToken);
        Assert.Equal("user_456", retrievedTokens.TokenId);
        Assert.Equal("sk-minimax-key-789", retrievedKey);

        vault.RemoveAccount(accountId);
        Assert.Null(vault.GetTokens(accountId));
        Assert.Null(vault.GetApiKey(accountId));
    }

    [Fact]
    public void PowerQuotaCommandProvider_TopLevelCommands_And_DockBands_HaveStableIds()
    {
        var storage = new ConfigStorage();
        storage.Current.Accounts.Add(new AccountConfig
        {
            Id = "acc-claude-test",
            Provider = ProviderId.Claude,
            Label = "Claude Pro"
        });

        var refreshService = new QuotaRefreshService(storage, new WindowsCredentialVault(), autoStartTimer: false);
        var provider = new PowerQuota.CommandPalette.Providers.PowerQuotaCommandProvider(storage, new WindowsCredentialVault(), refreshService);
        
        var topCommands = provider.TopLevelCommands();
        Assert.NotEmpty(topCommands);
        foreach (var cmd in topCommands)
        {
            Assert.NotNull(cmd.Command);
            Assert.False(string.IsNullOrWhiteSpace(cmd.Command.Id), $"Top level command '{cmd.Title}' is missing Command.Id");
        }

        var dockBands = provider.GetDockBands();
        Assert.NotEmpty(dockBands);
        foreach (var band in dockBands)
        {
            Assert.NotNull(band.Command);
            Assert.False(string.IsNullOrWhiteSpace(band.Command.Id), $"Dock band '{band.Title}' is missing Command.Id");
            Assert.StartsWith("dock-", band.Command.Id);
        }
    }

    [Fact]
    public void PowerQuotaCommandProvider_GetCommandItem_ResolvesByExactId_Prefix_And_Title()
    {
        var storage = new ConfigStorage();
        storage.Current.Accounts.Add(new AccountConfig
        {
            Id = "acc-claude-test",
            Provider = ProviderId.Claude,
            Label = "Claude Pro"
        });

        var refreshService = new QuotaRefreshService(storage, new WindowsCredentialVault(), autoStartTimer: false);
        var provider = new PowerQuota.CommandPalette.Providers.PowerQuotaCommandProvider(storage, new WindowsCredentialVault(), refreshService);
        
        // Exact Id match on top-level overview
        var overviewItem = provider.GetCommandItem("powerquota-overview");
        Assert.NotNull(overviewItem);
        Assert.Equal("PowerQuota", overviewItem!.Title);

        // Host-prefixed Id match on action command
        var prefixedItem = provider.GetCommandItem("39231EricJamesSoltys.PowerQuota_3cwpgnyg4f1v8!App!action-refresh-all");
        Assert.NotNull(prefixedItem);
        Assert.Equal("Refresh All Quotas", prefixedItem!.Title);

        // Dock item match
        var dockItem = provider.GetCommandItem("dock-Claude-acc-claude-test-status");
        Assert.NotNull(dockItem);
        Assert.Equal("dock-Claude-acc-claude-test-status", dockItem!.Command?.Id);

        // Fallback by title
        var byTitleItem = provider.GetCommandItem("Refresh All Quotas");
        Assert.NotNull(byTitleItem);

        // GetCommand resolution
        var cmd = provider.GetCommand("action-settings");
        Assert.NotNull(cmd);
        Assert.Equal("action-settings", cmd!.Id);
    }

    [Fact]
    public void ConfigStorage_LoadsAndSavesSettings()
    {
        var storage = new ConfigStorage();
        var cfg = storage.Current;

        cfg.RefreshIntervalMinutes = 15;
        cfg.DisplayRemainingNotUsed = true;
        cfg.DockDisplayMode = DockDisplayMode.PercentageOnly;

        storage.Save(cfg);

        var reloaded = new ConfigStorage();
        Assert.Equal(15, reloaded.Current.RefreshIntervalMinutes);
        Assert.True(reloaded.Current.DisplayRemainingNotUsed);
        Assert.Equal(DockDisplayMode.PercentageOnly, reloaded.Current.DockDisplayMode);
    }

    [Fact]
    public void QuotaRefreshService_InitializesStateForAllProviders()
    {
        var configStorage = new ConfigStorage();
        var vault = new WindowsCredentialVault();

        using var service = new QuotaRefreshService(configStorage, vault);

        Assert.NotNull(service.State);
        Assert.Equal(7, service.State.Providers.Count);
        Assert.Contains(service.State.Providers, p => p.Provider == ProviderId.Claude);
        Assert.Contains(service.State.Providers, p => p.Provider == ProviderId.Codex);
        Assert.Contains(service.State.Providers, p => p.Provider == ProviderId.Cursor);
        Assert.Contains(service.State.Providers, p => p.Provider == ProviderId.Gemini);
        Assert.Contains(service.State.Providers, p => p.Provider == ProviderId.Copilot);
        Assert.Contains(service.State.Providers, p => p.Provider == ProviderId.Minimax);
        Assert.Contains(service.State.Providers, p => p.Provider == ProviderId.Kimi);
    }

    [Fact]
    public void QuotaRefreshService_RemoveAccount_RemovesStateImmediatelyAndFiresEvent()
    {
        var configStorage = new ConfigStorage();
        configStorage.Current.Accounts.Clear();
        var vault = new WindowsCredentialVault();
        var accountId = "acc-remove-test-1";

        configStorage.Current.Accounts.Add(new AccountConfig
        {
            Id = accountId,
            Provider = ProviderId.Claude,
            Label = "Claude Test"
        });

        using var service = new QuotaRefreshService(configStorage, vault, autoStartTimer: false);

        service.State.ProviderAccounts.Add(new ProviderAccountRuntimeState
        {
            Provider = ProviderId.Claude,
            AccountId = accountId,
            Label = "Claude Test",
            Snapshot = new UsageSnapshot { Provider = ProviderId.Claude }
        });
        service.State.Providers.First(p => p.Provider == ProviderId.Claude).ActiveAccountId = accountId;
        service.State.Providers.First(p => p.Provider == ProviderId.Claude).SystemActiveAccountId = accountId;

        bool eventFired = false;
        service.StateChanged += (sender, state) =>
        {
            eventFired = true;
        };

        configStorage.Current.Accounts.RemoveAll(a => a.Id == accountId);
        service.RemoveAccount(accountId);

        Assert.True(eventFired);
        Assert.DoesNotContain(service.State.ProviderAccounts, a => a.AccountId == accountId);
        Assert.Null(service.State.Providers.First(p => p.Provider == ProviderId.Claude).SystemActiveAccountId);
        Assert.Null(service.State.Providers.First(p => p.Provider == ProviderId.Claude).ActiveAccountId);
    }

    [Fact]
    public void QuotaRefreshService_RemoveAccount_FallsBackToRemainingConfiguredAccount()
    {
        var configStorage = new ConfigStorage();
        configStorage.Current.Accounts.Clear();
        var vault = new WindowsCredentialVault();
        var accountId1 = "acc-remove-1";
        var accountId2 = "acc-remaining-2";

        configStorage.Current.Accounts.Add(new AccountConfig
        {
            Id = accountId1,
            Provider = ProviderId.Claude,
            Label = "Claude Account 1"
        });
        configStorage.Current.Accounts.Add(new AccountConfig
        {
            Id = accountId2,
            Provider = ProviderId.Claude,
            Label = "Claude Account 2"
        });

        using var service = new QuotaRefreshService(configStorage, vault, autoStartTimer: false);

        service.State.ProviderAccounts.Add(new ProviderAccountRuntimeState
        {
            Provider = ProviderId.Claude,
            AccountId = accountId1,
            Label = "Claude Account 1"
        });
        service.State.ProviderAccounts.Add(new ProviderAccountRuntimeState
        {
            Provider = ProviderId.Claude,
            AccountId = accountId2,
            Label = "Claude Account 2"
        });
        service.State.Providers.First(p => p.Provider == ProviderId.Claude).ActiveAccountId = accountId1;

        configStorage.Current.Accounts.RemoveAll(a => a.Id == accountId1);
        service.RemoveAccount(accountId1);

        Assert.DoesNotContain(service.State.ProviderAccounts, a => a.AccountId == accountId1);
        Assert.Contains(service.State.ProviderAccounts, a => a.AccountId == accountId2);
        Assert.Equal(accountId2, service.State.Providers.First(p => p.Provider == ProviderId.Claude).ActiveAccountId);
    }

    [Fact]
    public void QuotaRefreshService_ReconcileAccounts_PrunesOrphanedAccounts()
    {
        var configStorage = new ConfigStorage();
        var vault = new WindowsCredentialVault();
        var validAccountId = "acc-valid-1";
        var orphanAccountId = "acc-orphan-2";

        configStorage.Current.Accounts.Add(new AccountConfig
        {
            Id = validAccountId,
            Provider = ProviderId.Claude,
            Label = "Claude Valid"
        });

        using var service = new QuotaRefreshService(configStorage, vault, autoStartTimer: false);

        service.State.ProviderAccounts.Add(new ProviderAccountRuntimeState
        {
            Provider = ProviderId.Claude,
            AccountId = validAccountId,
            Label = "Claude Valid"
        });
        service.State.ProviderAccounts.Add(new ProviderAccountRuntimeState
        {
            Provider = ProviderId.Claude,
            AccountId = orphanAccountId,
            Label = "Claude Orphan"
        });

        service.ReconcileAccounts();

        Assert.Single(service.State.ProviderAccounts);
        Assert.Equal(validAccountId, service.State.ProviderAccounts[0].AccountId);
    }

    [Fact]
    public async Task QuotaRefreshService_RefreshProviderAsync_PrunesOrphanedAccounts()
    {
        var configStorage = new ConfigStorage();
        configStorage.Current.Accounts.Clear();
        var vault = new WindowsCredentialVault();
        var validAccountId = "acc-valid-refresh";
        var orphanAccountId = "acc-orphan-refresh";

        configStorage.Current.Accounts.Add(new AccountConfig
        {
            Id = validAccountId,
            Provider = ProviderId.Claude,
            Label = "Claude Valid"
        });

        using var service = new QuotaRefreshService(configStorage, vault, autoStartTimer: false);

        service.State.ProviderAccounts.Add(new ProviderAccountRuntimeState
        {
            Provider = ProviderId.Claude,
            AccountId = validAccountId,
            Label = "Claude Valid"
        });
        service.State.ProviderAccounts.Add(new ProviderAccountRuntimeState
        {
            Provider = ProviderId.Claude,
            AccountId = orphanAccountId,
            Label = "Claude Orphan"
        });

        await service.RefreshProviderAsync(ProviderId.Claude);

        Assert.DoesNotContain(service.State.ProviderAccounts, a => a.AccountId == orphanAccountId);
        Assert.Contains(service.State.ProviderAccounts, a => a.AccountId == validAccountId);
    }

    [Fact]
    public void ProviderDetailsPage_RemoveAccount_RemovesFromConfigVaultAndRuntimeState()
    {
        var configStorage = new ConfigStorage();
        var vault = new WindowsCredentialVault();
        var accountId = "acc-page-remove-test";

        configStorage.Current.Accounts.Add(new AccountConfig
        {
            Id = accountId,
            Provider = ProviderId.Minimax,
            Label = "Minimax Test"
        });
        vault.SaveApiKey(accountId, "sk-test-key");

        using var service = new QuotaRefreshService(configStorage, vault, autoStartTimer: false);

        service.State.ProviderAccounts.Add(new ProviderAccountRuntimeState
        {
            Provider = ProviderId.Minimax,
            AccountId = accountId,
            Label = "Minimax Test",
            Error = "Key required"
        });

        var page = new PowerQuota.CommandPalette.Pages.ProviderDetailsPage(ProviderId.Minimax, service, configStorage, vault);
        var items = page.GetItems();
        Assert.Single(items);

        var removeContextItem = items[0].MoreCommands?.OfType<Microsoft.CommandPalette.Extensions.Toolkit.CommandContextItem>()
            .FirstOrDefault(c => c.Title == "Remove Account");
        Assert.NotNull(removeContextItem);

        var invokable = removeContextItem!.Command as Microsoft.CommandPalette.Extensions.IInvokableCommand;
        Assert.NotNull(invokable);
        invokable!.Invoke(null!);

        Assert.DoesNotContain(configStorage.Current.Accounts, a => a.Id == accountId);
        Assert.Null(vault.GetApiKey(accountId));
        Assert.DoesNotContain(service.State.ProviderAccounts, a => a.AccountId == accountId);

        var updatedItems = page.GetItems();
        Assert.Single(updatedItems);
        Assert.Equal("No accounts configured", updatedItems[0].Title);
    }
}

