[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$toolRoot = Join-Path $repoRoot '.tools'
$downloadRoot = Join-Path $toolRoot 'downloads'
$installRoot = Join-Path $toolRoot 'dotnet'
$installer = Join-Path $downloadRoot 'dotnet-install.ps1'

New-Item -ItemType Directory -Path $downloadRoot -Force | Out-Null
New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer `
    -Version '10.0.302' `
    -InstallDir $installRoot `
    -Architecture x64 `
    -NoPath
if ($LASTEXITCODE -ne 0) {
    throw '.NET SDK installation failed.'
}

$actual = & (Join-Path $installRoot 'dotnet.exe') --version
if ($actual.Trim() -ne '10.0.302') {
    throw "Expected .NET SDK 10.0.302, received $actual."
}
Write-Host ".NET SDK $actual is ready at $installRoot"
