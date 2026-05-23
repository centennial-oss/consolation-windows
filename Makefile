APP_NAME := Consolation
PROJECT := Consolation/Consolation.csproj
BUILD_INFO := Consolation/BuildInfo.cs
PACKAGE_MANIFEST := Consolation/Package.appxmanifest
RELEASE_VERSION ?= localdev
ifeq ($(RELEASE_VERSION),localdev)
MSIX_VERSION ?= 1.0.0.0
else
MSIX_VERSION ?= $(RELEASE_VERSION).0
endif
BUILD_DATE := $(shell powershell -NoProfile -Command "[DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')")
GIT_COMMIT := $(shell powershell -NoProfile -Command "if (Get-Command git -ErrorAction SilentlyContinue) { git rev-parse HEAD } else { 'localdev' }")
DOTNET := dotnet
MSBUILD_RELEASE_PROPS := -c Release -p:AppxPackageSigningEnabled=false -p:GenerateAppxPackageOnBuild=true -p:AppxBundle=Never -p:AppxPackageVersion=$(MSIX_VERSION)

.PHONY: build build-debug build-release build-release-amd64 build-release-arm64 set-release-version-info clear-version-info

build: build-debug

build-debug:
	$(DOTNET) build $(PROJECT) -c Debug

build-release: set-release-version-info build-release-amd64 build-release-arm64 clear-version-info

build-release-amd64:
	$(DOTNET) publish $(PROJECT) $(MSBUILD_RELEASE_PROPS) -p:Platform=x64 -r win-x64

build-release-arm64:
	$(DOTNET) publish $(PROJECT) $(MSBUILD_RELEASE_PROPS) -p:Platform=ARM64 -r win-arm64

set-release-version-info:
	powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Set-BuildInfo.ps1 -Path $(BUILD_INFO) -Version $(RELEASE_VERSION) -BuildType Release -BuildDate $(BUILD_DATE) -CommitId $(GIT_COMMIT) -Architecture Windows
	powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Set-PackageVersion.ps1 -Path $(PACKAGE_MANIFEST) -Version $(MSIX_VERSION)

clear-version-info:
	powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Set-BuildInfo.ps1 -Path $(BUILD_INFO)
	powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Set-PackageVersion.ps1 -Path $(PACKAGE_MANIFEST)
