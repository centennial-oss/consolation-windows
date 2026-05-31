#Requires -Version 7.0
<#
.SYNOPSIS
  Builds a Partner Center-ready .msixupload from release .msix packages.

.DESCRIPTION
  Collects architecture-specific app packages (not dependency packages), bundles them
  with MakeAppx.exe, and wraps the bundle in a .msixupload zip as required by the Store.
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $ReleaseVersion,

    [string] $WorkspaceRoot = (Get-Location).Path,

    [string] $OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'

if (-not $OutputDirectory) {
    $OutputDirectory = $WorkspaceRoot
}

# Match Makefile: RELEASE_VERSION 1.1.0 -> MSIX package version 1.1.0.0
$msixVersion = if ($ReleaseVersion -match '^\d+\.\d+\.\d+\.\d+$') {
    $ReleaseVersion
} else {
    "$ReleaseVersion.0"
}

$appPackagesRoot = Join-Path $WorkspaceRoot 'Consolation/AppPackages'
$architectures = @(
    @{ Folder = "Consolation_${msixVersion}_x86_Test"; Package = "Consolation_${msixVersion}_x86.msix" },
    @{ Folder = "Consolation_${msixVersion}_x64_Test"; Package = "Consolation_${msixVersion}_x64.msix" },
    @{ Folder = "Consolation_${msixVersion}_arm64_Test"; Package = "Consolation_${msixVersion}_arm64.msix" }
)

$stageDir = Join-Path $env:RUNNER_TEMP "msix-bundle-stage"
if (Test-Path $stageDir) {
    Remove-Item -Path $stageDir -Recurse -Force
}
New-Item -ItemType Directory -Path $stageDir | Out-Null

$stagedPackages = @()
foreach ($arch in $architectures) {
    $packagePath = Join-Path $appPackagesRoot (Join-Path $arch.Folder $arch.Package)
    if (-not (Test-Path $packagePath)) {
        throw "Expected app package not found: $packagePath"
    }
    $dest = Join-Path $stageDir $arch.Package
    Copy-Item -Path $packagePath -Destination $dest
    $stagedPackages += $dest
    Write-Host "Staged: $packagePath"
}

$makeAppxCandidates = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter 'makeappx.exe' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\makeappx\.exe$' }

$makeAppx = $makeAppxCandidates | Sort-Object {
    $versionFolder = $_.Directory.Parent.Name
    try { [version]$versionFolder } catch { [version]'0.0.0.0' }
} -Descending | Select-Object -First 1

if (-not $makeAppx) {
    throw 'makeappx.exe was not found. Install the Windows 10/11 SDK on the runner.'
}

Write-Host "Using MakeAppx: $($makeAppx.FullName)"

$bundleName = "Consolation_${msixVersion}_x86_x64_arm64.msixbundle"
$bundlePath = Join-Path $OutputDirectory $bundleName
if (Test-Path $bundlePath) {
    Remove-Item $bundlePath -Force
}

& $makeAppx.FullName bundle /d $stageDir /p $bundlePath /bv $msixVersion
if ($LASTEXITCODE -ne 0) {
    throw "MakeAppx bundle failed with exit code $LASTEXITCODE"
}

Write-Host "Created bundle: $bundlePath"

$uploadContents = @($bundlePath)
$symFiles = Get-ChildItem -Path $appPackagesRoot -Recurse -Filter '*.appxsym' -ErrorAction SilentlyContinue
if ($symFiles) {
    Write-Host 'Including symbol files:'
    $symFiles.FullName | ForEach-Object { Write-Host "  $_" }
    $uploadContents += $symFiles.FullName
}

$uploadName = "Consolation_${msixVersion}_x86_x64_arm64.msixupload"
$uploadZip = Join-Path $env:RUNNER_TEMP "$uploadName.zip"
$uploadPath = Join-Path $OutputDirectory $uploadName

if (Test-Path $uploadZip) { Remove-Item $uploadZip -Force }
if (Test-Path $uploadPath) { Remove-Item $uploadPath -Force }

Compress-Archive -Path $uploadContents -DestinationPath $uploadZip -Force
Move-Item -Path $uploadZip -Destination $uploadPath -Force

Write-Host "Created upload package: $uploadPath"
Write-Host "MSIX_UPLOAD=$uploadPath"
Write-Host "MSIX_UPLOAD_FILENAME=$uploadName"

if ($env:GITHUB_OUTPUT) {
    "msix_upload=$uploadPath" >> $env:GITHUB_OUTPUT
    "msix_upload_filename=$uploadName" >> $env:GITHUB_OUTPUT
}
