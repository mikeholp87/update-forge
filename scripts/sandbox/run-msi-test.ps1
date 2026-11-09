# Runs INSIDE Windows Sandbox. Installs the MSI per-user, exercises the CLI, scheduled task and uninstall,
# and writes results to the mapped results folder.
$ErrorActionPreference = 'Continue'
$res = 'C:\Results'
New-Item -ItemType Directory -Force $res | Out-Null
'started' | Set-Content (Join-Path $res 'started.txt')
$log = Join-Path $res 'msi-test.log'
function Log($m) { "$((Get-Date).ToString('s')) $m" | Tee-Object -FilePath $log -Append }
function Check($name, $ok) { Log ("{0}: {1}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name) }

$msi = (Get-ChildItem C:\Input -Filter *.msi | Select-Object -First 1).FullName
$dir = "$env:LOCALAPPDATA\Programs\UpdateForge"
$cli = Join-Path $dir 'updateforge-cli.exe'

Log "Installing $msi"
$p = Start-Process msiexec.exe -ArgumentList '/i', "`"$msi`"", '/qn', '/l*v', "$res\install.log" -Wait -PassThru
Check 'MSI install exit 0' ($p.ExitCode -eq 0)
Check 'GUI exe installed' (Test-Path (Join-Path $dir 'UpdateForge.exe'))
Check 'CLI exe installed' (Test-Path $cli)
Check 'Start menu shortcut' (Test-Path "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\UpdateForge\UpdateForge.lnk")

& $cli doctor *>&1 | Tee-Object -FilePath $log -Append
Check 'doctor exit 0 (winget may be absent in Sandbox)' ($LASTEXITCODE -eq 0)
& $cli scan *>&1 | Out-File "$res\scan.txt"
Check 'scan ran' ($LASTEXITCODE -in 0, 10)

& $cli schedule apply *>&1 | Tee-Object -FilePath $log -Append
Check 'scheduled task registered' ($null -ne (Get-ScheduledTask -TaskPath '\UpdateForge\' -ErrorAction SilentlyContinue))
$t = Get-ScheduledTask -TaskPath '\UpdateForge\' -ErrorAction SilentlyContinue
if ($t) { Check 'task is not elevated / not SYSTEM' ($t.Principal.RunLevel -eq 'Limited' -and $t.Principal.UserId -ne 'SYSTEM') }

$g = Start-Process (Join-Path $dir 'UpdateForge.exe') -PassThru; Start-Sleep 15
Check 'GUI starts and stays alive' (-not $g.HasExited)
Stop-Process $g -Force -ErrorAction SilentlyContinue

Log 'Uninstalling'
$u = Start-Process msiexec.exe -ArgumentList '/x', "`"$msi`"", '/qn', '/l*v', "$res\uninstall.log" -Wait -PassThru
Check 'MSI uninstall exit 0' ($u.ExitCode -eq 0)
Check 'install folder removed' (-not (Test-Path (Join-Path $dir 'UpdateForge.exe')))
Check 'scheduled task removed' ($null -eq (Get-ScheduledTask -TaskPath '\UpdateForge\' -ErrorAction SilentlyContinue))
Check 'user data preserved' (Test-Path "$env:LOCALAPPDATA\UpdateForge")
Log 'DONE'
