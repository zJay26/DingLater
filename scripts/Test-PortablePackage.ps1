[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackagePath,
    [Parameter(Mandatory)]
    [ValidatePattern('^[a-fA-F0-9]{64}$')]
    [string]$ExpectedSha256,
    [string]$ExpectedVersion = '',
    [switch]$SkipSmokeTest,
    [switch]$TestValidationFailures
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$zipPath = (Resolve-Path -LiteralPath $PackagePath).Path
if ((Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash -ne $ExpectedSha256) {
    throw 'ZIP SHA-256 mismatch.'
}
$validationBase = [IO.Path]::GetFullPath((Join-Path $repoRoot '.tools\package-validation'))
$expanded = [IO.Path]::GetFullPath((Join-Path $validationBase ([guid]::NewGuid().ToString('N'))))
if (-not $expanded.StartsWith($validationBase + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Validation directory must stay inside the task staging directory.'
}
New-Item -ItemType Directory -Path $expanded -Force | Out-Null
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            $target = [IO.Path]::GetFullPath((Join-Path $expanded $entry.FullName))
            if (-not $target.StartsWith($expanded + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
                [IO.Path]::IsPathRooted($entry.FullName) -or $entry.FullName.Contains(':') -or -not $names.Add($target)) {
                throw "Unsafe or duplicate ZIP entry: $($entry.FullName)"
            }
        }
        $fileCount = @($archive.Entries | Where-Object { $_.Name }).Count
    }
    finally { $archive.Dispose() }
    [IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $expanded)
    & (Join-Path $PSScriptRoot 'Verify-Portable.ps1') -Path $expanded -ExpectedVersion $ExpectedVersion
    $manifest = Get-Content -LiteralPath (Join-Path $expanded 'package-files.json') -Raw | ConvertFrom-Json
    if ($fileCount -ne @($manifest.files).Count + 1) { throw 'ZIP contains files outside its manifest.' }
    if ($TestValidationFailures) {
        $runtimePath = Join-Path $expanded 'System.Private.CoreLib.dll'
        $savedRuntime = Join-Path $expanded 'System.Private.CoreLib.dll.test-backup'
        Move-Item -LiteralPath $runtimePath -Destination $savedRuntime
        try {
            $rejected = $false
            try { & (Join-Path $PSScriptRoot 'Verify-Portable.ps1') -Path $expanded }
            catch { $rejected = $_.Exception.Message.Contains('Missing: System.Private.CoreLib.dll') }
            if (-not $rejected) { throw 'Validator failed to reject an incomplete extraction.' }
        }
        finally { Move-Item -LiteralPath $savedRuntime -Destination $runtimePath }
        $resourcePath = Join-Path $expanded 'App.xbf'
        $original = [IO.File]::ReadAllBytes($resourcePath)
        try {
            $changed = [byte[]]$original.Clone()
            $changed[0] = $changed[0] -bxor 1
            [IO.File]::WriteAllBytes($resourcePath, $changed)
            $rejected = $false
            try { & (Join-Path $PSScriptRoot 'Verify-Portable.ps1') -Path $expanded }
            catch { $rejected = $_.Exception.Message.Contains('SHA-256 mismatch: App.xbf') }
            if (-not $rejected) { throw 'Validator failed to reject a same-size corrupted resource.' }
        }
        finally { [IO.File]::WriteAllBytes($resourcePath, $original) }
        Write-Host 'Validation regressions passed: missing runtime and corrupted XAML resource rejected.'
    }
    if (-not $SkipSmokeTest) {
        $executable = Join-Path $expanded 'DingLater.exe'
        $process = Start-Process -FilePath $executable -ArgumentList '--package-smoke-test' -WorkingDirectory $expanded -WindowStyle Hidden -PassThru
        try {
            $windowSeen = $false
            $deadline = [DateTime]::UtcNow.AddSeconds(25)
            while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
                $process.Refresh()
                if ($process.MainWindowHandle -ne 0) { $windowSeen = $true }
                Start-Sleep -Milliseconds 100
            }
            if (-not $process.HasExited) { throw 'Extracted app did not exit after its smoke test.' }
            if (-not $windowSeen -or $process.ExitCode -ne 0) {
                throw "Extracted app smoke test failed: window=$windowSeen, exit=$($process.ExitCode)."
            }
        }
        finally {
            if (-not $process.HasExited -and [string]::Equals([IO.Path]::GetFullPath($process.Path), $executable, [StringComparison]::OrdinalIgnoreCase)) {
                Stop-Process -Id $process.Id -Force
            }
            $process.Dispose()
        }
    }
    Write-Host "ZIP extraction verified: $fileCount files."
}
finally {
    if (Test-Path -LiteralPath $expanded) { Remove-Item -LiteralPath $expanded -Recurse -Force }
}
