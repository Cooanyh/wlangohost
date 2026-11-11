param(
    [string]$AppOutputPath,
    [ValidateSet('v142', 'v143')]
    [string]$PlatformToolset = 'v142'
)

$ErrorActionPreference = 'Stop'

$desktopRoot = $PSScriptRoot
$sdkRoot = Join-Path $desktopRoot '.tools\dotnet'
$localDotnet = Join-Path $sdkRoot 'dotnet.exe'
$dotnetCommand = Get-Command dotnet.exe -ErrorAction SilentlyContinue
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } elseif ($dotnetCommand) { $dotnetCommand.Source } else { $null }
$backendProject = Join-Path $desktopRoot 'src\PadP2P.ControlBackend\PadP2P.ControlBackend.vcxproj'
$appProject = Join-Path $desktopRoot 'src\PadP2P.App\PadP2P.App.csproj'
$artifactRoot = Join-Path $desktopRoot 'artifacts\Release\WlanGO-Package'
$logRoot = Join-Path $desktopRoot 'artifacts\logs'
$defaultAppOutput = Join-Path $artifactRoot 'app'
if ([string]::IsNullOrWhiteSpace($AppOutputPath)) {
    $appOutput = $defaultAppOutput
}
elseif ([System.IO.Path]::IsPathRooted($AppOutputPath)) {
    $appOutput = [System.IO.Path]::GetFullPath($AppOutputPath)
}
else {
    $appOutput = [System.IO.Path]::GetFullPath((Join-Path $desktopRoot $AppOutputPath))
}
$releasePrefix = [System.IO.Path]::GetFullPath($artifactRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $appOutput.StartsWith($releasePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The app output must stay inside $artifactRoot."
}
$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'

if (-not $dotnet) { throw 'The .NET 10 SDK was not found. Install the SDK specified in global.json or add dotnet.exe to PATH.' }
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw 'Visual Studio Installer vswhere.exe was not found. The C++ backend requires the VS v142 toolchain.'
}

$vsPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
if (-not $vsPath) { throw 'No Visual Studio MSBuild installation was found.' }
$msbuild = Join-Path $vsPath 'MSBuild\Current\Bin\MSBuild.exe'
if (-not (Test-Path -LiteralPath $msbuild)) { throw "MSBuild was not found: $msbuild" }

New-Item -ItemType Directory -Path $artifactRoot, $logRoot -Force | Out-Null
$iconGenerator = Join-Path $desktopRoot 'tools\Generate-AppIcons.ps1'
& $iconGenerator
$oldDotnetRoot = $env:DOTNET_ROOT
$oldTelemetryOptOut = $env:DOTNET_CLI_TELEMETRY_OPTOUT
$oldNoLogo = $env:DOTNET_NOLOGO
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

Push-Location $desktopRoot
try {
    $backendLog = Join-Path $logRoot 'backend-build.log'
    & $msbuild $backendProject /nologo /m /p:Configuration=Release /p:Platform=x64 "/p:PlatformToolset=$PlatformToolset" /v:minimal "/flp:logfile=$backendLog;verbosity=normal"
    if ($LASTEXITCODE -ne 0) { throw "C++ backend build failed with exit code $LASTEXITCODE. See $backendLog" }

    $restoreLog = Join-Path $logRoot 'app-restore.log'
    & $dotnet restore $appProject --runtime win-x64 -p:SelfContained=true --verbosity minimal *> $restoreLog
    if ($LASTEXITCODE -ne 0) { throw "NuGet restore failed with exit code $LASTEXITCODE. See $restoreLog" }

    $publishLog = Join-Path $logRoot 'app-publish.log'
    & $dotnet publish $appProject --configuration Release --runtime win-x64 --self-contained true --output $appOutput --no-restore --verbosity minimal *> $publishLog
    if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $publishLog -Tail 80; throw "WPF publish failed with exit code $LASTEXITCODE. See $publishLog" }

    $thirdPartyLicenseRoot = Join-Path (Split-Path -Parent $desktopRoot) 'third_party\licenses'
    Copy-Item -LiteralPath (Join-Path $thirdPartyLicenseRoot 'WPF-UI-LICENSE.md') -Destination (Join-Path $appOutput 'WPF-UI-LICENSE.md')
    Copy-Item -LiteralPath (Join-Path $thirdPartyLicenseRoot 'WPF-UI-ThirdPartyNotices.txt') -Destination (Join-Path $appOutput 'WPF-UI-ThirdPartyNotices.txt')
    Copy-Item -LiteralPath (Join-Path $thirdPartyLicenseRoot 'H.NotifyIcon-LICENSE.md') -Destination (Join-Path $appOutput 'H.NotifyIcon-LICENSE.md')

    $dotnetRoot = Split-Path -Parent $dotnet
    foreach ($notice in @(
            @{ Source = 'LICENSE.txt'; Destination = 'Microsoft.NET-LICENSE.txt' },
            @{ Source = 'ThirdPartyNotices.txt'; Destination = 'Microsoft.NET-ThirdPartyNotices.txt' }
        )) {
        $sourceNotice = Join-Path $dotnetRoot $notice.Source
        if (-not (Test-Path -LiteralPath $sourceNotice)) {
            throw "The .NET SDK license notice was not found: $sourceNotice"
        }
        Copy-Item -LiteralPath $sourceNotice -Destination (Join-Path $appOutput $notice.Destination) -Force
    }

    Get-FileHash (Join-Path $appOutput 'WlanGO.exe'), (Join-Path $appOutput 'backend\WlanGO.ControlBackend.exe') -Algorithm SHA256 |
        Select-Object Path, Hash
    Get-Item (Join-Path $appOutput 'WlanGO.exe'), (Join-Path $appOutput 'backend\WlanGO.ControlBackend.exe') |
        Select-Object FullName, Length, LastWriteTime
    Write-Host "App output directory: $appOutput"
}
finally {
    Pop-Location
    if ($null -eq $oldDotnetRoot) { Remove-Item Env:DOTNET_ROOT -ErrorAction SilentlyContinue } else { $env:DOTNET_ROOT = $oldDotnetRoot }
    if ($null -eq $oldTelemetryOptOut) { Remove-Item Env:DOTNET_CLI_TELEMETRY_OPTOUT -ErrorAction SilentlyContinue } else { $env:DOTNET_CLI_TELEMETRY_OPTOUT = $oldTelemetryOptOut }
    if ($null -eq $oldNoLogo) { Remove-Item Env:DOTNET_NOLOGO -ErrorAction SilentlyContinue } else { $env:DOTNET_NOLOGO = $oldNoLogo }
}
