# WlanGO development

## Components

- `src/PadP2P.App`: C# / .NET 10 WPF UI, lightweight MVVM, tray icon, preferences, startup registration, configurable app launch items, and SuperDisplay supervision.
- `src/PadP2P.ControlBackend`: small native C++ process with a per-user named-pipe control interface.
- `../third_party/Windows-classic-samples/Samples/WiFiDirectLegacyAP/cpp/WlanHostedNetworkWinRT.cpp`: Microsoft's original Wi-Fi Direct helper, compiled directly into the backend without rewriting its GO lifecycle.
- `tools/Generate-AppIcons.ps1`: creates the app and tray ICO files from the project's SVG asset.

## Requirements

- Windows 11 x64.
- .NET SDK 10.0.100 or later in the 10.0 feature band. `global.json` permits newer .NET 10 feature bands.
- Visual Studio Build Tools with the C++ workload, MSVC v142 or v143, and Windows SDK 10.0.22621 or newer.
- Inno Setup 6 to build the installer.

The app is published self-contained for `win-x64`; end users do not need to install .NET separately.

## Build

From this folder in PowerShell:

```powershell
.\Build-Desktop.ps1
.\Build-Installer.ps1
```

The backend defaults to MSVC v142. For Visual Studio 2022 / v143:

```powershell
.\Build-Desktop.ps1 -PlatformToolset v143
.\Build-Installer.ps1
```

`Build-Installer.ps1` can package an alternate application output without replacing the standard Release package:

```powershell
.\Build-Installer.ps1 -AppDirectory artifacts\Review\app -OutputDirectory artifacts\Review\installer
```

The application output is under `artifacts\Release\WlanGO-Package\app`; the installer and SHA-256 file are written under `artifacts\Release\WlanGO-Installer`. Build logs stay in the ignored `artifacts\logs` directory. A local network profile is not needed to build.

The package output includes the applicable WPF-UI, H.NotifyIcon, .NET runtime, and Microsoft Wi-Fi Direct sample notices.

GitHub Actions builds the x64 application and Inno Setup installer on Windows for pull requests, pushes to `main`, and manual runs. Workflow actions are pinned to upstream commit SHAs; Dependabot checks the pinned actions and NuGet packages weekly. To publish a release, set the version in `PadP2P.App.csproj`, update `CHANGELOG.md`, and push a matching tag such as `v0.2.0`. The tag workflow checks that the tag matches the app version, then publishes the installer and `SHA256SUMS.txt` as a GitHub Release. Wi-Fi Direct connection checks require physical Wi-Fi Direct-capable hardware and are not run on hosted runners.

## User data and privacy

- Settings: `%LOCALAPPDATA%\WlanGO\settings.json`.
- Wi-Fi passphrase: protected with Windows DPAPI for the current user; never included in logs or process arguments.
- Logs: `%LOCALAPPDATA%\WlanGO\logs`, capped at one 64 KiB file and two rotating copies.
- SuperDisplay executable path: stored as a per-user preference. The third-party program itself is not bundled.
- Custom app launch names and executable paths: stored as per-user preferences; they are not written to logs or shipped in the installer.
- No telemetry or background updater is included. The Settings page can query the public GitHub latest-release endpoint when the user asks, and provides project and release links. That request carries only the app version in its User-Agent; Wi-Fi credentials, client details, and user settings are not sent. Updates are installed manually from the Inno Setup release package.

First launch generates a random password and uses a generic SSID. Existing DPAPI-protected settings from the previous Pad P2P app are imported for the same Windows user. WlanGO does not scan project folders for local network configuration files.
