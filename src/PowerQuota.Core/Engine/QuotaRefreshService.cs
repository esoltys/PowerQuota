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
    private readonly bool _ownsHttpClient;
    private readonly Dictionary<ProviderId, IProviderAdapter> _adapters = new();
    private readonly Timer? _timer;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _stateLock = new();
    private bool _disposed;

    public AppState State { get; private set; } = new();
    public event EventHandler<AppState>? StateChanged;
    public int RefreshIntervalMinutes { get; private set; }

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
        _ownsHttpClient = httpClient == null;
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

        RefreshIntervalMinutes = Math.Max(1, _configStorage.Current.RefreshIntervalMinutes);
        if (autoStartTimer)
        {
            _timer = new Timer(OnTimerTick, null, 1000, Timeout.Infinite);
        }
    }

    public void UpdateRefreshInterval(int minutes)
    {
        int clampedMinutes = Math.Max(1, minutes);
        lock (_stateLock)
        {
            RefreshIntervalMinutes = clampedMinutes;
            try
            {
                if (_timer != null && !_disposed && !_cts.IsCancellationRequested)
                {
                    int intervalMs = clampedMinutes * 60 * 1000;
                    _timer.Change(intervalMs, Timeout.Infinite);
                }
            }
            catch (ObjectDisposedException)
            {
                // Ignore if service is being disposed or already disposed
            }
        }
    }

    internal void RegisterAdapter(IProviderAdapter adapter)
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

        NotifyStateChanged();
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

        NotifyStateChanged();
    }

    private async void OnTimerTick(object? state)
    {
        if (_disposed || _cts.IsCancellationRequested) return;

        try
        {
            await RefreshAllAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal during shutdown
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PowerQuota Timer] Background refresh failed: {ex}");
        }
        finally
        {
            if (!_disposed && !_cts.IsCancellationRequested)
            {
                try
                {
                    int intervalMs = Math.Max(1, RefreshIntervalMinutes) * 60 * 1000;
                    _timer?.Change(intervalMs, Timeout.Infinite);
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }

    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);
        var token = linkedCts.Token;

        try
        {
            await _refreshLock.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!_cts.IsCancellationRequested && ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (token.IsCancellationRequested) return;

            ReconcileAccounts();

            var config = _configStorage.Current;
            var tasks = new List<Task>();

            foreach (var pid in ProviderIdExtensions.All)
            {
                if (config.EnabledProviders.Contains(pid))
                {
                    tasks.Add(RefreshProviderInternalAsync(pid, token));
                }
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            NotifyStateChanged();
        }
        catch (OperationCanceledException) when (!_cts.IsCancellationRequested && ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Normal during service cancellation or shutdown
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PowerQuota RefreshAll] Error: {ex}");
        }
        finally
        {
            try
            {
                _refreshLock.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public void ResetBackoff(ProviderId? provider = null)
    {
        lock (_stateLock)
        {
            foreach (var acc in State.ProviderAccounts.Where(a => provider == null || a.Provider == provider.Value))
            {
                acc.RetryAfter = null;
                acc.ConsecutiveFailures = 0;
            }
        }
    }

    public async Task RefreshProviderAsync(ProviderId provider, CancellationToken ct = default)
    {
        ResetBackoff(provider);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);
        var token = linkedCts.Token;

        try
        {
            await _refreshLock.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!_cts.IsCancellationRequested && ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (token.IsCancellationRequested) return;

            await RefreshProviderInternalAsync(provider, token).ConfigureAwait(false);
            NotifyStateChanged();
        }
        catch (OperationCanceledException) when (!_cts.IsCancellationRequested && ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Normal during service cancellation or shutdown
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PowerQuota RefreshProvider] Error: {ex}");
        }
        finally
        {
            try
            {
                _refreshLock.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private int _claudeLoginInFlight;

    public bool IsClaudeLoginInProgress => Volatile.Read(ref _claudeLoginInFlight) != 0;

    /// <summary>
    /// Signs <paramref name="accountId"/> in with PowerQuota's own Claude OAuth session: opens the browser via
    /// <paramref name="openBrowser"/>, stores the resulting tokens, then refreshes Claude quotas. Returns false
    /// (without starting a second browser flow) when another login is already waiting for the browser.
    /// </summary>
    public async Task<bool> LoginClaudeAsync(string accountId, Action<Uri> openBrowser, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _claudeLoginInFlight, 1, 0) != 0) return false;

        try
        {
            SetAccountStatus(accountId, AuthState.ActionRequired, "Waiting for Claude login in the browser…");

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);
            ClaudeLoginResult result;
            try
            {
                result = await ClaudeOAuth.LoginAsync(_httpClient, openBrowser, linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SetAccountStatus(accountId, AuthState.ActionRequired, ex.Message);
                return false;
            }

            _vault.SaveTokens(accountId, result.Tokens);
            string? label = null;
            _configStorage.Mutate(cfg =>
            {
                var account = cfg.Accounts.FirstOrDefault(a => a.Id == accountId);
                if (account == null) return;
                if (!string.IsNullOrEmpty(result.Email)) account.Email = result.Email;
                // Replace generated labels (including older versions' borrowed "Claude Code (CLI)" session);
                // keep any label the user chose.
                if (string.IsNullOrEmpty(account.Label)
                    || account.Label.Contains("(CLI)", StringComparison.Ordinal)
                    || account.Label == $"{ProviderId.Claude.GetLabel()} Quota")
                {
                    account.Label = result.Email ?? ProviderId.Claude.GetLabel();
                }
                account.UpdatedAt = DateTimeOffset.UtcNow;
                label = account.Label;
            });

            lock (_stateLock)
            {
                var accountState = State.ProviderAccounts.FirstOrDefault(a => a.AccountId == accountId);
                if (accountState != null && label != null) accountState.Label = label;
            }

            await RefreshProviderAsync(ProviderId.Claude, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            Volatile.Write(ref _claudeLoginInFlight, 0);
        }
    }

    private void SetAccountStatus(string accountId, AuthState authState, string message)
    {
        lock (_stateLock)
        {
            var accountState = State.ProviderAccounts.FirstOrDefault(a => a.AccountId == accountId);
            if (accountState == null) return;
            accountState.AuthState = authState;
            accountState.Error = message;
        }
        NotifyStateChanged();
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
            systemActiveId = await adapter.GetSystemActiveAccountIdAsync(accounts, _vault).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch { }

        foreach (var account in accounts)
        {
            if (ct.IsCancellationRequested) return;

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
                var snapshot = await adapter.FetchAsync(account, _vault, _httpClient, ct).ConfigureAwait(false);
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
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
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
                    accountState.Error = "Rate limited";
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
    }

    private AccountConfig? TryAutoDiscoverAccount(ProviderId provider)
    {
        try
        {
            switch (provider)
            {
                case ProviderId.Codex:
                    if (!string.IsNullOrEmpty(HostCliScanner.GetCodexActiveToken()))
                        return new AccountConfig { Provider = ProviderId.Codex, Label = "Codex (CLI)" };
                    break;
                case ProviderId.Cursor:
                    var (at, _) = HostCliScanner.ScanCursorIdeTokens();
                    if (!string.IsNullOrEmpty(at))
                        return new AccountConfig { Provider = ProviderId.Cursor, Label = "Cursor (IDE)" };
                    break;
                case ProviderId.Gemini:
                    var (gAt, gRt, _, gEmail) = HostCliScanner.ScanGeminiAntigravityCredentials();
                    if (!string.IsNullOrEmpty(gAt) || !string.IsNullOrEmpty(gRt))
                        return new AccountConfig { Provider = ProviderId.Gemini, Label = "Gemini", Email = gEmail };
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
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PowerQuota AutoDiscover] Error for {provider}: {ex}");
        }
        return null;
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        try
        {
            _cts.Cancel();
        }
        catch { }

        _timer?.Dispose();
        try
        {
            _refreshLock.Dispose();
        }
        catch { }

        try
        {
            _cts.Dispose();
        }
        catch { }

        if (_ownsHttpClient)
        {
            try
            {
                _httpClient.Dispose();
            }
            catch { }
        }
    }
}
