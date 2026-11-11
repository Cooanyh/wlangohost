param(
    [string]$AppDirectory,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

$desktopRoot = $PSScriptRoot
$repoRoot = Split-Path -Parent $desktopRoot
$defaultPackageRoot = Join-Path $desktopRoot 'artifacts\Release\WlanGO-Package\app'
$installerScript = Join-Path $desktopRoot 'installer\WlanGO.iss'
$artifactRoot = Join-Path $desktopRoot 'artifacts'
$defaultOutputRoot = Join-Path $desktopRoot 'artifacts\Release\WlanGO-Installer'
$packageRoot = if ([string]::IsNullOrWhiteSpace($AppDirectory)) {
    $defaultPackageRoot
}
elseif ([System.IO.Path]::IsPathRooted($AppDirectory)) {
    [System.IO.Path]::GetFullPath($AppDirectory)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $desktopRoot $AppDirectory))
}
$outputRoot = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $defaultOutputRoot
}
elseif ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    [System.IO.Path]::GetFullPath($OutputDirectory)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $desktopRoot $OutputDirectory))
}
$artifactPrefix = [System.IO.Path]::GetFullPath($artifactRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $outputRoot.StartsWith($artifactPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The installer output must stay inside $artifactRoot."
}
$projectPath = Join-Path $desktopRoot 'src\PadP2P.App\PadP2P.App.csproj'

if (-not (Test-Path -LiteralPath (Join-Path $packageRoot 'WlanGO.exe'))) {
    throw "Published app was not found at $packageRoot. Run Build-Desktop.ps1 first."
}
if (-not (Test-Path -LiteralPath (Join-Path $packageRoot 'backend\WlanGO.ControlBackend.exe'))) {
    throw 'The native control backend is missing from the published app.'
}

[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$version = $project.Project.PropertyGroup.Version | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($version)) { throw 'Could not read the application version from the project file.' }

if ($env:GITHUB_REF_TYPE -eq 'tag' -and $env:GITHUB_REF_NAME -match '^v(.+)$' -and $Matches[1] -ne $version) {
    throw "Release tag $env:GITHUB_REF_NAME does not match app version $version."
}

$compiler = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
$compilerPath = if ($compiler) { $compiler.Source } else { $null }
if (-not $compilerPath) {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    $compilerPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $compilerPath) { throw 'Inno Setup 6 compiler ISCC.exe was not found. Install it from https://jrsoftware.org/isinfo.php.' }

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
Push-Location $desktopRoot
try {
    & $compilerPath '/Qp' "/DAppVersion=$version" "/DPackageDir=$packageRoot" "/DOutputDir=$outputRoot" $installerScript
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}

$installerPath = Join-Path $outputRoot "WlanGO-Setup-$version-x64.exe"
if (-not (Test-Path -LiteralPath $installerPath)) { throw "Installer output was not created: $installerPath" }

$hash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumPath = Join-Path $outputRoot 'SHA256SUMS.txt'
$checksumText = "{0} *{1}`r`n" -f $hash, (Split-Path -Leaf $installerPath)
[System.IO.File]::WriteAllText($checksumPath, $checksumText, [System.Text.UTF8Encoding]::new($false))

$changelogPath = Join-Path $repoRoot 'CHANGELOG.md'
if (-not (Test-Path -LiteralPath $changelogPath)) { throw "Changelog was not found: $changelogPath" }
$sectionPattern = "(?ms)^##[ \t]+$([regex]::Escape($version))[ \t]*\r?\n(?<body>.*?)(?=^##[ \t]|\z)"
$changelog = [System.IO.File]::ReadAllText($changelogPath)
$releaseSection = [regex]::Match($changelog, $sectionPattern)
if (-not $releaseSection.Success) { throw "No changelog section was found for version $version." }
$notesPath = Join-Path $outputRoot 'RELEASE-NOTES.md'
$notes = $releaseSection.Groups['body'].Value.Trim() + "`r`n"
[System.IO.File]::WriteAllText($notesPath, $notes, [System.Text.UTF8Encoding]::new($false))

Get-Item -LiteralPath $installerPath, $checksumPath, $notesPath | Select-Object FullName, Length
Write-Host "SHA256 $hash  $(Split-Path -Leaf $installerPath)"
