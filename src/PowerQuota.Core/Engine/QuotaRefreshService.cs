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
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _stateLock = new();
    private bool _disposed;

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
            _timer = new Timer(OnTimerTick, null, 1000, Timeout.Infinite);
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
                    int intervalMs = Math.Max(1, _configStorage.Current.RefreshIntervalMinutes) * 60 * 1000;
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

    public async Task RefreshProviderAsync(ProviderId provider, CancellationToken ct = default)
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
            var pState = State.Providers.FirstOrDefault(p => p.Provider == provider);
            if (pState != null)
            {
                pState.SystemActiveAccountId = systemActiveId;
                pState.ActiveAccountId = systemActiveId ?? accounts.FirstOrDefault()?.Id;
            }
            State.UpdatedAt = DateTimeOffset.UtcNow;
        }

        NotifyStateChanged();
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

        try
        {
            _httpClient.Dispose();
        }
        catch { }
    }
}

