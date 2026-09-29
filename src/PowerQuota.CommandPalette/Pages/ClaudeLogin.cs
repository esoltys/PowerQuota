using PowerQuota.Core.Engine;

namespace PowerQuota.CommandPalette.Pages;

internal static class ClaudeLogin
{
    /// <summary>Starts PowerQuota's own browser login for a Claude account without blocking the palette.</summary>
    public static void Start(QuotaRefreshService refreshService, string accountId)
    {
        _ = refreshService.LoginClaudeAsync(accountId, OpenBrowser);
    }

    private static void OpenBrowser(Uri url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch { }
    }
}
