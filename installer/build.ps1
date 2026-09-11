# Builds the installer.
#
#   .\installer\build.ps1
#
# Output, in dist\:
#   n52dejavu-1.0.msi          for deployment - Group Policy, Intune, msiexec /qn
#   n52dejavu-1.0-setup.exe    for people - fetches the .NET runtime first if it is missing
#
# The WiX authoring deliberately knows how to build nothing. It packages what is put in
# front of it, so what ships is exactly what was tested.

$ErrorActionPreference = 'Stop'

$root   = Split-Path -Parent $PSScriptRoot
$dist   = Join-Path $root 'dist'
$app    = Join-Path $dist 'app'
$prereq = Join-Path $dist 'prereq'

# The runtime the bundle chains. Change both together: the version pins the download, and
# bundle.wxs pins the folder it looks for to decide whether it is needed.
$runtimeVersion = '10.0.11'
$runtimeUrl = "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/$runtimeVersion/windowsdesktop-runtime-$runtimeVersion-win-x64.exe"

$wix = @(
    "$env:USERPROFILE\.dotnet\tools\wix.exe"
    (Get-Command wix -ErrorAction SilentlyContinue).Source
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if (-not $wix) {
    throw "WiX is not installed.  dotnet tool install --global wix --version 5.*"
}

# A running copy holds its own assemblies open, and the failure that causes is a wall of
# MSB3027 retry noise that says nothing about the actual problem.
if (Get-Process n52dejavu -ErrorAction SilentlyContinue) {
    throw 'n52 DejaVu is running. Quit it from the notification area first.'
}

New-Item $dist -ItemType Directory -Force | Out-Null

# ---- 1. The application ---------------------------------------------------------------
#
# Framework-dependent, so this is six files rather than four hundred. Cleared first because
# publish does not remove what it no longer produces, and a renamed assembly left behind
# would be packaged and shipped.
if (Test-Path $app) { Remove-Item $app -Recurse -Force }

Write-Host 'Publishing...' -ForegroundColor Cyan

# No symbols in the payload. They are only useful against a build you have the source for,
# and they carry the absolute paths of the machine that built them.
dotnet publish (Join-Path $root 'src\DejaVu.App') -c Release -o $app --nologo `
    -p:DebugType=none -p:DebugSymbols=false | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

# The host needs all of these. Without the runtimeconfig it assumes a self-contained build
# and dies looking for hostpolicy.dll, which is a confusing way to learn that a file is
# missing - so check here instead.
$required = 'n52dejavu.exe', 'n52dejavu.dll', 'DejaVu.Core.dll',
            'n52dejavu.runtimeconfig.json', 'n52dejavu.deps.json', 'help.md'

$missing = $required | Where-Object { -not (Test-Path (Join-Path $app $_)) }
if ($missing) { throw "publish did not produce: $($missing -join ', ')" }

$files = Get-ChildItem $app -Recurse -File
Write-Host ("  {0} files, {1:N0} KB" -f $files.Count, (($files | Measure-Object Length -Sum).Sum / 1KB))

# ---- 2. The license, as RTF -------------------------------------------------------------
#
# The MSI license page takes RTF and nothing else. Generated rather than kept as a second
# copy, so LICENSE stays the only place the terms are written down.
$body = (Get-Content (Join-Path $root 'LICENSE') -Raw).Replace('\', '\\').Replace('{', '\{').Replace('}', '\}')
$body = ($body -split "`r?`n") -join '\par' + "`r`n"

"{\rtf1\ansi\deff0{\fonttbl{\f0\fnil\fcharset0 Consolas;}}`r`n\fs18 $body}" |
    Set-Content (Join-Path $dist 'LICENSE.rtf') -Encoding ASCII

# ---- 3. The runtime, for the bundle's payload -------------------------------------------
#
# Fetched so WiX computes the hash and size from the real file. Kept between builds because
# it is 57 MB and does not change unless the pinned version does.
New-Item $prereq -ItemType Directory -Force | Out-Null
$runtime = Join-Path $prereq 'windowsdesktop-runtime-win-x64.exe'

if (-not (Test-Path $runtime)) {
    Write-Host "Fetching the .NET $runtimeVersion Desktop Runtime..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $runtimeUrl -OutFile $runtime -TimeoutSec 600
}

# ---- 4. WiX -----------------------------------------------------------------------------
#
# From the installer folder: WiX resolves the paths in the authoring against the working
# directory, not against the file they are written in.
Push-Location $PSScriptRoot
try {
    Write-Host 'Building the MSI...' -ForegroundColor Cyan
    & $wix build -arch x64 `
        -ext WixToolset.Util.wixext -ext WixToolset.UI.wixext `
        -out (Join-Path $dist 'n52dejavu-1.0.msi') 'n52dejavu.wxs'
    if ($LASTEXITCODE -ne 0) { throw 'MSI build failed' }

    Write-Host 'Building the bootstrapper...' -ForegroundColor Cyan
    & $wix build -arch x64 `
        -ext WixToolset.Util.wixext -ext WixToolset.BootstrapperApplications.wixext `
        -out (Join-Path $dist 'n52dejavu-1.0-setup.exe') 'bundle.wxs'
    if ($LASTEXITCODE -ne 0) { throw 'bootstrapper build failed' }
}
finally {
    Pop-Location
}

Write-Host ''
Get-ChildItem $dist -File | Where-Object { $_.Extension -in '.msi', '.exe' } |
    ForEach-Object { Write-Host ("  {0,-28} {1,7:N2} MB" -f $_.Name, ($_.Length / 1MB)) -ForegroundColor Green }
