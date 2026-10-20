<#
.SYNOPSIS
    Builds a BIMDesigner release: tests it, publishes it self-contained for 64-bit Windows,
    signs it, and wraps it in a signed MSI installer.

.DESCRIPTION
    Windows' Smart App Control and SmartScreen judge a program by who signed it. An unsigned
    build is new code from nobody, and on a machine with Smart App Control on it will not run.
    So a release is signed - the program, its own libraries and the installer - with a trusted
    code-signing certificate, by signtool from the Windows SDK, timestamped so the signature
    outlives the certificate.

    The certificate is given by its thumbprint, if it is in the certificate store (as a
    hardware token's is), or as a .pfx file and its password - on the command line or in the
    environment variables BIMDESIGNER_SIGN_THUMBPRINT, BIMDESIGNER_SIGN_PFX and
    BIMDESIGNER_SIGN_PASSWORD. Without one, everything is built and left unsigned, and the
    script says so: fine to try out, not to hand to anyone.

.EXAMPLE
    ./build/release.ps1
    ./build/release.ps1 -Version 0.6.0 -CertificateThumbprint 0123456789ABCDEF...
#>
param(
    [string]$Version,
    [string]$CertificateThumbprint = $env:BIMDESIGNER_SIGN_THUMBPRINT,
    [string]$PfxPath = $env:BIMDESIGNER_SIGN_PFX,
    [string]$PfxPassword = $env:BIMDESIGNER_SIGN_PASSWORD,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'

if (-not $Version) {
    $project = [xml](Get-Content (Join-Path $root 'BIMDesigner.UI\BIMDesigner.UI.csproj'))
    $Version = ($project.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
}
Write-Host "BIMDesigner $Version" -ForegroundColor Cyan

function Invoke-Step([string]$Name, [scriptblock]$Step) {
    Write-Host "`n== $Name" -ForegroundColor Cyan
    & $Step
    if ($LASTEXITCODE -ne 0) { throw "$Name failed (exit code $LASTEXITCODE)." }
}

if (-not $SkipTests) {
    Invoke-Step 'Tests' { dotnet test (Join-Path $root 'BIMDesigner.Tests') -c Release }
}

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
Invoke-Step 'Publish' {
    dotnet publish (Join-Path $root 'BIMDesigner.UI') -c Release -r win-x64 --self-contained true `
        -p:Version=$Version -p:DebugType=none -o $publish
}

# ---- signing ---------------------------------------------------------------------------

function Find-SignTool {
    $onPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path $kits) {
        $found = Get-ChildItem $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Sort-Object { $_.Directory.Parent.Name } -Descending |
            Select-Object -First 1
        if ($found) { return $found.FullName }
    }
    return $null
}

$signTool = Find-SignTool
$canSign = ($CertificateThumbprint -or $PfxPath) -and $signTool

function Invoke-Sign([string[]]$Files) {
    if (-not $canSign) { return }
    $identity = if ($CertificateThumbprint) { @('/sha1', $CertificateThumbprint) } else { @('/f', $PfxPath, '/p', $PfxPassword) }
    Invoke-Step "Sign $($Files.Count) file(s)" {
        & $signTool sign /fd SHA256 /tr $TimestampUrl /td SHA256 /d 'BIMDesigner' @identity $Files
    }
}

# The program and its own libraries: what Smart App Control checks when it starts.
Invoke-Sign (Get-ChildItem $publish -File | Where-Object { $_.Name -like 'BIMDesigner*.exe' -or $_.Name -like 'BIMDesigner*.dll' } | ForEach-Object FullName)

Invoke-Step 'Installer' {
    dotnet build (Join-Path $root 'installer\BIMDesigner.Installer.wixproj') -c Release `
        -p:ProductVersion=$Version -p:PublishDir="$publish\" -o $artifacts
}

$msi = Join-Path $artifacts "BIMDesigner-$Version-x64.msi"
Invoke-Sign @($msi)

Write-Host "`n== Done" -ForegroundColor Cyan
Write-Host "Installer: $msi"
if ($canSign) {
    Write-Host 'Signed: the program, its libraries and the installer.' -ForegroundColor Green
}
elseif (-not $signTool) {
    Write-Warning 'UNSIGNED: signtool was not found. Install the Windows SDK (its Signing Tools) and run this again with a certificate.'
}
else {
    Write-Warning 'UNSIGNED: no certificate was given. Smart App Control will block this build; give -CertificateThumbprint or -PfxPath to sign a release.'
}
