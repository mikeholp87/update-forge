# Host-side: stages the MSI, fills the .wsb template and starts Windows Sandbox. Requires the Sandbox feature.
param([string]$Script = 'run-msi-test.ps1')
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not (Test-Path "$env:windir\System32\WindowsSandbox.exe")) { throw 'Windows Sandbox is not enabled.' }
$msi = Get-ChildItem "$root\src\UpdateForge.Installer\bin" -Recurse -Filter *.msi | Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $msi) { throw 'Build the MSI first.' }
if (Get-Process WindowsSandbox* -ErrorAction SilentlyContinue) { throw 'A Windows Sandbox is already running. Close its window first (only one can run at a time).' }
$input = Join-Path $root 'artifacts\sandbox\input'; $results = Join-Path $root 'artifacts\sandbox\results'
New-Item -ItemType Directory -Force $input, $results | Out-Null
Remove-Item "$input\*", "$results\*" -Force -ErrorAction SilentlyContinue
Copy-Item $msi.FullName $input
$wsb = Join-Path $root 'artifacts\sandbox\test.wsb'
(Get-Content "$PSScriptRoot\UpdateForge-msi-test.wsb.template" -Raw).Replace('__INPUT__', $input).Replace('__RESULTS__', $results).Replace('__SCRIPTS__', $PSScriptRoot).Replace('__SCRIPT__', $Script) | Set-Content $wsb
Start-Process $wsb
Write-Host "Sandbox started. Results will appear in $results\msi-test.log"
