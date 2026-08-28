using System.Net.Http;
using PowerQuota.Core.Models;
using PowerQuota.Core.Providers;
using PowerQuota.Core.Storage;

namespace PowerQuota.Core.Engine;

public class QuotaRefreshService : IDisposable
{
    private readonly ConfigStorage _configStorage;
    private readonly WindowsCredentialVault _vault;
    private readonly HttpClient _httpClient;
    private readonly Dictionary<ProviderId, IProviderAdapter> _adapters = new();
    private readonly Timer? _timer;
    private readonly object _stateLock = new();

    public AppState State { get; private set; } = new();
    public event EventHandler<AppState>? StateChanged;

    public void NotifyStateChanged()
    {
        lock (_stateLock)
        {
            StateChanged?.Invoke(this, State);
        }
    }

    public QuotaRefreshService(ConfigStorage configStorage, WindowsCredentialVault vault, HttpClient? httpClient = null, bool autoStartTimer = true)
    {
        _configStorage = configStorage;
        _vault = vault;
        _httpClient = httpClient ?? new HttpClient();

        // Register all provider adapters
        RegisterAdapter(new CodexProvider());
        RegisterAdapter(new ClaudeProvider());
        RegisterAdapter(new CursorProvider());
        RegisterAdapter(new GeminiProvider());
        RegisterAdapter(new CopilotProvider());
        RegisterAdapter(new MinimaxProvider());
        RegisterAdapter(new KimiProvider());

        InitializeState();

        if (autoStartTimer)
        {
            int intervalMs = Math.Max(1, _configStorage.Current.RefreshIntervalMinutes) * 60 * 1000;
            _timer = new Timer(async _ => await RefreshAllAsync(), null, 1000, intervalMs);
        }
    }

    private void RegisterAdapter(IProviderAdapter adapter)
    {
        _adapters[adapter.Id] = adapter;
    }

    private void InitializeState()
    {
        lock (_stateLock)
        {
            var config = _configStorage.Current;
            State = new AppState
            {
                UpdatedAt = DateTimeOffset.UtcNow,
                Providers = ProviderIdExtensions.All.Select(pid => new ProviderRuntimeState
                {
                    Provider = pid,
                    Enabled = config.EnabledProviders.Contains(pid)
                }).ToList()
            };
        }
    }

    public void RemoveAccount(string accountId)
    {
        lock (_stateLock)
        {
            State.ProviderAccounts.RemoveAll(a => a.AccountId == accountId);

            var remainingConfigured = _configStorage.Current.Accounts.Where(a => a.Id != accountId).ToList();
            foreach (var pState in State.Providers)
            {
                var providerAccounts = remainingConfigured.Where(a => a.Provider == pState.Provider).ToList();
                if (pState.SystemActiveAccountId == accountId)
                {
                    pState.SystemActiveAccountId = null;
                }
                if (pState.ActiveAccountId == accountId)
                {
                    pState.ActiveAccountId = pState.SystemActiveAccountId ?? providerAccounts.FirstOrDefault()?.Id;
                }
            }
            State.UpdatedAt = DateTimeOffset.UtcNow;
        }

        StateChanged?.Invoke(this, State);
    }

    public void ReconcileAccounts()
    {
        lock (_stateLock)
        {
            var configuredAccounts = _configStorage.Current.Accounts;
            var configuredAccountIds = configuredAccounts.Select(a => a.Id).ToHashSet();
            State.ProviderAccounts.RemoveAll(a => !configuredAccountIds.Contains(a.AccountId));

            foreach (var pState in State.Providers)
            {
                var providerAccounts = configuredAccounts.Where(a => a.Provider == pState.Provider).ToList();
                if (pState.SystemActiveAccountId != null && !providerAccounts.Any(a => a.Id == pState.SystemActiveAccountId))
                {
                    pState.SystemActiveAccountId = null;
                }
                if (pState.ActiveAccountId != null && !providerAccounts.Any(a => a.Id == pState.ActiveAccountId))
                {
                    pState.ActiveAccountId = pState.SystemActiveAccountId ?? providerAccounts.FirstOrDefault()?.Id;
                }
            }
            State.UpdatedAt = DateTimeOffset.UtcNow;
        }

        StateChanged?.Invoke(this, State);
    }

    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        ReconcileAccounts();

        var config = _configStorage.Current;
        var tasks = new List<Task>();

        foreach (var pid in ProviderIdExtensions.All)
        {
            if (config.EnabledProviders.Contains(pid))
            {
                tasks.Add(RefreshProviderInternalAsync(pid, ct));
            }
        }

        await Task.WhenAll(tasks);
        StateChanged?.Invoke(this, State);
    }

    public async Task RefreshProviderAsync(ProviderId provider, CancellationToken ct = default)
    {
        await RefreshProviderInternalAsync(provider, ct);
        StateChanged?.Invoke(this, State);
    }

    private async Task RefreshProviderInternalAsync(ProviderId provider, CancellationToken ct = default)
    {
        if (!_adapters.TryGetValue(provider, out var adapter)) return;

        var config = _configStorage.Current;
        var accounts = config.Accounts.Where(a => a.Provider == provider).ToList();

        // If no configured accounts, check if host scanner can create one dynamically
        if (accounts.Count == 0)
        {
            var autoAccount = TryAutoDiscoverAccount(provider);
            if (autoAccount != null)
            {
                _configStorage.Mutate(cfg =>
                {
                    if (!cfg.Accounts.Any(a => a.Id == autoAccount.Id || (a.Provider == autoAccount.Provider && a.Label == autoAccount.Label)))
                    {
                        cfg.Accounts.Add(autoAccount);
                    }
                });
                accounts.Add(autoAccount);
            }
        }

        // Clean up orphaned runtime states for this provider that are no longer configured
        lock (_stateLock)
        {
            var currentAccountIds = accounts.Select(a => a.Id).ToHashSet();
            State.ProviderAccounts.RemoveAll(a => a.Provider == provider && !currentAccountIds.Contains(a.AccountId));
        }

        string? systemActiveId = null;
        try
        {
            systemActiveId = await adapter.GetSystemActiveAccountIdAsync(accounts, _vault);
        }
        catch { }

        foreach (var account in accounts)
        {
            // If the account was removed while refresh was running, skip it
            if (!_configStorage.Current.Accounts.Any(a => a.Id == account.Id))
            {
                continue;
            }

            ProviderAccountRuntimeState accountState;
            lock (_stateLock)
            {
                accountState = State.ProviderAccounts.FirstOrDefault(a => a.AccountId == account.Id) ?? new ProviderAccountRuntimeState
                {
                    Provider = provider,
                    AccountId = account.Id,
                    Label = string.IsNullOrEmpty(account.Label) ? (account.Email ?? provider.GetLabel()) : account.Label
                };

                if (!State.ProviderAccounts.Contains(accountState))
                {
                    State.ProviderAccounts.Add(accountState);
                }
            }

            if (accountState.IsBackingOff)
            {
                continue;
            }

            try
            {
                var snapshot = await adapter.FetchAsync(account, _vault, _httpClient, ct);
                lock (_stateLock)
                {
                    accountState.Snapshot = snapshot;
                    accountState.Health = ProviderHealth.Ok;
                    accountState.AuthState = AuthState.Ready;
                    accountState.Error = null;
                    accountState.LastSuccessAt = DateTimeOffset.UtcNow;
                    accountState.ConsecutiveFailures = 0;
                    accountState.RetryAfter = null;
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                lock (_stateLock)
                {
                    accountState.Health = ProviderHealth.Error;
                    accountState.AuthState = AuthState.ActionRequired;
                    accountState.Error = ex.Message;
                    accountState.ConsecutiveFailures++;
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                lock (_stateLock)
                {
                    accountState.Health = ProviderHealth.Error;
                    accountState.ConsecutiveFailures++;
                    // Backoff 2^failures * 30 seconds
                    var backoffSec = Math.Min(3600, (int)Math.Pow(2, accountState.ConsecutiveFailures) * 30);
                    accountState.RetryAfter = DateTimeOffset.UtcNow.AddSeconds(backoffSec);
                    accountState.Error = $"Rate limited, retrying in {backoffSec}s";
                }
            }
            catch (Exception ex)
            {
                lock (_stateLock)
                {
                    accountState.Health = ProviderHealth.Error;
                    accountState.Error = ex.Message;
                    accountState.ConsecutiveFailures++;
                }
            }
        }

        lock (_stateLock)
        {
            var currentConfigured = _configStorage.Current.Accounts.Where(a => a.Provider == provider).ToList();
            var currentConfiguredIds = currentConfigured.Select(a => a.Id).ToHashSet();
            State.ProviderAccounts.RemoveAll(a => a.Provider == provider && !currentConfiguredIds.Contains(a.AccountId));

            var pState = State.Providers.FirstOrDefault(p => p.Provider == provider);
            if (pState != null)
            {
                pState.SystemActiveAccountId = systemActiveId != null && currentConfiguredIds.Contains(systemActiveId) ? systemActiveId : null;
                pState.ActiveAccountId = pState.SystemActiveAccountId ?? currentConfigured.FirstOrDefault()?.Id;
            }
            State.UpdatedAt = DateTimeOffset.UtcNow;
        }

        StateChanged?.Invoke(this, State);
    }

    private AccountConfig? TryAutoDiscoverAccount(ProviderId provider)
    {
        switch (provider)
        {
            case ProviderId.Codex:
                if (!string.IsNullOrEmpty(HostCliScanner.GetCodexActiveToken()))
                    return new AccountConfig { Provider = ProviderId.Codex, Label = "Codex (CLI)" };
                break;
            case ProviderId.Claude:
                if (!string.IsNullOrEmpty(HostCliScanner.GetClaudeActiveToken()))
                    return new AccountConfig { Provider = ProviderId.Claude, Label = "Claude Code (CLI)" };
                break;
            case ProviderId.Cursor:
                var (at, _) = HostCliScanner.ScanCursorIdeTokens();
                if (!string.IsNullOrEmpty(at))
                    return new AccountConfig { Provider = ProviderId.Cursor, Label = "Cursor (IDE)" };
                break;
            case ProviderId.Gemini:
                if (!string.IsNullOrEmpty(HostCliScanner.GetGeminiActiveToken()))
                    return new AccountConfig { Provider = ProviderId.Gemini, Label = "Gemini (CLI)" };
                break;
            case ProviderId.Copilot:
                if (!string.IsNullOrEmpty(HostCliScanner.GetCopilotActiveToken()))
                    return new AccountConfig { Provider = ProviderId.Copilot, Label = "Copilot (CLI)" };
                break;
            case ProviderId.Kimi:
                if (!string.IsNullOrEmpty(HostCliScanner.GetOpenCodeKimiApiKey()))
                    return new AccountConfig { Provider = ProviderId.Kimi, Label = "Kimi (OpenCode)" };
                break;
        }
        return null;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _httpClient.Dispose();
    }
}

