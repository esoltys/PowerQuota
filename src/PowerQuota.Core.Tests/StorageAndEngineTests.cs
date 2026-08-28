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
        storage.Current.Accounts.Clear();
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
        storage.Current.Accounts.Clear();
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

        using var service = new QuotaRefreshService(configStorage, vault, autoStartTimer: false);

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
    public async Task QuotaRefreshService_PreventsOverlappingRefreshes_SerializesExecution()
    {
        var configStorage = new ConfigStorage();
        configStorage.Current.Accounts.Clear();
        configStorage.Current.Accounts.Add(new AccountConfig
        {
            Id = "acc-claude-concurrency",
            Provider = ProviderId.Claude,
            Label = "Claude Concurrency Test"
        });

        var slowAdapter = new ConcurrencyTestingAdapter(ProviderId.Claude, delayMs: 80);
        using var service = new QuotaRefreshService(configStorage, new WindowsCredentialVault(), autoStartTimer: false);
        service.RegisterAdapter(slowAdapter);

        // Launch multiple concurrent refreshes
        var task1 = service.RefreshAllAsync();
        var task2 = service.RefreshProviderAsync(ProviderId.Claude);
        var task3 = service.RefreshAllAsync();
        var task4 = service.RefreshProviderAsync(ProviderId.Claude);

        await Task.WhenAll(task1, task2, task3, task4);

        // Max concurrent refresh executions must never exceed 1
        Assert.Equal(1, slowAdapter.MaxConcurrent);
        Assert.Equal(4, slowAdapter.TotalInvocations);
    }

    [Fact]
    public async Task QuotaRefreshService_Disposal_CancelsInFlightWorkCleanly()
    {
        var configStorage = new ConfigStorage();
        configStorage.Current.Accounts.Clear();
        configStorage.Current.Accounts.Add(new AccountConfig
        {
            Id = "acc-claude-cancel",
            Provider = ProviderId.Claude,
            Label = "Claude Cancel Test"
        });

        var slowAdapter = new ConcurrencyTestingAdapter(ProviderId.Claude, delayMs: 1000);
        var service = new QuotaRefreshService(configStorage, new WindowsCredentialVault(), autoStartTimer: false);
        service.RegisterAdapter(slowAdapter);

        var refreshTask = service.RefreshAllAsync();
        await Task.Delay(50); // Let refresh start and acquire lock

        service.Dispose();

        // Should complete without throwing unhandled exceptions
        await refreshTask;
    }

    [Fact]
    public async Task QuotaRefreshService_ObservesExceptionsSafely_WithoutCrashing()
    {
        var configStorage = new ConfigStorage();
        configStorage.Current.Accounts.Clear();
        configStorage.Current.Accounts.Add(new AccountConfig
        {
            Id = "acc-failing",
            Provider = ProviderId.Claude,
            Label = "Failing Adapter Test"
        });

        var failingAdapter = new FailingTestingAdapter(ProviderId.Claude);
        using var service = new QuotaRefreshService(configStorage, new WindowsCredentialVault(), autoStartTimer: false);
        service.RegisterAdapter(failingAdapter);

        // Both calls should complete gracefully and not throw unhandled exception
        await service.RefreshProviderAsync(ProviderId.Claude);
        await service.RefreshAllAsync();

        var accState = service.State.ProviderAccounts.FirstOrDefault(a => a.AccountId == "acc-failing");
        Assert.NotNull(accState);
        Assert.Equal(ProviderHealth.Error, accState!.Health);
        Assert.Contains("Simulated network failure", accState.Error);
    }

    private class ConcurrencyTestingAdapter : PowerQuota.Core.Providers.IProviderAdapter
    {
        private int _currentConcurrent;
        private int _maxConcurrent;
        private int _totalInvocations;
        private readonly int _delayMs;

        public ProviderId Id { get; }
        public int MaxConcurrent => _maxConcurrent;
        public int TotalInvocations => _totalInvocations;

        public ConcurrencyTestingAdapter(ProviderId id, int delayMs = 50)
        {
            Id = id;
            _delayMs = delayMs;
        }

        public async Task<UsageSnapshot> FetchAsync(AccountConfig account, WindowsCredentialVault vault, HttpClient client, CancellationToken ct = default)
        {
            int current = Interlocked.Increment(ref _currentConcurrent);
            Interlocked.Increment(ref _totalInvocations);

            int initialMax, computedMax;
            do
            {
                initialMax = _maxConcurrent;
                computedMax = Math.Max(initialMax, current);
            } while (Interlocked.CompareExchange(ref _maxConcurrent, computedMax, initialMax) != initialMax);

            try
            {
                await Task.Delay(_delayMs, ct);
            }
            finally
            {
                Interlocked.Decrement(ref _currentConcurrent);
            }

            return new UsageSnapshot
            {
                Provider = Id,
                Identity = new ProviderIdentity { Email = "test@example.com" }
            };
        }

        public Task<string?> GetSystemActiveAccountIdAsync(IReadOnlyList<AccountConfig> accounts, WindowsCredentialVault vault)
        {
            return Task.FromResult<string?>(accounts.FirstOrDefault()?.Id);
        }
    }

    private class FailingTestingAdapter : PowerQuota.Core.Providers.IProviderAdapter
    {
        public ProviderId Id { get; }

        public FailingTestingAdapter(ProviderId id)
        {
            Id = id;
        }

        public Task<UsageSnapshot> FetchAsync(AccountConfig account, WindowsCredentialVault vault, HttpClient client, CancellationToken ct = default)
        {
            throw new HttpRequestException("Simulated network failure");
        }

        public Task<string?> GetSystemActiveAccountIdAsync(IReadOnlyList<AccountConfig> accounts, WindowsCredentialVault vault)
        {
            return Task.FromResult<string?>(accounts.FirstOrDefault()?.Id);
        }
    }
}

