# Microsoft Store & Packaging Environment Variables

This rule documents the environment variables, identity parameters, and procedures for packaging and publishing PowerQuota to the Microsoft Store via Partner Center.

## Store Identity Environment Variables

These variables are defined in the Windows **User** environment registry (`[System.Environment]::GetEnvironmentVariable($name, "User")`). The build script [`build-msix.ps1`](file:///c:/Users/ericj/source/PowerQuota/build-msix.ps1) automatically checks `Process`, `User`, and `Machine` environment scopes via `Get-ConfigEnvVar`.

| Variable | Scope | Example Value | Description |
|---|---|---|---|
| `PublisherDisplayName` | User | `Your Name` | Friendly publisher display name validated by Partner Center during package acceptance. |
| `PackageIdentityPublisher` | User | `00000000-0000-0000-0000-000000000000` | Publisher GUID used in `Package.Identity.Publisher` as `CN=<GUID>`. |
| `PackageIdentityName` | User | `12345YourPublisherId` | Package prefix used in `Package.Identity.Name` as `<Prefix>.PowerQuota`. |

> [!NOTE]
> Do not hardcode custom developer credentials into `Package.appxmanifest` directly; the build script dynamically patches `AppxManifest.xml` in the staging directory during the `-ForStore` packaging process.

---

## Store API & Submission Credentials

When automating submissions or querying certification status through Partner Center / StoreBroker:

| Variable | Description |
|---|---|
| `STORE_TENANT_ID` | Azure AD Directory (Tenant) ID linked to Partner Center. |
| `STORE_CLIENT_ID` | Azure AD App Registration Client ID. |
| `STORE_CLIENT_SECRET` | Azure AD Client Secret for Store API authentication. |
| `STORE_APP_ID` | Partner Center Store Product/App ID (found on the app's Partner Center identity page). |

---

## Release & Packaging Workflow

1. **Version Updates**:
   - Update `<Version>`, `<AssemblyVersion>`, and `<FileVersion>` in [`Directory.Build.props`](file:///c:/Users/ericj/source/PowerQuota/Directory.Build.props).
   - Update `Version` attribute in `<Identity>` inside [`Package.appxmanifest`](file:///c:/Users/ericj/source/PowerQuota/src/PowerQuota.CommandPalette/Package.appxmanifest).

2. **Build Store MSIX Bundle**:
   ```powershell
   pwsh -ExecutionPolicy Bypass -File .\build-msix.ps1 -ForStore -Bundle -Version <Version>
   ```
   Outputs:
   - `artifacts/PowerQuota_<Version>_Store.msixbundle`
   - `artifacts/PowerQuota_<Version>_x64_Store.msix`
   - `artifacts/PowerQuota_<Version>_arm64_Store.msix`

3. **Tagging & GitHub Release**:
   - Tag format: `v*` (e.g., `v1.1.0` or `v.1.1`).
   - Pushing the tag automatically triggers `.github/workflows/build-and-release.yml`.

---

## Store Install Stuck on "Retry"

A genuine version bump (e.g. 1.6.0 → 1.7.0) is handled automatically: the extension
watches for it via `PackageCatalog.PackageUpdating` in
[`PowerQuotaExtension.StartUpdateWatcher()`](file:///c:/Users/ericj/source/PowerQuota/src/PowerQuota.CommandPalette/PowerQuotaExtension.cs)
and exits itself once Windows finishes the swap, so PowerToys can relaunch it against the
new files — no manual steps needed.

`PackageUpdating` only fires for an actual version transition, though. If the Store shows
"Retry" (install/re-register failing) for what should be a fresh install of the **same**
version — e.g. a previously stalled or interrupted install being retried — Windows can't
hot-swap a running binary against itself and there's no event to hook. Check
`Microsoft-Windows-AppXDeploymentServer/Operational` for `error 0x80073D02: Unable to
install because the following apps need to be closed <PackageFullName>` to confirm this is
what's happening:

```powershell
Get-WinEvent -LogName "Microsoft-Windows-AppXDeploymentServer/Operational" -MaxEvents 5
```

Fix: close the locked processes, then hit Retry in the Store.

```powershell
Stop-Process -Name "PowerQuota.CommandPalette" -Force -ErrorAction SilentlyContinue
Stop-Process -Name "Microsoft.CmdPal.UI" -Force -ErrorAction SilentlyContinue
```
