# Runs INSIDE Windows Sandbox. End-to-end upgrade test: WinGet -> install UpdateForge MSI -> install an OLD version of a
# harmless package -> UpdateForge scan flags it -> UpdateForge updates it -> verify the version really changed.
$ErrorActionPreference = 'Continue'
$res = 'C:\Results'
New-Item -ItemType Directory -Force $res | Out-Null
'started' | Set-Content (Join-Path $res 'started.txt')
$log = Join-Path $res 'upgrade-test.log'
function Log($m) { "$((Get-Date).ToString('s')) $m" | Tee-Object -FilePath $log -Append }
function Check($name, $ok) { Log ("{0}: {1}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name) }
$pkg = 'jqlang.jq'

try {
    # 1. WinGet
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\Scripts\ensure-winget.ps1 | Out-Null
    $winget = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe'
    Check 'WinGet available' (Test-Path $winget)
    if (-not (Test-Path $winget)) { throw 'no winget' }

    # 2. UpdateForge MSI
    $msi = (Get-ChildItem C:\Input -Filter *.msi | Select-Object -First 1).FullName
    $p = Start-Process msiexec.exe -ArgumentList '/i', "`"$msi`"", '/qn' -Wait -PassThru
    $cli = "$env:LOCALAPPDATA\Programs\UpdateForge\updateforge-cli.exe"
    Check 'UpdateForge MSI installed' ($p.ExitCode -eq 0 -and (Test-Path $cli))

    # 3. Install an OLDER version of the test package (not the newest)
    $versions = & $winget show --id $pkg --exact --versions --accept-source-agreements 2>&1 |
        Where-Object { $_ -match '^\d+(\.\d+)+' } | ForEach-Object { $_.Trim() }
    Log "Published versions (newest first): $($versions -join ', ')"
    $newest = $versions | Select-Object -First 1
    $old = $versions | Select-Object -Skip 2 -First 1
    Check 'found an older version to install' ([bool]$old)
    Log "Installing $pkg $old (newest is $newest)"
    & $winget install --id $pkg --exact --version $old --silent --accept-package-agreements --accept-source-agreements 2>&1 | Out-File "$res\winget-install.txt"
    $before = (& $winget list --id $pkg --exact --accept-source-agreements 2>&1 | Out-String)
    Log "winget list BEFORE:`n$before"
    Check 'old version installed' ($before -match [regex]::Escape($old))

    # 4. UpdateForge sees it as outdated
    $scan = & $cli scan --json 2>$null | Out-String | ConvertFrom-Json
    $row = $scan | Where-Object { $_.PackageId -eq $pkg -and $_.Provider -eq 'winget' } | Select-Object -First 1
    $scan | ConvertTo-Json -Depth 4 | Out-File "$res\scan-before.json"
    Log "UpdateForge row BEFORE: Installed=$($row.Installed) Available=$($row.Available) Status=$($row.Status) Support=$($row.Support) Confidence=$($row.Confidence)"
    Check 'UpdateForge lists the package' ($null -ne $row)
    Check 'UpdateForge reports update available' ($row.Status -eq 'UpdateAvailable' -and $row.Available)
    Check 'UpdateForge reports installed = old version' ($row.Installed -eq $old)

    # 5. Update through UpdateForge
    $out = & $cli update --id $pkg --json 2>$null | Out-String
    $out | Out-File "$res\update-result.json"
    Log "UpdateForge update result:`n$out"
    $r = $out | ConvertFrom-Json | Select-Object -First 1
    Check 'UpdateForge reports Completed' ($r.State -eq 'Completed')

    # 6. Verify independently of UpdateForge
    $after = (& $winget list --id $pkg --exact --accept-source-agreements 2>&1 | Out-String)
    Log "winget list AFTER:`n$after"
    Check 'WinGet confirms version changed' (($after -notmatch [regex]::Escape($old)) -or ($after -match [regex]::Escape($newest)))
    $scan2 = & $cli scan --json 2>$null | Out-String | ConvertFrom-Json
    $row2 = $scan2 | Where-Object { $_.PackageId -eq $pkg -and $_.Provider -eq 'winget' } | Select-Object -First 1
    Log "UpdateForge row AFTER: Installed=$($row2.Installed) Status=$($row2.Status)"
    Check 'UpdateForge now reports up to date' ($row2.Status -eq 'UpToDate')

    # 7. History recorded
    $hist = & $cli history --json 2>$null | Out-String
    $hist | Out-File "$res\history.json"
    Check 'history recorded the update' ($hist -match $pkg)
}
catch { Log "ERROR: $($_.Exception.Message)" }
Log 'DONE'
