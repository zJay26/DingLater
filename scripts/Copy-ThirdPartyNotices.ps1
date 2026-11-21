[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishRoot,
    [Parameter(Mandatory)][string]$AssetsPath,
    [Parameter(Mandatory)][string]$PackagesRoot
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$licensesRoot = Join-Path $PublishRoot 'licenses'
New-Item -ItemType Directory -Path $licensesRoot -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE'), (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md') -Destination $PublishRoot
$sharedLicenses = @('H.NotifyIcon-LICENSE.txt', 'dotnet-LICENSE.txt', 'SQLitePCLRaw-LICENSE.txt', 'SQLitePCLRaw-NOTICE.txt')
foreach ($document in $sharedLicenses) {
    Copy-Item -LiteralPath (Join-Path (Join-Path $repoRoot 'licenses') $document) -Destination $licensesRoot
}

$assets = Get-Content -LiteralPath $AssetsPath -Raw | ConvertFrom-Json
$windowsTarget = $assets.targets.PSObject.Properties | Where-Object Name -like '*/win-x64'
if ($null -eq $windowsTarget) { throw 'The app restore did not resolve a win-x64 target.' }

# The Windows App SDK also copies native files through its MSBuild targets.
$packageVersions = @(
    foreach ($package in $windowsTarget.Value.PSObject.Properties) {
        $entry = $package.Value
        if ($entry.type -eq 'package' -and ($entry.runtime -or $entry.runtimeTargets -or $entry.native -or $package.Name -like 'Microsoft.WindowsAppSDK*')) {
            $package.Name
        }
    }
    $frameworkPacks = @('Microsoft.NETCore.App.Host.win-x64', 'Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.Windows.SDK.NET.Ref')
    foreach ($pack in $assets.project.frameworks.PSObject.Properties.Value.downloadDependencies) {
        if ($pack.name -in $frameworkPacks) {
            $version = ($pack.version -split ',')[0].Trim([char[]]'[] ')
            "$($pack.name)/$version"
        }
    }
)

# These packages have a shared upstream license or a vendor license URL in the notice index.
$licensesInIndex = @(
    'H.GeneratedIcons.System.Drawing', 'H.NotifyIcon', 'H.NotifyIcon.WinUI',
    'Microsoft.Data.Sqlite', 'Microsoft.Data.Sqlite.Core', 'Microsoft.Win32.SystemEvents',
    'Microsoft.Windows.SDK.NET.Ref', 'System.Security.Cryptography.ProtectedData',
    'SQLitePCLRaw.config.e_sqlite3', 'SQLitePCLRaw.core', 'SQLitePCLRaw.provider.e_sqlite3'
)

foreach ($packageVersion in ($packageVersions | Sort-Object -Unique)) {
    $packagePath = Join-Path $PackagesRoot ($packageVersion.ToLowerInvariant())
    $noticeName = '^(license|licence|copying|copyright|notices?|third[-_. ]*party[-_. ]*notices?|eula)(\.(txt|md|rtf|html))?$'
    $documents = @(Get-ChildItem -LiteralPath $packagePath -File | Where-Object Name -match $noticeName)
    $id = ($packageVersion -split '/', 2)[0]
    if ($id -notin $licensesInIndex -and -not ($documents.Name -match '^(license|licence|copying)(\.|$)')) {
        throw "License missing for published component: $packageVersion"
    }
    if ($documents.Count -gt 0) {
        $destination = Join-Path $licensesRoot $packageVersion
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        $documents | Copy-Item -Destination $destination
    }
}
