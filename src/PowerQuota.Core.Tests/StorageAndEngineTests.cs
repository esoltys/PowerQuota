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
        storage.Mutate(c => c.Accounts.Add(new AccountConfig
        {
            Id = "acc-claude-test",
            Provider = ProviderId.Claude,
            Label = "Claude Pro"
        }));

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
        storage.Mutate(c => c.Accounts.Add(new AccountConfig
        {
            Id = "acc-claude-test",
            Provider = ProviderId.Claude,
            Label = "Claude Pro"
        }));

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
        var tempFile = Path.Combine(Path.GetTempPath(), $"pq_test_{Guid.NewGuid():N}.json");
        try
        {
            var storage = new ConfigStorage(tempFile);
            var cfg = storage.Current;

            cfg.RefreshIntervalMinutes = 15;
            cfg.DisplayRemainingNotUsed = true;
            cfg.DockDisplayMode = DockDisplayMode.PercentageOnly;

            storage.Save(cfg);

            var reloaded = new ConfigStorage(tempFile);
            Assert.Equal(15, reloaded.Current.RefreshIntervalMinutes);
            Assert.True(reloaded.Current.DisplayRemainingNotUsed);
            Assert.Equal(DockDisplayMode.PercentageOnly, reloaded.Current.DockDisplayMode);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ConfigStorage_Current_ReturnsIsolatedClone()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"pq_test_{Guid.NewGuid():N}.json");
        try
        {
            var storage = new ConfigStorage(tempFile);
            var snapshot1 = storage.Current;
            snapshot1.RefreshIntervalMinutes = 999;
            snapshot1.Accounts.Add(new AccountConfig { Id = "ghost-account", Provider = ProviderId.Claude });

            var snapshot2 = storage.Current;
            Assert.NotEqual(999, snapshot2.RefreshIntervalMinutes);
            Assert.DoesNotContain(snapshot2.Accounts, a => a.Id == "ghost-account");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ConfigStorage_Mutate_CoordinatesConcurrentUpdatesWithoutDataLoss()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"pq_test_{Guid.NewGuid():N}.json");
        try
        {
            var storage = new ConfigStorage(tempFile);
            const int threadCount = 30;

            Parallel.For(0, threadCount, i =>
            {
                storage.Mutate(cfg =>
                {
                    cfg.Accounts.Add(new AccountConfig
                    {
                        Id = $"acc-{i}",
                        Provider = ProviderId.Claude,
                        Label = $"Account {i}"
                    });
                });
            });

            Assert.Equal(threadCount, storage.Current.Accounts.Count);

            var reloaded = new ConfigStorage(tempFile);
            Assert.Equal(threadCount, reloaded.Current.Accounts.Count);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ConfigStorage_AtomicPersistence_CleansUpTempFilesAndPreservesValidConfig()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"pq_dir_{Guid.NewGuid():N}");
        var tempFile = Path.Combine(tempDir, "config.json");
        try
        {
            var storage = new ConfigStorage(tempFile);
            storage.Mutate(cfg =>
            {
                cfg.RefreshIntervalMinutes = 10;
                cfg.Accounts.Add(new AccountConfig { Id = "test-1", Provider = ProviderId.Gemini });
            });

            Assert.True(File.Exists(tempFile));
            var tmpFiles = Directory.GetFiles(tempDir, "*.tmp");
            Assert.Empty(tmpFiles);

            var loaded = new ConfigStorage(tempFile);
            Assert.Equal(10, loaded.Current.RefreshIntervalMinutes);
            Assert.Single(loaded.Current.Accounts);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
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
}

