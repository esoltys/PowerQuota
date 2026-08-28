using Xunit;
using PowerQuota.Core.Engine;
using PowerQuota.Core.Models;
using PowerQuota.Core.Storage;

namespace PowerQuota.Core.Tests;

public class StorageAndEngineTests : IDisposable
{
    private readonly string _testDir;
    private readonly ConfigStorage _storage;
    private readonly WindowsCredentialVault _vault;

    public StorageAndEngineTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "PowerQuotaTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDir);
        _storage = new ConfigStorage(_testDir);
        _vault = new WindowsCredentialVault(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try
            {
                Directory.Delete(_testDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors during test teardown
            }
        }
    }

    [Fact]
    public void CredentialVault_EncryptsAndDecryptsTokensAndApiKeys()
    {
        var accountId = "test-acc-123";

        var tokens = new StoredTokens
        {
            AccessToken = "sec_access_token_xyz",
            RefreshToken = "sec_refresh_token_abc",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(2),
            TokenId = "user_456"
        };

        _vault.SaveTokens(accountId, tokens);
        _vault.SaveApiKey(accountId, "sk-minimax-key-789");

        var retrievedTokens = _vault.GetTokens(accountId);
        var retrievedKey = _vault.GetApiKey(accountId);

        Assert.NotNull(retrievedTokens);
        Assert.Equal("sec_access_token_xyz", retrievedTokens!.AccessToken);
        Assert.Equal("sec_refresh_token_abc", retrievedTokens.RefreshToken);
        Assert.Equal("user_456", retrievedTokens.TokenId);
        Assert.Equal("sk-minimax-key-789", retrievedKey);

        // Verify isolation and reload persistence from the isolated directory
        var reloadedVault = new WindowsCredentialVault(_testDir);
        var reloadedTokens = reloadedVault.GetTokens(accountId);
        var reloadedKey = reloadedVault.GetApiKey(accountId);
        Assert.NotNull(reloadedTokens);
        Assert.Equal("sec_access_token_xyz", reloadedTokens!.AccessToken);
        Assert.Equal("sk-minimax-key-789", reloadedKey);

        _vault.RemoveAccount(accountId);
        Assert.Null(_vault.GetTokens(accountId));
        Assert.Null(_vault.GetApiKey(accountId));

        var afterRemovalVault = new WindowsCredentialVault(_testDir);
        Assert.Null(afterRemovalVault.GetTokens(accountId));
        Assert.Null(afterRemovalVault.GetApiKey(accountId));
    }

    [Fact]
    public void PowerQuotaCommandProvider_TopLevelCommands_And_DockBands_HaveStableIds()
    {
        _storage.Mutate(c => c.Accounts.Add(new AccountConfig
        {
            Id = "acc-claude-test",
            Provider = ProviderId.Claude,
            Label = "Claude Pro"
        }));

        var refreshService = new QuotaRefreshService(_storage, _vault, autoStartTimer: false);
        var provider = new PowerQuota.CommandPalette.Providers.PowerQuotaCommandProvider(_storage, _vault, refreshService);
        
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
        _storage.Mutate(c => c.Accounts.Add(new AccountConfig
        {
            Id = "acc-claude-test",
            Provider = ProviderId.Claude,
            Label = "Claude Pro"
        }));

        var refreshService = new QuotaRefreshService(_storage, _vault, autoStartTimer: false);
        var provider = new PowerQuota.CommandPalette.Providers.PowerQuotaCommandProvider(_storage, _vault, refreshService);
        
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
        var cfg = _storage.Current;

        cfg.RefreshIntervalMinutes = 15;
        cfg.DisplayRemainingNotUsed = true;
        cfg.DockDisplayMode = DockDisplayMode.PercentageOnly;

        _storage.Save(cfg);

        var reloaded = new ConfigStorage(_testDir);
        Assert.Equal(15, reloaded.Current.RefreshIntervalMinutes);
        Assert.True(reloaded.Current.DisplayRemainingNotUsed);
        Assert.Equal(DockDisplayMode.PercentageOnly, reloaded.Current.DockDisplayMode);
    }

    [Fact]
    public void ConfigStorage_Current_ReturnsIsolatedClone()
    {
        var storage = new ConfigStorage(_testDir);
        var snapshot1 = storage.Current;
        snapshot1.RefreshIntervalMinutes = 999;
        snapshot1.Accounts.Add(new AccountConfig { Id = "ghost-account", Provider = ProviderId.Claude });

        var snapshot2 = storage.Current;
        Assert.NotEqual(999, snapshot2.RefreshIntervalMinutes);
        Assert.DoesNotContain(snapshot2.Accounts, a => a.Id == "ghost-account");
    }

    [Fact]
    public void ConfigStorage_Mutate_CoordinatesConcurrentUpdatesWithoutDataLoss()
    {
        var storage = new ConfigStorage(_testDir);
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

        var reloaded = new ConfigStorage(_testDir);
        Assert.Equal(threadCount, reloaded.Current.Accounts.Count);
    }

    [Fact]
    public void ConfigStorage_AtomicPersistence_CleansUpTempFilesAndPreservesValidConfig()
    {
        var storage = new ConfigStorage(_testDir);
        storage.Mutate(cfg =>
        {
            cfg.RefreshIntervalMinutes = 10;
            cfg.Accounts.Add(new AccountConfig { Id = "test-1", Provider = ProviderId.Gemini });
        });

        Assert.True(File.Exists(_storage.ConfigFilePath));
        var tmpFiles = Directory.GetFiles(_testDir, "*.tmp");
        Assert.Empty(tmpFiles);

        var loaded = new ConfigStorage(_testDir);
        Assert.Equal(10, loaded.Current.RefreshIntervalMinutes);
        Assert.Single(loaded.Current.Accounts);
    }

    [Fact]
    public void QuotaRefreshService_InitializesStateForAllProviders()
    {
        using var service = new QuotaRefreshService(_storage, _vault);

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
    public void PowerQuotaExtension_GetProvider_ReturnsCommandProvider_AndHandlesLoggingSafely()
    {
        var extension = new PowerQuota.CommandPalette.PowerQuotaExtension();
        var provider = extension.GetProvider(Microsoft.CommandPalette.Extensions.ProviderType.Commands);
        Assert.NotNull(provider);

        var nullProvider = extension.GetProvider((Microsoft.CommandPalette.Extensions.ProviderType)999);
        Assert.Null(nullProvider);
    }

    [Fact]
    public void PowerQuotaExtension_Dispose_SignalsDisposedEvent()
    {
        var extension = new PowerQuota.CommandPalette.PowerQuotaExtension();
        extension.Dispose();
        Assert.True(PowerQuota.CommandPalette.PowerQuotaExtension.DisposedEvent.WaitOne(0));
    }
}
