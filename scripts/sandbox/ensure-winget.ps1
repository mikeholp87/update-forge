# Runs INSIDE Windows Sandbox. Checks for WinGet and installs it from Microsoft's official download links if missing.
$ErrorActionPreference = 'Stop'
$res = 'C:\Results'
New-Item -ItemType Directory -Force $res | Out-Null
'started' | Set-Content (Join-Path $res 'started.txt')
$log = Join-Path $res 'winget-setup.log'
function Log($m) { "$((Get-Date).ToString('s')) $m" | Tee-Object -FilePath $log -Append }

function Find-Winget {
    $c = Get-Command winget.exe -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    $p = Get-ChildItem "$env:ProgramFiles\WindowsApps" -Filter 'Microsoft.DesktopAppInstaller_*' -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'winget.exe' } | Where-Object { Test-Path $_ } | Select-Object -First 1
    return $p
}

try {
    $w = Find-Winget
    if ($w) { Log "WinGet already present: $w" }
    else {
        Log 'WinGet not found; installing (official Microsoft sources).'
        $ProgressPreference = 'SilentlyContinue'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $tmp = Join-Path $env:TEMP 'wg'; New-Item -ItemType Directory -Force $tmp | Out-Null
        # Microsoft publishes WinGet's exact dependency set (VCLibs, UI.Xaml, WindowsAppRuntime) as a zip in the winget-cli release.
        Log 'Downloading DesktopAppInstaller_Dependencies.zip'
        Invoke-WebRequest -Uri 'https://github.com/microsoft/winget-cli/releases/latest/download/DesktopAppInstaller_Dependencies.zip' -OutFile (Join-Path $tmp 'deps.zip') -UseBasicParsing
        Expand-Archive (Join-Path $tmp 'deps.zip') -DestinationPath (Join-Path $tmp 'deps') -Force
        Log 'Downloading winget.msixbundle'
        Invoke-WebRequest -Uri 'https://aka.ms/getwinget' -OutFile (Join-Path $tmp 'winget.msixbundle') -UseBasicParsing
        Get-ChildItem (Join-Path $tmp 'deps') -Recurse -Filter *.appx | Where-Object { $_.FullName -match '\\x64\\' } | ForEach-Object {
            Log "Installing dependency $($_.Name)"; Add-AppxPackage -Path $_.FullName
        }
        Log 'Installing WinGet (App Installer)'
        Add-AppxPackage -Path (Join-Path $tmp 'winget.msixbundle')
        Start-Sleep 5
        $w = Find-Winget
    }
    if (-not $w) { throw 'winget.exe not found after install' }
    $v = & $w --version
    Log "RESULT: PASS winget version $v at $w"
    & $w source list 2>&1 | Out-File "$res\winget-sources.txt"
}
catch { Log "RESULT: FAIL $($_.Exception.Message)" }
Log 'DONE'
