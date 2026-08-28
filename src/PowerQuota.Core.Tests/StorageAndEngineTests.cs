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
    public void QuotaRefreshService_UpdateRefreshInterval_UpdatesIntervalWithoutRecreatingService()
    {
        _storage.Mutate(cfg => cfg.RefreshIntervalMinutes = 5);

        using var service = new QuotaRefreshService(_storage, _vault, autoStartTimer: true);

        Assert.Equal(5, service.RefreshIntervalMinutes);

        service.UpdateRefreshInterval(15);
        Assert.Equal(15, service.RefreshIntervalMinutes);

        service.UpdateRefreshInterval(1);
        Assert.Equal(1, service.RefreshIntervalMinutes);

        // Clamping check: <= 0 should clamp to 1 minute
        service.UpdateRefreshInterval(0);
        Assert.Equal(1, service.RefreshIntervalMinutes);

        service.UpdateRefreshInterval(-5);
        Assert.Equal(1, service.RefreshIntervalMinutes);
    }

    [Fact]
    public void QuotaRefreshService_UpdateRefreshInterval_SafeWhenTimerDisabledOrDisposed()
    {
        _storage.Mutate(cfg => cfg.RefreshIntervalMinutes = 5);

        var service = new QuotaRefreshService(_storage, _vault, autoStartTimer: false);
        service.UpdateRefreshInterval(30);
        Assert.Equal(30, service.RefreshIntervalMinutes);

        service.Dispose();
        // Calling update on disposed service does not throw ObjectDisposedException
        service.UpdateRefreshInterval(60);
        Assert.Equal(60, service.RefreshIntervalMinutes);
    }

    [Fact]
    public void SettingsFormPage_IntervalSelection_UpdatesRefreshServiceAndConfig()
    {
        _storage.Mutate(cfg => cfg.RefreshIntervalMinutes = 5);
        using var service = new QuotaRefreshService(_storage, _vault, autoStartTimer: false);
        var settingsPage = new PowerQuota.CommandPalette.Pages.SettingsFormPage(_storage, service);

        var items = settingsPage.GetItems();
        var intervalItem = items.FirstOrDefault(i => i.Title == "Auto-Refresh Interval");
        Assert.NotNull(intervalItem);

        var choicePage = intervalItem!.Command as PowerQuota.CommandPalette.Pages.SettingChoicePage;
        Assert.NotNull(choicePage);

        var choices = choicePage!.GetItems();
        var thirtyMinChoice = choices.FirstOrDefault(c => c.Title == "Every 30 Minutes");
        Assert.NotNull(thirtyMinChoice);

        var command = thirtyMinChoice!.Command as Microsoft.CommandPalette.Extensions.Toolkit.AnonymousCommand;
        Assert.NotNull(command);
        command!.Invoke();

        Assert.Equal(30, service.RefreshIntervalMinutes);
        Assert.Equal(30, _storage.Current.RefreshIntervalMinutes);
    }

    [Fact]
    public async Task QuotaRefreshService_PreventsOverlappingRefreshes_SerializesExecution()
    {
        _storage.Mutate(cfg =>
        {
            cfg.Accounts.Clear();
            cfg.Accounts.Add(new AccountConfig
            {
                Id = "acc-claude-concurrency",
                Provider = ProviderId.Claude,
                Label = "Claude Concurrency Test"
            });
        });

        var slowAdapter = new ConcurrencyTestingAdapter(ProviderId.Claude, delayMs: 80);
        using var service = new QuotaRefreshService(_storage, _vault, autoStartTimer: false);
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
        _storage.Mutate(cfg =>
        {
            cfg.Accounts.Clear();
            cfg.Accounts.Add(new AccountConfig
            {
                Id = "acc-claude-cancel",
                Provider = ProviderId.Claude,
                Label = "Claude Cancel Test"
            });
        });

        var slowAdapter = new ConcurrencyTestingAdapter(ProviderId.Claude, delayMs: 1000);
        var service = new QuotaRefreshService(_storage, _vault, autoStartTimer: false);
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
        _storage.Mutate(cfg =>
        {
            cfg.Accounts.Clear();
            cfg.Accounts.Add(new AccountConfig
            {
                Id = "acc-failing",
                Provider = ProviderId.Claude,
                Label = "Failing Adapter Test"
            });
        });

        var failingAdapter = new FailingTestingAdapter(ProviderId.Claude);
        using var service = new QuotaRefreshService(_storage, _vault, autoStartTimer: false);
        service.RegisterAdapter(failingAdapter);

        // Both calls should complete gracefully and not throw unhandled exception
        await service.RefreshProviderAsync(ProviderId.Claude);
        await service.RefreshAllAsync();

        var accState = service.State.ProviderAccounts.FirstOrDefault(a => a.AccountId == "acc-failing");
        Assert.NotNull(accState);
        Assert.Equal(ProviderHealth.Error, accState!.Health);
        Assert.Contains("Simulated network failure", accState.Error);
    }

    [Fact]
    public void QuotaRefreshService_RemoveAccount_RemovesStateImmediatelyAndFiresEvent()
    {
        var accountId = "acc-remove-test-1";

        _storage.Mutate(cfg =>
        {
            cfg.Accounts.Clear();
            cfg.Accounts.Add(new AccountConfig
            {
                Id = accountId,
                Provider = ProviderId.Claude,
                Label = "Claude Test"
            });
        });

        using var service = new QuotaRefreshService(_storage, _vault, autoStartTimer: false);

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

        _storage.Mutate(cfg => cfg.Accounts.RemoveAll(a => a.Id == accountId));
        service.RemoveAccount(accountId);

        Assert.True(eventFired);
        Assert.DoesNotContain(service.State.ProviderAccounts, a => a.AccountId == accountId);
        Assert.Null(service.State.Providers.First(p => p.Provider == ProviderId.Claude).SystemActiveAccountId);
        Assert.Null(service.State.Providers.First(p => p.Provider == ProviderId.Claude).ActiveAccountId);
    }

    [Fact]
    public void QuotaRefreshService_RemoveAccount_FallsBackToRemainingConfiguredAccount()
    {
        var accountId1 = "acc-remove-1";
        var accountId2 = "acc-remaining-2";

        _storage.Mutate(cfg =>
        {
            cfg.Accounts.Clear();
            cfg.Accounts.Add(new AccountConfig
            {
                Id = accountId1,
                Provider = ProviderId.Claude,
                Label = "Claude Account 1"
            });
            cfg.Accounts.Add(new AccountConfig
            {
                Id = accountId2,
                Provider = ProviderId.Claude,
                Label = "Claude Account 2"
            });
        });

        using var service = new QuotaRefreshService(_storage, _vault, autoStartTimer: false);

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

        _storage.Mutate(cfg => cfg.Accounts.RemoveAll(a => a.Id == accountId1));
        service.RemoveAccount(accountId1);

        Assert.DoesNotContain(service.State.ProviderAccounts, a => a.AccountId == accountId1);
        Assert.Contains(service.State.ProviderAccounts, a => a.AccountId == accountId2);
        Assert.Equal(accountId2, service.State.Providers.First(p => p.Provider == ProviderId.Claude).ActiveAccountId);
    }

    [Fact]
    public void QuotaRefreshService_ReconcileAccounts_PrunesOrphanedAccounts()
    {
        var validAccountId = "acc-valid-1";
        var orphanAccountId = "acc-orphan-2";

        _storage.Mutate(cfg =>
        {
            cfg.Accounts.Clear();
            cfg.Accounts.Add(new AccountConfig
            {
                Id = validAccountId,
                Provider = ProviderId.Claude,
                Label = "Claude Valid"
            });
        });

        using var service = new QuotaRefreshService(_storage, _vault, autoStartTimer: false);

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
        var validAccountId = "acc-valid-refresh";
        var orphanAccountId = "acc-orphan-refresh";

        _storage.Mutate(cfg =>
        {
            cfg.Accounts.Clear();
            cfg.Accounts.Add(new AccountConfig
            {
                Id = validAccountId,
                Provider = ProviderId.Claude,
                Label = "Claude Valid"
            });
        });

        using var service = new QuotaRefreshService(_storage, _vault, autoStartTimer: false);

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
        var accountId = "acc-page-remove-test";

        _storage.Mutate(cfg =>
        {
            cfg.Accounts.Clear();
            cfg.Accounts.Add(new AccountConfig
            {
                Id = accountId,
                Provider = ProviderId.Minimax,
                Label = "Minimax Test"
            });
        });
        _vault.SaveApiKey(accountId, "sk-test-key");

        using var service = new QuotaRefreshService(_storage, _vault, autoStartTimer: false);

        service.State.ProviderAccounts.Add(new ProviderAccountRuntimeState
        {
            Provider = ProviderId.Minimax,
            AccountId = accountId,
            Label = "Minimax Test",
            Error = "Key required"
        });

        var page = new PowerQuota.CommandPalette.Pages.ProviderDetailsPage(ProviderId.Minimax, service, _storage, _vault);
        var items = page.GetItems();
        Assert.Single(items);

        var removeContextItem = items[0].MoreCommands?.OfType<Microsoft.CommandPalette.Extensions.Toolkit.CommandContextItem>()
            .FirstOrDefault(c => c.Title == "Remove Account");
        Assert.NotNull(removeContextItem);

        var invokable = removeContextItem!.Command as Microsoft.CommandPalette.Extensions.IInvokableCommand;
        Assert.NotNull(invokable);
        invokable!.Invoke(null!);

        Assert.DoesNotContain(_storage.Current.Accounts, a => a.Id == accountId);
        Assert.Null(_vault.GetApiKey(accountId));
        Assert.DoesNotContain(service.State.ProviderAccounts, a => a.AccountId == accountId);

        var updatedItems = page.GetItems();
        Assert.Single(updatedItems);
        Assert.Equal("No accounts configured", updatedItems[0].Title);
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
