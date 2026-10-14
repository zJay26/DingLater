[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExecutablePath,
    [Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$executable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$executableDirectory = Split-Path -Parent $executable
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null

# Inspect only the window owned by the isolated smoke-test process.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class TrayHoverSmokeWindows {
    public delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    public static IntPtr Find(uint process) {
        IntPtr result = IntPtr.Zero;
        EnumWindows((window, parameter) => {
            uint owner; GetWindowThreadProcessId(window, out owner);
            if (owner != process || !IsWindowVisible(window)) return true;
            var title = new StringBuilder(256); GetWindowText(window, title, title.Capacity);
            if (!title.ToString().StartsWith("DingLater · 待处理", StringComparison.Ordinal)) return true;
            result = window; return false;
        }, IntPtr.Zero);
        return result;
    }
}
'@

$started = [DateTime]::UtcNow
$process = Start-Process -FilePath $executable -ArgumentList '--package-smoke-test --tray-hover-smoke-test' `
    -WorkingDirectory $executableDirectory -WindowStyle Hidden -PassThru
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    $window = [IntPtr]::Zero
    while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        $window = [TrayHoverSmokeWindows]::Find([uint32]$process.Id)
        if ($window -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 30
    }
    if ($window -eq [IntPtr]::Zero) { throw 'The isolated tray flyout was not displayed.' }
    if (-not $process.WaitForExit(20000)) { throw 'The isolated tray smoke test did not finish.' }
    $errorPath = Join-Path $executableDirectory 'tray-hover-smoke.error'
    if ($process.ExitCode -ne 0) {
        if (Test-Path -LiteralPath $errorPath) { Get-Content -LiteralPath $errorPath }
        throw "Tray smoke test exited with $($process.ExitCode)."
    }
    $reportPath = Join-Path $executableDirectory 'tray-hover-smoke.json'
    $reportFile = Get-Item -LiteralPath $reportPath
    if ($reportFile.LastWriteTimeUtc -lt $started) { throw 'The tray smoke report is stale.' }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if (-not $report.Passed -or $report.BeforePending -ne 12 -or $report.BeforeContacts -ne 5 `
        -or $report.AfterPending -ne 0 -or $report.SnoozedPreserved -ne 1 -or $report.Handled -ne 13 `
        -or -not $report.NonActivating) { throw 'Unexpected tray smoke result.' }
    Copy-Item -LiteralPath $reportPath -Destination (Join-Path $output 'tray-hover-smoke.json') -Force
    $previewPath = Join-Path $executableDirectory 'tray-hover-B-actual.png'
    if ((Get-Item -LiteralPath $previewPath).Length -eq 0) { throw 'The rendered flyout preview is empty.' }
    Copy-Item -LiteralPath $previewPath -Destination (Join-Path $output 'tray-hover-B-actual.png') -Force
    $report | ConvertTo-Json
}
finally {
    # This process handle was created above with synthetic-data-only smoke flags.
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    $process.Dispose()
}
