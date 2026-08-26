using System.Runtime.InteropServices;
using Microsoft.CommandPalette.Extensions;
using PowerQuota.CommandPalette.Providers;

namespace PowerQuota.CommandPalette;

[ComVisible(true)]
[Guid("9f3c1a82-47d5-4a61-8f55-7d88c2419f10")]
public sealed partial class PowerQuotaExtension : IExtension
{
    private readonly PowerQuotaCommandProvider _commandProvider = new();

    public object? GetProvider(ProviderType providerType)
    {
        return providerType switch
        {
            ProviderType.Commands => _commandProvider,
            _ => null
        };
    }

    public void Dispose()
    {
        _commandProvider.Dispose();
    }
}

