using Microsoft.CommandPalette.Extensions;
using Shmuelie.WinRTServer;

namespace PowerQuota.CommandPalette;

public static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Contains("-RegisterProcessAsComServer", StringComparer.OrdinalIgnoreCase))
        {
            var tcs = new TaskCompletionSource();
            await using var server = new ComServer();
            server.RegisterClass<PowerQuotaExtension, IExtension>(null!);
            server.Empty += (_, _) => tcs.TrySetResult();
            server.Start();
            await tcs.Task;
        }
        else
        {
            Console.WriteLine("=== PowerQuota for Windows ===");
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

