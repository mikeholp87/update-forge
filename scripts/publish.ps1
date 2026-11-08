<#
.SYNOPSIS
  Publishes UpdateForge (GUI + CLI) self-contained into artifacts/publish/<rid> and creates the portable ZIP.
.EXAMPLE
  ./scripts/publish.ps1 -Version 0.1.0
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [string]$Version = '0.1.0'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$out = Join-Path $root "artifacts/publish/$Runtime"
$portable = Join-Path $root "artifacts/UpdateForge-$Version-$Runtime-portable.zip"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force (Split-Path $portable) | Out-Null

# Both executables go to the same folder so the scheduled task can find updateforge-cli.exe next to UpdateForge.exe.
foreach ($proj in 'src/UpdateForge.Desktop', 'src/UpdateForge.Cli') {
    dotnet publish $proj -c $Configuration -r $Runtime --self-contained true -o $out "-p:Version=$Version" "-p:DebugType=none" "-p:DebugSymbols=false"
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $proj" }
}

Copy-Item (Join-Path $root 'LICENSE') $out
@"
UpdateForge $Version (portable)
This folder runs without installation. Portable mode limitations:
 - The scheduled-task path is tied to this folder; if you move it, re-save Settings to refresh the task.
 - No Start menu entry, no automatic upgrade through the MSI.
 - Settings and history are stored in %LOCALAPPDATA%\UpdateForge.
"@ | Set-Content (Join-Path $out 'PORTABLE-README.txt')

if (Test-Path $portable) { Remove-Item $portable -Force }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $portable
$hash = (Get-FileHash $portable -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path -Leaf $portable)" | Set-Content "$portable.sha256"

$sizeMb = [math]::Round((Get-ChildItem $out -Recurse | Measure-Object Length -Sum).Sum / 1MB, 1)
$zipMb = [math]::Round((Get-Item $portable).Length / 1MB, 1)
Write-Host "Published to $out ($sizeMb MB), portable ZIP $zipMb MB, SHA-256 $hash"
