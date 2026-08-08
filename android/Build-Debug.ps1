[CmdletBinding()]
param(
    [switch]$RunDeviceTests
)

$ErrorActionPreference = "Stop"
$androidRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repositoryRoot = Split-Path -Parent $androidRoot
$outputRoot = Join-Path $repositoryRoot "BuildOutput"
$sourceApk = Join-Path $androidRoot "app\build\outputs\apk\debug\app-debug.apk"
$targetName = "DingLater-android-0.1.0-debug.apk"
$targetApk = Join-Path $outputRoot $targetName
$checksumPath = "$targetApk.sha256"

Push-Location $androidRoot
try {
    # AGP 9 host-test packaging can race processDebugResources when parallel execution is enabled.
    # Keep the release helper deterministic even if gradle.properties enables parallel builds.
    & .\gradlew.bat --no-parallel --no-configuration-cache testDebugUnitTest lintDebug
    if ($LASTEXITCODE -ne 0) { throw "Android source validation failed with exit code $LASTEXITCODE" }

    if ($RunDeviceTests) {
        & .\gradlew.bat --no-parallel --no-configuration-cache connectedDebugAndroidTest
        if ($LASTEXITCODE -ne 0) { throw "Android device tests failed with exit code $LASTEXITCODE" }
    }

    & .\gradlew.bat --no-parallel --no-configuration-cache assembleDebug
    if ($LASTEXITCODE -ne 0) { throw "Android APK build failed with exit code $LASTEXITCODE" }
} finally {
    Pop-Location
}

if (-not (Test-Path -LiteralPath $sourceApk)) { throw "Debug APK was not produced: $sourceApk" }
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
Copy-Item -LiteralPath $sourceApk -Destination $targetApk -Force
$hash = (Get-FileHash -LiteralPath $targetApk -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $checksumPath -Value "$hash *$targetName" -Encoding ascii

Write-Output "APK: $targetApk"
Write-Output "SHA-256: $hash"
Write-Output "Checksum: $checksumPath"
