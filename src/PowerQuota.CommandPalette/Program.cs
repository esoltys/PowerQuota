using System.Runtime.InteropServices;
using Microsoft.CommandPalette.Extensions;

namespace PowerQuota.CommandPalette;

public static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    public static async Task Main(string[] args)
    {
        var logPath = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "PowerQuota", "extension.log");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(logPath)!);
        System.IO.File.AppendAllText(logPath, $"[{System.DateTime.UtcNow:O}] Main started with args: {string.Join(" ", args)}\n");

        if (args.Contains("-RegisterProcessAsComServer", StringComparer.OrdinalIgnoreCase))
        {
            System.IO.File.AppendAllText(logPath, $"[{System.DateTime.UtcNow:O}] Registering ExtensionServer...\n");
            try
            {
                using var server = new ExtensionServer();
                server.RegisterExtension<PowerQuotaExtension>(() =>
                {
                    System.IO.File.AppendAllText(logPath, $"[{System.DateTime.UtcNow:O}] Creating PowerQuotaExtension instance\n");
                    return new PowerQuotaExtension();
                });
                PowerQuotaExtension.StartUpdateWatcher();
                System.IO.File.AppendAllText(logPath, $"[{System.DateTime.UtcNow:O}] Extension registered. Waiting for host requests...\n");
                PowerQuotaExtension.DisposedEvent.WaitOne();
                System.IO.File.AppendAllText(logPath, $"[{System.DateTime.UtcNow:O}] DisposedEvent signaled, exiting cleanly\n");
            }
            catch (Exception ex)
            {
                System.IO.File.AppendAllText(logPath, $"[{System.DateTime.UtcNow:O}] ERROR: {ex}\n");
                throw;
            }
        }
        else
        {
            AttachConsole(-1);
            Console.WriteLine("\n=== PowerQuota for Windows ===");
            Console.WriteLine("PowerToys Command Palette & Dock Extension");
            Console.WriteLine("Testing AI Provider discovery and quota fetch...\n");

            var configStorage = new Core.Storage.ConfigStorage();
            var vault = new Core.Storage.WindowsCredentialVault();
            using var refreshService = new Core.Engine.QuotaRefreshService(configStorage, vault);

            await refreshService.RefreshAllAsync();

            Console.WriteLine($"Found {refreshService.State.ProviderAccounts.Count} account(s):");
            foreach (var acc in refreshService.State.ProviderAccounts)
            {
                Console.WriteLine($" • [{acc.Provider}] {acc.Label}: {acc.GetStatusLine()}");
            }
        }
    }
}

