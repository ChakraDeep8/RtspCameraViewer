<#
.SYNOPSIS
    Publishes RTSP Camera Viewer and compiles the one-click installer.

.DESCRIPTION
    Produces dist\RtspCameraViewer-Setup-v<version>.exe from a clean self-contained win-x64
    publish. Self-contained means the machine it installs on needs neither .NET nor VLC.

.PARAMETER Version
    Version stamped into the installer, its filename and Add/Remove Programs, e.g. 1.4.0.

.PARAMETER SkipPublish
    Reuse whatever is already in publish\ instead of rebuilding it. For iterating on the
    installer script itself, where re-publishing 300 MB each time is just waiting.

.EXAMPLE
    .\installer\build-installer.ps1 -Version 1.4.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path -Parent $PSScriptRoot
$project    = Join-Path $repoRoot 'RtspCameraViewer\RtspCameraViewer.csproj'
$publishDir = Join-Path $repoRoot 'publish'
$distDir    = Join-Path $repoRoot 'dist'
$issFile    = Join-Path $PSScriptRoot 'RtspCameraViewer.iss'

# Inno Setup installs per-user by default (winget), per-machine when installed by hand, so look
# in both rather than hard-coding one and failing on the other machine.
$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6 not found. Install it with:  winget install --id JRSoftware.InnoSetup"
}

if (-not $SkipPublish) {
    Write-Host "Publishing $Version (self-contained win-x64)..." -ForegroundColor Cyan

    # A stale publish folder is how a removed file survives into the next installer, so this is a
    # clean build rather than a copy over the top.
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    & dotnet publish $project -c Release -r win-x64 --self-contained true `
        -p:Version=$Version -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

    # LibVLC ships both architectures; the 32-bit half is ~100 MB that an x64-only build can
    # never load.
    $x86 = Join-Path $publishDir 'libvlc\win-x86'
    if (Test-Path $x86) { Remove-Item $x86 -Recurse -Force }
}

$exe = Join-Path $publishDir 'RtspCameraViewer.exe'
if (-not (Test-Path $exe)) { throw "No published app at $exe - run without -SkipPublish." }

$plugins = Join-Path $publishDir 'libvlc\win-x64\plugins'
if (-not (Test-Path $plugins)) {
    # Worth failing loudly: without the plugin tree the app installs and starts, then fails on
    # every single camera, which looks like a broken build rather than a broken installer.
    throw "LibVLC plugins missing from $plugins - the installer would produce an app that cannot decode."
}

New-Item -ItemType Directory -Force -Path $distDir | Out-Null

Write-Host "Compiling installer..." -ForegroundColor Cyan
& $iscc "/DAppVersion=$Version" "/DPublishDir=$publishDir" $issFile
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

$setup = Join-Path $distDir "RtspCameraViewer-Setup-v$Version.exe"
$sizeMb = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host ""
Write-Host "Installer ready: $setup ($sizeMb MB)" -ForegroundColor Green
