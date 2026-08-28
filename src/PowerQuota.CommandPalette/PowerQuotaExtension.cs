using System.Runtime.InteropServices;
using Microsoft.CommandPalette.Extensions;
using PowerQuota.CommandPalette.Providers;

namespace PowerQuota.CommandPalette;

[ComVisible(true)]
[Guid("9f3c1a82-47d5-4a61-8f55-7d88c2419f10")]
public sealed partial class PowerQuotaExtension : IExtension
{
    private readonly PowerQuotaCommandProvider _commandProvider = new();
    private static readonly System.Threading.ManualResetEvent _disposedEvent = new(false);

    public static System.Threading.ManualResetEvent DisposedEvent => _disposedEvent;

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

