# Reverses install.ps1: puts MI_01 back on the HID mouse stack and removes what we added.
# Run elevated.
#
# After this, the scroll wheel works as an ordinary mouse wheel again and the three state
# LEDs go back to being unreachable.

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

$ErrorActionPreference = 'Continue'

$hwid    = 'USB\VID_050D&PID_0815&MI_01'
$subject = 'CN=n52 DejaVu Driver Signing'

# Every published copy of our package, in the order pnputil lists them.
#
# Parsed as blocks rather than with one regex over the whole output: pnputil prints one
# record per package separated by blank lines, and a lazy match spanning the lot will
# happily pair one package's Published Name with a different package's Original Name.
function Get-N52DriverPackage {
    $text = (pnputil /enum-drivers | Out-String) -replace "`r`n", "`n"
    foreach ($block in ($text -split "`n`n")) {
        if ($block -notmatch '(?im)^\s*Original Name:\s*n52winusb\.inf\s*$') { continue }
        if ($block -match '(?im)^\s*Published Name:\s*(oem\d+\.inf)\s*$') { $Matches[1] }
    }
}

Write-Host "1. removing our driver package"
# Every copy of it, not the first one found. Each install used to publish a fresh oemNN.inf,
# so a machine set up more than once holds several - and removing one left the rest, which
# the pad could bind straight back to instead of returning to the HID stack. This script
# then reported success while having reversed nothing.
$packages = @(Get-N52DriverPackage)
if ($packages.Count -eq 0) {
    Write-Host "   no n52winusb package found in the driver store"
} else {
    foreach ($oem in $packages) {
        Write-Host "   removing $oem"
        pnputil /delete-driver $oem /uninstall /force
    }
}

Write-Host "2. re-detecting the device"
# With our package gone, PnP falls back to input.inf / HidUsb, restoring the HID mouse.
pnputil /scan-devices | Out-Null
Start-Sleep -Seconds 3

$found = @(Get-PnpDevice -ErrorAction SilentlyContinue |
           Where-Object { $_.InstanceId -match [regex]::Escape($hwid) })

if ($found.Count -eq 0) {
    Write-Host "   no n52 attached, so there was nothing to hand back"
} else {
    $found | Select-Object Status, Class, FriendlyName | Format-Table -AutoSize
}

Write-Host "3. removing the signing certificate"
foreach ($storeName in 'My','Root','TrustedPublisher') {
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($storeName,'LocalMachine')
    $store.Open('ReadWrite')
    foreach ($c in @($store.Certificates | Where-Object { $_.Subject -eq $subject })) {
        $store.Remove($c)
        Write-Host "   removed from LocalMachine\$storeName"
    }
    $store.Close()
}

if ($found.Count -eq 0) {
    Write-Host "`nDone. Nothing of ours is left on this PC. Any n52 plugged in from now on"
    Write-Host "will be an ordinary keyboard and mouse."
} else {
    Write-Host "`nDone. If the wheel is still not working, unplug and replug the pad."
}
