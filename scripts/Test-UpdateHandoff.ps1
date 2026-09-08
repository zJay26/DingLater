[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$ExpectedVersion
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$validationBase = [IO.Path]::GetFullPath((Join-Path $repoRoot '.tools\update-validation'))
$runRoot = [IO.Path]::GetFullPath((Join-Path $validationBase ([guid]::NewGuid().ToString('N'))))
if (-not $runRoot.StartsWith($validationBase + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Update validation path must remain in task staging.'
}
$target = Join-Path $runRoot 'installed app'
$source = Join-Path $runRoot 'download app'
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path -LiteralPath $PackagePath).Path, $target)
    [IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path -LiteralPath $PackagePath).Path, $source)
    # Exercise this build as an older installation; never modify real user data or a running installed app.
    $manifestPath = Join-Path $target 'package-files.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $manifest.version = '0.0.0'
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    'preserve this user file' | Set-Content -LiteralPath (Join-Path $target 'user-file.txt') -Encoding utf8
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $target 'DingLater.exe'))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.WorkingDirectory = $target
    $start.Arguments = '--package-smoke-test --update-handoff-smoke-test "' + $source + '"'
    $process = [Diagnostics.Process]::Start($start)
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(120)
        while ([DateTime]::UtcNow -lt $deadline) {
            $errors = @(Get-ChildItem -LiteralPath $runRoot -Filter '*.error' -Recurse -File)
            if ($errors.Count) { throw (($errors | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n") }
            if (@(Get-ChildItem -LiteralPath $runRoot -Filter '*.success' -File).Count -eq 1) { break }
            Start-Sleep -Milliseconds 200
        }
        if (@(Get-ChildItem -LiteralPath $runRoot -Filter '*.success' -File).Count -ne 1) { throw 'Update handoff timed out.' }
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "Original smoke process exited with $($process.ExitCode)." }
        & (Join-Path $PSScriptRoot 'Verify-Portable.ps1') -Path $target -ExpectedVersion $ExpectedVersion
        if (-not (Test-Path -LiteralPath (Join-Path $target 'user-file.txt'))) { throw 'Unmanaged user file was not preserved.' }
        if (@(Get-ChildItem -LiteralPath (Join-Path $target '.dinglater-backup') -Directory).Count -ne 1) { throw 'Old installation backup missing.' }
        Write-Host 'Update handoff passed: helper ready, original exited, files replaced, backup retained, updated app restarted and exited successfully.'
    }
    finally { $process.Dispose() }
}
finally {
    # Stop only helpers/smoke processes belonging to this unique validation directory before cleanup.
    foreach ($candidate in @(Get-Process DingLater -ErrorAction SilentlyContinue)) {
        if ($candidate.Path -and $candidate.Path.StartsWith($runRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            Stop-Process -Id $candidate.Id -Force -ErrorAction SilentlyContinue
            $candidate.WaitForExit()
        }
    }
    if (Test-Path -LiteralPath $runRoot) { Remove-Item -LiteralPath $runRoot -Recurse -Force }
}
