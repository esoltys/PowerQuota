# PowerQuota (for PowerToys Command Palette & Dock)

A Windows-native extension for **PowerToys Command Palette** and the **PowerToys Dock** that tracks AI coding quotas for **Claude Code**, **Codex / ChatGPT**, **Cursor**, **Gemini Code Assist**, **GitHub Copilot**, **Minimax**, and **Kimi for Coding**.

Converted from the open-source COSMIC panel applet [TopiCsarno/yapcap](https://github.com/TopiCsarno/yapcap) into a native C# / .NET 9 experience for Windows power users. I greatly enjoy using it on my CachyOS with COSMIC laptop and missed it on my Windows 11 desktop.

---


## Highlights

- **7 Supported AI Providers**
  - **Claude Code**: 5h session windows, weekly model quotas (e.g. Claude 3.7 Sonnet, Opus), extra spend limits, and host session scanning.
  - **Codex / ChatGPT**: 5h session limits, weekly windows, OpenAI credit balance, and `~/.codex/auth.json` host discovery.
  - **Cursor**: Safe read-only SQLite state scanning (`state.vscdb`), Fast/Composer and monthly request limits.
  - **Gemini (Google Code Assist)**: Multi-tier quota classification (Flash, Lite, Pro) with project resolution. Not the same as Antigravity quota, unfortunately.
  - **GitHub Copilot**: Free chat/completions or paid premium interactions quota.
  - **Minimax**: 5h interval limits and weekly token plan quotas.
  - **Kimi for Coding**: Weekly usage and rate-limit windows + OpenCode auth prefill (`~/.opencode/auth.json`).
- **PowerToys Dock Integration**
  - Live persistent dock bands (`GetDockBands()`) pinned directly to the PowerToys Dock.
  - Real-time quota percentage badges (e.g., `Claude 42%`, `Codex 35%`), status indicators, and reset countdown tooltips.
- **Command Palette Experience**
  - **Overview List**: Dashboard of all configured AI providers and quotas accessible with `Alt + Space` / `Win + Shift + C`.
  - **Provider Details**: Drill down into session, weekly, extra spend, and credit balances.
  - **Settings Form**: Toggle between used % vs remaining %, relative vs absolute reset times, and refresh intervals.
- **Zero Telemetry & Windows DPAPI Security**
  - All API requests are sent directly from your machine to provider endpoints. No intermediate servers or telemetry.
  - Credentials and tokens are encrypted locally using Windows Data Protection API (DPAPI with `CurrentUser` scope).

---

## Project Structure

```
PowerQuota/
├── PowerQuota.sln
├── src/
│   ├── PowerQuota.Core/                # AI quota engine, provider adapters, DPAPI vault, CLI scanner
│   │   ├── Engine/                     # QuotaRefreshService with exponential rate-limit backoff
│   │   ├── Models/                     # UsageSnapshot, UsageWindow, AppState, ProviderId
│   │   ├── Providers/                  # 7 AI provider adapters (Claude, Codex, Cursor, Gemini, etc.)
│   │   └── Storage/                    # WindowsCredentialVault, ConfigStorage, HostCliScanner
│   │
│   ├── PowerQuota.Core.Tests/          # Automated xUnit unit tests for all providers and storage
│   │   ├── ProviderTests.cs
│   │   └── StorageAndEngineTests.cs
│   │
│   └── PowerQuota.CommandPalette/      # WinRT COM Server extension for PowerToys Command Palette & Dock
│       ├── Assets/                     # Store & tile logo PNG assets
│       ├── Pages/                      # OverviewListPage, ProviderDetailsPage, AddAccountFormPage, SettingsFormPage
│       ├── Providers/                  # PowerQuotaCommandProvider (implements ICommandProvider4 & GetDockBands)
│       ├── Package.appxmanifest        # AppExtension com.microsoft.commandpalette manifest
│       └── Program.cs                  # COM out-of-process server entrypoint
```

---

## Getting Started

### Prerequisites
- Windows 10 (Build 19041+) or Windows 11
- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [PowerToys](https://github.com/microsoft/PowerToys) (with Command Palette enabled)
- **Windows Developer Mode**: Must be enabled (**Settings** → **System** → **Advanced** → **Developer Mode**, or **System** → **For developers**) or run registration with Administrator privileges to install unpacked development packages.

### Building the Project

1. Open the repository directory in PowerShell:
   ```powershell
   cd PowerQuota
   ```

2. Restore and build:
   ```powershell
   dotnet build
   ```

3. Run automated tests:
   ```powershell
   dotnet test
   ```

### Publishing and Registering with PowerToys

1. Publish the Command Palette extension:
   ```powershell
   dotnet publish src/PowerQuota.CommandPalette/PowerQuota.CommandPalette.csproj -c Release -r win-x64 --self-contained false
   ```

2. Register the extension manifest with Windows:
   ```powershell
   Add-AppxPackage -Register "src\PowerQuota.CommandPalette\bin\Release\net9.0-windows10.0.22621.0\win-x64\publish\AppxManifest.xml"
   ```

3. Confirm registration:
   ```powershell
   Get-AppxPackage *PowerQuota*
   ```
   *Verify that `Status` reports `Ok`.*

4. Open **PowerToys Settings** -> **Command Palette**:
   - Enable **Command Palette** and **Enable Dock**.
   - Press `Alt + Space` (or your configured shortcut) and type `powerquota` to view your AI quotas.
   - Pinned dock bands will appear in your persistent PowerToys Dock bar with real-time percentages!

---

## Acknowledgments

This project is a native Windows & PowerToys port inspired by and based on the work of [TopiCsarno/yapcap](https://github.com/TopiCsarno/yapcap) (originally created for the COSMIC desktop environment). Special thanks to [TopiCsarno](https://github.com/TopiCsarno) for their research into AI provider quota endpoints and rate-limit models.

---

## License

This project is licensed under the [MIT License](LICENSE).

