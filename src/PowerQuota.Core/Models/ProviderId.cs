namespace PowerQuota.Core.Models;

public enum ProviderId
{
    Codex,
    Claude,
    Cursor,
    Gemini,
    Copilot,
    Minimax,
    Kimi
}

public static class ProviderIdExtensions
{
    public static string GetLabel(this ProviderId provider) => provider switch
    {
        ProviderId.Codex => "Codex",
        ProviderId.Claude => "Claude",
        ProviderId.Cursor => "Cursor",
        ProviderId.Gemini => "Gemini",
        ProviderId.Copilot => "Copilot",
        ProviderId.Minimax => "Minimax",
        ProviderId.Kimi => "Kimi",
        _ => provider.ToString()
    };

    public static readonly ProviderId[] All = Enum.GetValues<ProviderId>();
}

