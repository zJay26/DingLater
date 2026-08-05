[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version = '2.1.0',
    [string]$OutputDirectory = 'artifacts\release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifactRoot = Join-Path $repoRoot 'artifacts'
$publishRoot = Join-Path $artifactRoot 'portable-publish'
$releaseRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
}
$artifactPrefix = $artifactRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
foreach ($target in @($publishRoot, $releaseRoot)) {
    $resolvedTarget = [IO.Path]::GetFullPath($target)
    if (-not $resolvedTarget.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path outside the repository artifacts directory: $resolvedTarget"
    }

    if (Test-Path -LiteralPath $resolvedTarget) {
        Remove-Item -LiteralPath $resolvedTarget -Recurse -Force
    }

    New-Item -ItemType Directory -Path $resolvedTarget -Force | Out-Null
}

$dotnet = Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}

$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.tools\cli-home'
$env:NUGET_PACKAGES = Join-Path $repoRoot '.tools\nuget-packages'

& $dotnet restore (Join-Path $repoRoot 'DingLater.slnx') --locked-mode --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

& $dotnet test (Join-Path $repoRoot 'DingLater.slnx') -c $Configuration --no-restore --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet test failed.' }

& $dotnet publish (Join-Path $repoRoot 'src\DingLater.App\DingLater.App.csproj') `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    --no-restore `
    -o $publishRoot `
    -p:Version=$Version `
    -p:PublishSingleFile=false `
    -p:DebugSymbols=false `
    -p:DebugType=None
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

$requiredWinUIResources = @(
    'DingLater.pri',
    'App.xbf',
    'Styles\Typography.Small.xbf',
    'Styles\Typography.Standard.xbf',
    'Styles\Typography.Large.xbf',
    'Styles\Typography.ExtraLarge.xbf',
    'Views\MainWindow.xbf',
    'Views\ShellPage.xbf',
    'Views\InboxPage.xbf',
    'Views\SettingsPage.xbf',
    'Views\OnboardingPage.xbf'
)
foreach ($relativePath in $requiredWinUIResources) {
    $resourcePath = Join-Path $publishRoot $relativePath
    if (-not (Test-Path -LiteralPath $resourcePath -PathType Leaf)) {
        throw "Published WinUI resource is missing: $relativePath"
    }
}

$publishedExe = Join-Path $publishRoot 'DingLater.exe'
$smokeProcess = Start-Process `
    -FilePath $publishedExe `
    -ArgumentList '--package-smoke-test' `
    -PassThru `
    -WindowStyle Hidden
$smokeWindowSeen = $false
$smokeDeadline = [DateTime]::UtcNow.AddSeconds(15)
try {
    while ([DateTime]::UtcNow -lt $smokeDeadline -and -not $smokeProcess.HasExited) {
        $smokeProcess.Refresh()
        if ($smokeProcess.MainWindowHandle -ne 0) {
            $smokeWindowSeen = $true
            break
        }

        Start-Sleep -Milliseconds 100
    }

    if (-not $smokeWindowSeen) {
        $exitDetail = if ($smokeProcess.HasExited) { "exit code $($smokeProcess.ExitCode)" } else { 'no window handle' }
        throw "Published DingLater failed its window smoke test: $exitDetail."
    }

    if (-not $smokeProcess.WaitForExit(10000)) {
        throw 'Published DingLater window smoke test did not exit on schedule.'
    }

    if ($smokeProcess.ExitCode -ne 0) {
        throw "Published DingLater window smoke test exited with code $($smokeProcess.ExitCode)."
    }
}
finally {
    if (-not $smokeProcess.HasExited) {
        $smokeProcess.Refresh()
        if ([string]::Equals(
                [IO.Path]::GetFullPath($smokeProcess.Path),
                [IO.Path]::GetFullPath($publishedExe),
                [StringComparison]::OrdinalIgnoreCase)) {
            Stop-Process -Id $smokeProcess.Id -Force
        }
    }
}

Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $publishRoot
$docsRoot = Join-Path $publishRoot 'docs'
New-Item -ItemType Directory -Path $docsRoot -Force | Out-Null
foreach ($document in @('PRIVACY.md', 'COMPATIBILITY.md', 'TESTING.md', 'DISTRIBUTION.md')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot "docs\$document") -Destination $docsRoot
}

$zipName = "DingLater-$Version-win-x64-portable.zip"
$zipPath = Join-Path $releaseRoot $zipName
Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash *$zipName" | Set-Content -LiteralPath (Join-Path $releaseRoot 'SHA256SUMS.txt') -Encoding ascii

Write-Host "Portable package: $zipPath"
Write-Host "SHA-256: $hash"
