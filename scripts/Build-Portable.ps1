[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version = '2.5.0',
    [string]$DotnetPath = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$runId = [guid]::NewGuid().ToString('N')
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot ".tools\portable-build-$runId"))
$publishRoot = Join-Path $artifactRoot 'publish'
$outputRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'BuildOutput'))
$backupRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot ".tools\portable-backup-$runId"))
$zipName = "DingLater-$Version-win-x64-portable.zip"
$temporaryZip = Join-Path $artifactRoot $zipName
$repoPrefix = $repoRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
foreach ($target in @($artifactRoot, $publishRoot, $outputRoot, $backupRoot)) {
    if (-not $target.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to use a build path outside the repository: $target"
    }
}

function Remove-DirectoryWithRetry {
    param([Parameter(Mandatory)][string]$LiteralPath, [int]$Attempts = 10)
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        if (-not (Test-Path -LiteralPath $LiteralPath)) { return }
        try { Remove-Item -LiteralPath $LiteralPath -Recurse -Force; return }
        catch {
            if ($attempt -eq $Attempts) { throw }
            Start-Sleep -Milliseconds 500
        }
    }
}

New-Item -ItemType Directory -Path (Join-Path $repoRoot '.tools') -Force | Out-Null
$buildLock = [IO.File]::Open((Join-Path $repoRoot '.tools\portable-build.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    if (-not [string]::IsNullOrWhiteSpace($DotnetPath)) {
        $dotnet = [IO.Path]::GetFullPath($DotnetPath)
        if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { throw "dotnet executable does not exist: $dotnet" }
    }
    else {
        $dotnet = Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
        if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
    }
    $env:DOTNET_CLI_HOME = Join-Path $repoRoot '.tools\cli-home'
    $env:NUGET_PACKAGES = Join-Path $repoRoot '.tools\nuget-packages'
    New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

    & $dotnet restore (Join-Path $repoRoot 'DingLater.slnx') --locked-mode --nologo
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }
    & $dotnet test (Join-Path $repoRoot 'DingLater.slnx') -c $Configuration --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'dotnet test failed.' }
    & $dotnet publish (Join-Path $repoRoot 'src\DingLater.App\DingLater.App.csproj') `
        -c $Configuration -r win-x64 --self-contained true --no-restore -o $publishRoot `
        -p:Version=$Version -p:PublishSingleFile=false -p:DebugSymbols=false -p:DebugType=None
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

    foreach ($relativePath in @('DingLater.pri', 'App.xbf', 'Views\MainWindow.xbf', 'Views\ShellPage.xbf', 'Views\InboxPage.xbf', 'Views\SettingsPage.xbf', 'Views\OnboardingPage.xbf', 'Styles\Typography.Small.xbf', 'Styles\Typography.Standard.xbf', 'Styles\Typography.Large.xbf', 'Styles\Typography.ExtraLarge.xbf')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishRoot $relativePath) -PathType Leaf)) { throw "Published WinUI resource is missing: $relativePath" }
    }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $publishRoot
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Verify-Portable.ps1') -Destination $publishRoot
    $docsRoot = Join-Path $publishRoot 'docs'
    New-Item -ItemType Directory -Path $docsRoot -Force | Out-Null
    foreach ($document in @('PRIVACY.md', 'COMPATIBILITY.md', 'TESTING.md', 'DISTRIBUTION.md')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot "docs\$document") -Destination $docsRoot
    }
    $releaseNotes = Join-Path $repoRoot "docs\releases\v$Version.md"
    if (Test-Path -LiteralPath $releaseNotes -PathType Leaf) {
        $releaseDocs = Join-Path $docsRoot 'releases'
        New-Item -ItemType Directory -Path $releaseDocs -Force | Out-Null
        Copy-Item -LiteralPath $releaseNotes -Destination $releaseDocs
    }
    $assetRoot = Join-Path $docsRoot 'assets'
    New-Item -ItemType Directory -Path $assetRoot -Force | Out-Null
    foreach ($file in @('dinglater-inbox.png', 'dinglater-snoozed.png', 'dinglater-settings.png')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot "docs\assets\$file") -Destination $assetRoot
    }

    $files = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path = $_.FullName.Substring($publishRoot.Length + 1).Replace('\', '/')
            size = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
    [ordered]@{ schema = 1; version = $Version; files = $files } | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $publishRoot 'package-files.json') -Encoding utf8
    Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $temporaryZip -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $temporaryZip -Algorithm SHA256).Hash.ToLowerInvariant()
    & (Join-Path $PSScriptRoot 'Test-PortablePackage.ps1') -PackagePath $temporaryZip -ExpectedSha256 $hash -ExpectedVersion $Version -TestValidationFailures
    & (Join-Path $PSScriptRoot 'Test-UpdateHandoff.ps1') -PackagePath $temporaryZip -ExpectedVersion $Version
    Copy-Item -LiteralPath $temporaryZip -Destination $publishRoot
    "$hash *$zipName" | Set-Content -LiteralPath (Join-Path $publishRoot 'SHA256SUMS.txt') -Encoding ascii

    # Keep the last working build until the new ZIP passes extraction and launch checks.
    if (Test-Path -LiteralPath $outputRoot) { Move-Item -LiteralPath $outputRoot -Destination $backupRoot }
    try { Move-Item -LiteralPath $publishRoot -Destination $outputRoot }
    catch {
        if (Test-Path -LiteralPath $backupRoot) { Move-Item -LiteralPath $backupRoot -Destination $outputRoot }
        throw
    }
    if (Test-Path -LiteralPath $backupRoot) {
        try { Remove-DirectoryWithRetry -LiteralPath $backupRoot }
        catch { Write-Warning "The previous build is still in use and was preserved at $backupRoot" }
    }
    Write-Host "Latest test app: $(Join-Path $outputRoot 'DingLater.exe')"
    Write-Host "Portable package: $(Join-Path $outputRoot $zipName)"
    Write-Host "SHA-256: $hash"
}
finally {
    try {
        if (Test-Path -LiteralPath $artifactRoot) { Remove-DirectoryWithRetry -LiteralPath $artifactRoot }
    }
    finally { $buildLock.Dispose() }
}
