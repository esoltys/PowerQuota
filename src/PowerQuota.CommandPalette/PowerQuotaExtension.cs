using System.Runtime.InteropServices;
using Microsoft.CommandPalette.Extensions;
using PowerQuota.CommandPalette.Providers;
using Windows.ApplicationModel;

namespace PowerQuota.CommandPalette;

[ComVisible(true)]
[Guid("9f3c1a82-47d5-4a61-8f55-7d88c2419f10")]
public sealed partial class PowerQuotaExtension : IExtension
{
    private readonly PowerQuotaCommandProvider _commandProvider = new();
    private static readonly System.Threading.ManualResetEvent _disposedEvent = new(false);
    private static PackageCatalog? _packageCatalog;

    public static System.Threading.ManualResetEvent DisposedEvent => _disposedEvent;

    // MSIX updates replace the on-disk package while this COM server process keeps running
    // the old assembly, so the app looks "updated" (new version registered) without the
    // running extension actually changing. PackageCatalog notifies us the moment the swap
    // completes, so we can exit and let PowerToys relaunch us against the new files.
    public static void StartUpdateWatcher()
    {
        try
        {
            _packageCatalog = PackageCatalog.OpenForCurrentPackage();
            _packageCatalog.PackageUpdating += OnPackageUpdating;
            TryLog("Package update watcher started.");
        }
        catch (Exception ex)
        {
            TryLog($"Failed to start package update watcher: {ex}");
        }
    }

    private static void OnPackageUpdating(PackageCatalog sender, PackageUpdatingEventArgs args)
    {
        if (!args.IsComplete)
        {
            return;
        }

        var v = args.TargetPackage.Id.Version;
        TryLog($"Detected in-place package update to {v.Major}.{v.Minor}.{v.Build}.{v.Revision}; restarting extension host so the new version takes effect.");
        _disposedEvent.Set();
    }

    public object? GetProvider(ProviderType providerType)
    {
        TryLog($"GetProvider called for: {providerType}");
        return providerType switch
        {
            ProviderType.Commands => _commandProvider,
            _ => null
        };
    }

    public void Dispose()
    {
        TryLog("PowerQuotaExtension.Dispose called");
        _commandProvider.Dispose();
        _disposedEvent.Set();
    }

    private static void TryLog(string message)
    {
        try
        {
            var dir = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "PowerQuota");
            System.IO.Directory.CreateDirectory(dir);
            var logPath = System.IO.Path.Combine(dir, "extension.log");
            System.IO.File.AppendAllText(logPath, $"[{System.DateTime.UtcNow:O}] {message}\n");
        }
        catch
        {
            // Diagnostic logging failures should never prevent extension functionality.
        }
    }
}

