#Requires -Version 7.0
<#
.SYNOPSIS
  Builds Partner Center-ready .msixupload files from release .msix packages.

.DESCRIPTION
  Creates one .msixupload per architecture (matching Partner Center naming),
  each a zip containing that architecture's .msix package.
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $ReleaseVersion,

    [string] $WorkspaceRoot = (Get-Location).Path
)

$ErrorActionPreference = 'Stop'

# Match Makefile: RELEASE_VERSION 1.1.0 -> MSIX package version 1.1.0.0
$msixVersion = if ($ReleaseVersion -match '^\d+\.\d+\.\d+\.\d+$') {
    $ReleaseVersion
} else {
    "$ReleaseVersion.0"
}

$appPackagesRoot = Join-Path $WorkspaceRoot 'Consolation/AppPackages'
$outputDir = Join-Path $WorkspaceRoot 'store-packages'
if (Test-Path $outputDir) {
    Remove-Item -Path $outputDir -Recurse -Force
}
New-Item -ItemType Directory -Path $outputDir | Out-Null

$architectures = @(
    @{ Arch = 'x86'; Folder = "Consolation_${msixVersion}_x86_Test"; Package = "Consolation_${msixVersion}_x86.msix" },
    @{ Arch = 'x64'; Folder = "Consolation_${msixVersion}_x64_Test"; Package = "Consolation_${msixVersion}_x64.msix" },
    @{ Arch = 'arm64'; Folder = "Consolation_${msixVersion}_arm64_Test"; Package = "Consolation_${msixVersion}_arm64.msix" }
)

$uploadPaths = @()
foreach ($arch in $architectures) {
    $packagePath = Join-Path $appPackagesRoot (Join-Path $arch.Folder $arch.Package)
    if (-not (Test-Path $packagePath)) {
        throw "Expected app package not found: $packagePath"
    }

    $uploadName = "Consolation_${msixVersion}_$($arch.Arch).msixupload"
    $uploadZip = Join-Path $env:RUNNER_TEMP "$uploadName.zip"
    $uploadPath = Join-Path $outputDir $uploadName

    if (Test-Path $uploadZip) { Remove-Item $uploadZip -Force }
    if (Test-Path $uploadPath) { Remove-Item $uploadPath -Force }

    Compress-Archive -Path $packagePath -DestinationPath $uploadZip -Force
    Move-Item -Path $uploadZip -Destination $uploadPath -Force

    Write-Host "Created $uploadName from $packagePath"
    $uploadPaths += $uploadPath
}

Write-Host "Created $($uploadPaths.Count) upload package(s) in $outputDir"

if ($env:GITHUB_OUTPUT) {
    "msix_packages_dir=$outputDir" >> $env:GITHUB_OUTPUT
}
