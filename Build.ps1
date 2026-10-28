#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$DotnetPath = 'dotnet',
    [switch]$Portable,
    [switch]$Android,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
Push-Location -LiteralPath $PSScriptRoot
try {
    & $DotnetPath restore AudioLink.sln
    if ($LASTEXITCODE -ne 0) { throw 'NuGet restore failed.' }
    & $DotnetPath build AudioLink.sln -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (!$SkipTests) {
        & $DotnetPath run --project tests/AudioLink.Tests -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw 'DSP tests failed.' }
    }
    if ($Android) { & (Join-Path $PSScriptRoot 'Build-Android.ps1') }
    $publishArgs = @('publish', 'src/AudioLink.App', '-c', 'Release', '-r', 'win-x64', '-o', 'artifacts/AudioLink', '-p:DebugType=None', '-p:DebugSymbols=false')
    if ($Portable) {
        $publishArgs += @('--self-contained', 'true', '-p:PublishSingleFile=false')
    } else {
        $publishArgs += @('--self-contained', 'false')
    }
    & $DotnetPath @publishArgs
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath 'README.md', 'VERIFICATION.md', 'UI-REVIEW.md', '人工验收.md', 'THIRD-PARTY-NOTICES.md', 'LICENSE' -Destination 'artifacts/AudioLink'
    if (Test-Path -LiteralPath 'artifacts/AudioLink-Android.apk') {
        New-Item -ItemType Directory -Path 'artifacts/AudioLink/Android' -Force | Out-Null
        Copy-Item -LiteralPath 'artifacts/AudioLink-Android.apk' -Destination 'artifacts/AudioLink/Android/AudioLink-Android.apk'
        Copy-Item -LiteralPath 'android/README.md' -Destination 'artifacts/AudioLink/Android/README.md'
    }
    if ($Portable) {
        $sdkRoot = Split-Path -Parent (Get-Command $DotnetPath).Source
        $runtimeLicense = Join-Path $sdkRoot 'LICENSE.txt'
        $runtimeNotices = Join-Path $sdkRoot 'ThirdPartyNotices.txt'
        if (Test-Path -LiteralPath $runtimeLicense) { Copy-Item -LiteralPath $runtimeLicense -Destination 'artifacts/AudioLink/DOTNET-LICENSE.txt' }
        if (Test-Path -LiteralPath $runtimeNotices) { Copy-Item -LiteralPath $runtimeNotices -Destination 'artifacts/AudioLink/DOTNET-THIRD-PARTY-NOTICES.txt' }
    }
    Compress-Archive -Path 'artifacts/AudioLink/*' -DestinationPath 'artifacts/AudioLink-Windows-x64.zip' -Force
    Write-Output "Ready: $PSScriptRoot\artifacts\AudioLink\AudioLink.exe"
} finally {
    Pop-Location
}
