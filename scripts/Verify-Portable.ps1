[CmdletBinding()]
param(
    [string]$Path = $PSScriptRoot,
    [string]$ExpectedVersion = ''
)

$ErrorActionPreference = 'Stop'
$packageRoot = (Resolve-Path -LiteralPath $Path).Path
$prefix = $packageRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$manifestPath = Join-Path $packageRoot 'package-files.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw 'Package manifest is missing. Extract the entire ZIP into a new folder before running DingLater.'
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schema -ne 1 -or @($manifest.files).Count -eq 0) {
    throw 'Invalid package manifest.'
}
if ($ExpectedVersion -and $manifest.version -ne $ExpectedVersion) {
    throw "Package version mismatch: expected $ExpectedVersion, found $($manifest.version)."
}
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$failures = [Collections.Generic.List[string]]::new()
foreach ($file in $manifest.files) {
    $relative = [string]$file.path
    $fullPath = [IO.Path]::GetFullPath((Join-Path $packageRoot $relative))
    if ([IO.Path]::IsPathRooted($relative) -or -not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
        $relative.Contains(':') -or -not $seen.Add($relative)) {
        throw "Invalid package path: $relative"
    }
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        $failures.Add("Missing: $relative")
        continue
    }
    if ((Get-Item -LiteralPath $fullPath).Length -ne [long]$file.size) {
        $failures.Add("Size mismatch: $relative")
        continue
    }
    $actual = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
    if ($actual -ne $file.sha256) {
        $failures.Add("SHA-256 mismatch: $relative")
    }
}
foreach ($required in @('DingLater.exe', 'DingLater.dll', 'System.Private.CoreLib.dll', 'DingLater.pri', 'App.xbf', 'Views/MainWindow.xbf', 'Views/ShellPage.xbf', 'Views/InboxPage.xbf', 'Views/SettingsPage.xbf', 'Verify-Portable.ps1')) {
    if (-not $seen.Contains($required)) {
        $failures.Add("Required file absent from manifest: $required")
    }
}
if ($failures.Count -gt 0) {
    throw ("Package is incomplete or damaged. Extract the ZIP again into a new folder.`n" + ($failures -join "`n"))
}
$productVersion = (Get-Item -LiteralPath (Join-Path $packageRoot 'DingLater.exe')).VersionInfo.ProductVersion
if (-not $productVersion.StartsWith([string]$manifest.version + '+', [StringComparison]::Ordinal) -and $productVersion -ne $manifest.version) {
    throw "Executable version does not match manifest: $productVersion"
}
Write-Host "Verified $($seen.Count) files; DingLater $productVersion."
