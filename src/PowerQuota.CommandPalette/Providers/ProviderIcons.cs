using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerQuota.Core.Models;

namespace PowerQuota.CommandPalette.Providers;

public static class ProviderIcons
{
    private static readonly string IconsDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons");

    public static IconInfo GetIcon(ProviderId? provider = null)
    {
        if (provider == null)
        {
            return new IconInfo("\uE945");
        }

        string baseName = provider.Value switch
        {
            ProviderId.Claude => "claude",
            ProviderId.Codex => "codex",
            ProviderId.Cursor => "cursor",
            ProviderId.Gemini => "gemini",
            ProviderId.Copilot => "copilot",
            ProviderId.Minimax => "minimax",
            ProviderId.Kimi => "kimi",
            _ => "powerquota"
        };

        var svgPath = Path.Combine(IconsDir, $"{baseName}.svg");
        if (File.Exists(svgPath))
        {
            return new IconInfo(svgPath);
        }

        var pngPath = Path.Combine(IconsDir, $"{baseName}.png");
        if (File.Exists(pngPath))
        {
            return new IconInfo(pngPath);
        }

        return new IconInfo("\uE945");
    }
}
