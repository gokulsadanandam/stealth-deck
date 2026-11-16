<#
  Local build: publishes the self-contained exe and, if Inno Setup is installed, the installer.
  Usage:  .\build.ps1            (uses the Version from StealthDeck.csproj)
          .\build.ps1 -Version 1.2.0
  Output: .\publish\StealthDeck.exe, .\dist\StealthDeck-Setup-<version>.exe, .\dist\StealthDeck-<version>-portable.zip
#>
param([string]$Version)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $Version) {
    $Version = ([xml](Get-Content .\StealthDeck.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
Write-Host "Building StealthDeck $Version"

if (Test-Path .\publish) { Remove-Item .\publish -Recurse -Force }
if (Test-Path .\dist) { Remove-Item .\dist -Recurse -Force }
New-Item -ItemType Directory .\dist | Out-Null

dotnet publish .\StealthDeck.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false -o .\publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Compress-Archive -Path .\publish\StealthDeck.exe -DestinationPath ".\dist\StealthDeck-$Version-portable.zip" -Force

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($iscc) {
    & $iscc "/DAppVersion=$Version" "/DSourceDir=..\publish" .\installer\StealthDeck.iss
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
} else {
    Write-Warning "Inno Setup 6 not found - skipped the installer. Install it with: winget install JRSoftware.InnoSetup"
}

$wix = Get-Command wix -ErrorAction SilentlyContinue
if (-not $wix -and (Test-Path "$env:USERPROFILE\.dotnet\tools\wix.exe")) { $wix = "$env:USERPROFILE\.dotnet\tools\wix.exe" }
if ($wix) {
    & $wix build .\installer\StealthDeck.wxs -d Version=$Version -d SourceDir=publish -arch x64 -o ".\dist\StealthDeck-$Version.msi"
    if ($LASTEXITCODE -ne 0) { throw "WiX build failed" }
} else {
    Write-Warning "WiX not found - skipped the MSI. Install it with: dotnet tool install --global wix --version 5.0.2"
}

Get-ChildItem .\dist | Select-Object Name, @{n='MB'; e={[math]::Round($_.Length / 1MB, 1)}}
