[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version = '2.2.0',
    [string]$DotnetPath = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$publishRoot = [IO.Path]::GetFullPath((Join-Path $artifactRoot 'portable-publish'))
$outputRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'BuildOutput'))
$legacyPackagingRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'packaging'))
$zipName = "DingLater-$Version-win-x64-portable.zip"
$temporaryZip = Join-Path $artifactRoot $zipName
$temporaryChecksums = Join-Path $artifactRoot 'SHA256SUMS.txt'
$repoPrefix = $repoRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
foreach ($target in @($artifactRoot, $publishRoot, $outputRoot, $legacyPackagingRoot)) {
    if (-not $target.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to use a build path outside the repository: $target"
    }
}

function Remove-DirectoryWithRetry {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath,
        [int]$Attempts = 10
    )

    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        if (-not (Test-Path -LiteralPath $LiteralPath)) {
            return
        }

        try {
            Remove-Item -LiteralPath $LiteralPath -Recurse -Force
            return
        }
        catch {
            if ($attempt -eq $Attempts) {
                throw
            }

            Start-Sleep -Milliseconds 500
        }
    }
}

if (Test-Path -LiteralPath $artifactRoot) {
    Remove-DirectoryWithRetry -LiteralPath $artifactRoot
}
if (Test-Path -LiteralPath $legacyPackagingRoot) {
    $legacyPackagingContents = Get-ChildItem -LiteralPath $legacyPackagingRoot -Force
    if ($legacyPackagingContents.Count -eq 0) {
        Remove-Item -LiteralPath $legacyPackagingRoot -Force
    }
}

try {
    New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

    if (-not [string]::IsNullOrWhiteSpace($DotnetPath)) {
        $dotnet = [IO.Path]::GetFullPath($DotnetPath)
        if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
            throw "The requested dotnet executable does not exist: $dotnet"
        }
    }
    else {
        $dotnet = Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
    }
    if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
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

    foreach ($temporaryFile in @($temporaryZip, $temporaryChecksums)) {
        if (Test-Path -LiteralPath $temporaryFile) {
            Remove-Item -LiteralPath $temporaryFile -Force
        }
    }

    Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $temporaryZip -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $temporaryZip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash *$zipName" | Set-Content -LiteralPath $temporaryChecksums -Encoding ascii

    if (Test-Path -LiteralPath $outputRoot) {
        Remove-DirectoryWithRetry -LiteralPath $outputRoot
    }
    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
    Copy-Item -Path (Join-Path $publishRoot '*') -Destination $outputRoot -Recurse -Force
    Copy-Item -LiteralPath $temporaryZip -Destination $outputRoot
    Copy-Item -LiteralPath $temporaryChecksums -Destination $outputRoot

    Write-Host "Latest test app: $(Join-Path $outputRoot 'DingLater.exe')"
    Write-Host "Portable package: $(Join-Path $outputRoot $zipName)"
    Write-Host "SHA-256: $hash"
}
finally {
    if (Test-Path -LiteralPath $publishRoot) {
        Remove-DirectoryWithRetry -LiteralPath $publishRoot
    }
    foreach ($temporaryFile in @($temporaryZip, $temporaryChecksums)) {
        if (Test-Path -LiteralPath $temporaryFile) {
            Remove-Item -LiteralPath $temporaryFile -Force
        }
    }
    if (Test-Path -LiteralPath $artifactRoot) {
        Remove-DirectoryWithRetry -LiteralPath $artifactRoot
    }
}
