# Binds Microsoft's packaged WinUSB driver to the n52's MI_01 interface, so the three state
# LEDs can be driven from user mode. Run elevated.
#
# WHY THIS IS NEEDED: while MI_01 is bound as a system mouse collection, Windows refuses to
# carry a HID output report to it from user mode, so the LEDs are unreachable. WinUSB gives
# us direct USB access to the interface. N52UsbDevice.SetLeds carries the full reasoning,
# the SET_REPORT packet and the LED bit map.
#
# WHAT IT COSTS: MI_01 stops being a system mouse, so the scroll wheel only works while
# n52 DejaVu is running and reading it over WinUSB. MI_00 -- the 14 keys, the d-pad and both
# thumb inputs -- is a separate interface and is not touched.
#
# WHAT IT CHANGES: adds a driver package (winusb.sys itself is Microsoft-signed and stays
# untouched; only the small INF naming this device is ours), and trusts a self-signed
# certificate so Windows will accept that INF. Run uninstall.ps1 to reverse both.

# Elevates itself rather than demanding elevation.
#
# Deliberately not "#Requires -RunAsAdministrator". That is checked before any of this file
# runs, so a script carrying it cannot elevate itself - and Windows registers exactly one
# context-menu verb for .ps1, "Run with PowerShell", which is always unelevated. The pair
# meant the obvious gesture failed instantly with a #requires error and a window that shut
# before it could be read.
#
# Relaunches the host it is already running under, rather than hard-coding powershell.exe,
# so PowerShell 7 stays on 7. -NoExit keeps the new window open: this reports what it did,
# and an installer that flashes closed on success has told the user nothing.
$admin = ([Security.Principal.WindowsPrincipal] `
          [Security.Principal.WindowsIdentity]::GetCurrent()
         ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $admin) {
    Write-Host "Not running as administrator - asking to elevate."
    try {
        Start-Process (Get-Process -Id $PID).Path -Verb RunAs -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-NoExit', '-File', "`"$PSCommandPath`"")
    } catch {
        Write-Host "Elevation was refused. Open PowerShell as administrator and run this script there."
    }
    exit
}

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$inf  = Join-Path $here 'n52winusb.inf'
$cat  = Join-Path $here 'n52winusb.cat'
$subject = 'CN=n52 DejaVu Driver Signing'

if (-not (Test-Path $inf)) { throw "n52winusb.inf not found beside this script." }

function Get-N52DriverPackage {
    $text = (pnputil /enum-drivers | Out-String) -replace "`r`n", "`n"
    foreach ($block in ($text -split "`n`n")) {
        if ($block -notmatch '(?im)^\s*Original Name:\s*n52winusb\.inf\s*$') { continue }
        if ($block -match '(?im)^\s*Published Name:\s*(oem\d+\.inf)\s*$') { $Matches[1] }
    }
}

Write-Host "1. building catalog"

$staging = Join-Path ([IO.Path]::GetTempPath()) ("n52cat-" + [guid]::NewGuid().ToString('N'))
$staged  = "$staging.cat"
New-Item -ItemType Directory -Path $staging | Out-Null
try {
    Copy-Item -LiteralPath $inf -Destination $staging
    New-FileCatalog -Path $staging -CatalogFilePath $staged -CatalogVersion 2 | Out-Null
    Move-Item -LiteralPath $staged -Destination $cat -Force
} finally {
    Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue
    Remove-Item -Force $staged -ErrorAction SilentlyContinue
}
if ((Get-Item $cat).Length -eq 0) { throw "catalog came out empty." }

Write-Host "2. signing certificate"
$cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -eq $subject } | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject `
        -CertStoreLocation Cert:\LocalMachine\My -NotAfter (Get-Date).AddYears(3) `
        -KeyUsage DigitalSignature -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3')
    Write-Host "   created, thumbprint $($cert.Thumbprint)"
} else {
    Write-Host "   reusing existing, thumbprint $($cert.Thumbprint)"
}

foreach ($storeName in 'Root','TrustedPublisher') {
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($storeName,'LocalMachine')
    $store.Open('ReadWrite')
    if (-not ($store.Certificates | Where-Object Thumbprint -eq $cert.Thumbprint)) { $store.Add($cert) }
    $store.Close()
}

Write-Host "3. signing catalog"

$signed = Set-AuthenticodeSignature -FilePath $cat -Certificate $cert -HashAlgorithm SHA256
if ($signed.Status -ne 'Valid') { throw "catalog signature is not valid: $($signed.StatusMessage)" }

Write-Host "4. removing older copies of this package"

foreach ($oem in @(Get-N52DriverPackage)) {
    Write-Host "   removing $oem"
    pnputil /delete-driver $oem /uninstall /force | Out-Null
}

Write-Host "5. installing driver package"
pnputil /add-driver $inf /install

Write-Host "6. checking"

$remaining = @(Get-N52DriverPackage)
Write-Host "   driver packages in the store: $($remaining.Count)"
if ($remaining.Count -ne 1) { Write-Warning "expected exactly one package." }

$found = @(Get-PnpDevice -ErrorAction SilentlyContinue |
           Where-Object { $_.InstanceId -like 'USB\VID_050D&PID_0815&MI_0*' })

foreach ($d in $found) {
    Write-Host "   $($d.InstanceId) -> $($d.Status) / $($d.Service)"
    if ($d.Service -ne 'WINUSB') { Write-Warning "$($d.InstanceId) is not on WinUSB." }
}

if ($found.Count -eq 0) {
    Write-Host ""
    Write-Warning "No n52 was found on this PC, so nothing was handed over."
    Write-Host "The driver package is installed and waiting. Plug the pad in and run this again."
    Write-Host "Certificate thumbprint: $($cert.Thumbprint)"
    exit
}

Write-Host "`nDone. The scroll wheel is now dead until n52 DejaVu reads it over WinUSB."
Write-Host "Certificate thumbprint: $($cert.Thumbprint)"
