<#
.SYNOPSIS
  Installs a locally built Clocktail bundle (service + MCP server + lab) over the current installation.

.DESCRIPTION
  Use this instead of `npx clocktail setup` when you built Clocktail yourself: npx installs the published release.
  The broker (service), the MCP server and the lab must always be updated together. Windows asks for admin rights once,
  for the service. Everything it replaces is backed up first; -Rollback puts it back.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build\install-local.ps1 -Plan       # show what would happen, change nothing
  powershell -ExecutionPolicy Bypass -File build\install-local.ps1             # install
  powershell -ExecutionPolicy Bypass -File build\install-local.ps1 -Rollback "$env:LOCALAPPDATA\Clocktail\install-backup-<time>"
#>
[CmdletBinding()]
param(
    [string]$Source,                                                            # default: ..\out\game-test beside this script
    [string]$ClientDir  = (Join-Path $env:LOCALAPPDATA 'Programs\Clocktail'),   # what your agents run
    [string]$ServiceDir = (Join-Path $env:ProgramFiles 'Clocktail'),            # what the elevated service runs
    [string]$BackupRoot = (Join-Path $env:LOCALAPPDATA 'Clocktail'),
    [string]$Rollback,
    [switch]$Plan,
    [switch]$Yes,
    [switch]$NoService   # agent-side files only (used by tests)
)
$ErrorActionPreference = 'Stop'

# $PSScriptRoot is empty inside parameter defaults on some hosts, so the default is worked out here.
if (-not $Source) {
    $here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $Source = Join-Path $here '..\out\game-test'
}

# Windows PowerShell started from inside a PowerShell 7 window inherits 7's module path, which hides its own
# Microsoft.PowerShell.Utility (Get-FileHash and friends vanish). Put the system paths back before anything is looked up.
if ($PSVersionTable.PSEdition -eq 'Desktop' -and $env:PSModulePath -match '\\PowerShell\\7\\') {
    $env:PSModulePath = (Join-Path $env:USERPROFILE 'Documents\WindowsPowerShell\Modules') + ';' +
        (Join-Path $env:ProgramFiles 'WindowsPowerShell\Modules') + ';' + (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\Modules')
}

# The release lays all three out in the agent folder, and the MCP server's install_service runs the Clocktail.Broker.exe beside it:
# a stale copy there would put the OLD service back the next time an agent updates it.
$ClientFiles = 'clocktail-mcp.exe', 'clocktail-lab.exe', 'Clocktail.Broker.exe'

function Say([string]$text) { Write-Host $text }
function Hash([string]$path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($sha.ComputeHash([System.IO.File]::ReadAllBytes($path))) } finally { $sha.Dispose() }
}

# A running exe cannot be overwritten but it can be renamed: the old copy moves aside and keeps running until its agent restarts.
function Put-File([string]$from, [string]$dir, [switch]$Preview) {
    $leaf = Split-Path $from -Leaf
    $to = Join-Path $dir $leaf
    if ((Test-Path -LiteralPath $to) -and ((Hash $to) -eq (Hash $from))) { Say "  unchanged   $to"; return }
    if ($Preview) { Say "  would put   $to"; return }
    if (Test-Path -LiteralPath $to) {
        Rename-Item -LiteralPath $to -NewName ($leaf + '.old-' + [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())
    }
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Copy-Item -LiteralPath $from -Destination $to
    Say "  installed   $to"
}

function Running([string]$name, [string]$under) {
    @(Get-Process -Name $name -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and $_.Path.StartsWith($under, [StringComparison]::OrdinalIgnoreCase) } catch { $false } })
}

function Wait-NewService([string]$broker) {
    for ($i = 0; $i -lt 45; $i++) {
        Start-Sleep -Seconds 1
        try {
            $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
            $text = (& $broker status 2>$null | Out-String)
            $ErrorActionPreference = $old
            # Only a service that knows guarded game sessions reports game_protocol: the old one does not.
            if ($text -match '"reachable":\s*true' -and $text -match '"game_protocol":\s*(\d+)') {
                $protocol = $Matches[1]
                $id = if ($text -match '"pid":\s*(\d+)') { $Matches[1] } else { '?' }
                $gpu = if ($text -match '"gpus":\s*\[\s*"([^"]+)"') { $Matches[1] } else { '?' }
                return [pscustomobject]@{ Pid = $id; Protocol = $protocol; Gpu = $gpu }
            }
        } catch { $ErrorActionPreference = 'Stop' }
    }
    return $null
}

# ---- rollback ----
if ($Rollback) {
    $b = (Resolve-Path -LiteralPath $Rollback).Path
    Say "Rolling back from $b"
    foreach ($f in $ClientFiles) { if (Test-Path -LiteralPath (Join-Path $b "client\$f")) { Put-File (Join-Path $b "client\$f") $ClientDir } }
    if (-not $NoService -and (Test-Path -LiteralPath (Join-Path $b 'service\Clocktail.Broker.exe'))) {
        Say 'Reinstalling the previous service (Windows asks for admin rights once)...'
        & (Join-Path $b 'service\Clocktail.Broker.exe') install
        if ($LASTEXITCODE -ne 0) { throw "The previous service did not install (exit $LASTEXITCODE)." }
    }
    Say 'Rolled back. Restart your agent(s).'
    return
}

# ---- check the bundle before touching anything ----
$Source = (Resolve-Path -LiteralPath $Source).Path
foreach ($f in $ClientFiles) {
    $p = Join-Path $Source $f
    if (-not (Test-Path -LiteralPath $p)) { throw "$f is missing from $Source. Publish the whole bundle first (docs\game-test.md)." }
    if ((Get-Item -LiteralPath $p).Length -lt 10MB) { throw "$f in $Source looks incomplete." }
}
$agents = Running 'clocktail-mcp' $ClientDir
$jobs   = Running 'clocktail-lab' $ClientDir
$games  = @(Get-Process -Name 'q2rtx' -ErrorAction SilentlyContinue)

Say ''
Say "Source        : $Source"
Say "Agent files   : $ClientDir  (server, lab and the service installer beside them)"
Say $(if ($NoService) { 'Service       : not touched (-NoService)' } else { "Service       : $ServiceDir (the logon task is re-registered; one Windows admin prompt)" })
Say "Backup        : $BackupRoot\install-backup-<time>  (what is replaced, for -Rollback)"
if ($agents.Count -gt 0) { Say "Running agents: $($agents.Count) keep the old server until they restart (fine: the old file is moved aside)." }
Say 'A trial in force ends when the service restarts: the daily profile goes back on.'
Say ''
if ($jobs.Count -gt 0 -or $games.Count -gt 0) {
    throw 'A lab job or Quake II RTX is running. Stop it first (lab_stop, or close the game), then run this again. Nothing was changed.'
}
if ($Plan) {
    Say 'Plan only; nothing was changed:'
    foreach ($f in $ClientFiles) { Put-File (Join-Path $Source $f) $ClientDir -Preview }
    return
}
if (-not $Yes) {
    if ((Read-Host 'Type YES to install') -ne 'YES') { Say 'Cancelled. Nothing was changed.'; return }
}

# ---- backup, then install ----
$backup = Join-Path $BackupRoot ('install-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
if (Test-Path -LiteralPath $backup) { throw "The backup folder $backup already exists; an earlier backup is never overwritten. Nothing was changed." }
New-Item -ItemType Directory -Path (Join-Path $backup 'client'), (Join-Path $backup 'service') | Out-Null
foreach ($f in $ClientFiles) { if (Test-Path -LiteralPath (Join-Path $ClientDir $f)) { Copy-Item -LiteralPath (Join-Path $ClientDir $f) -Destination (Join-Path $backup 'client') } }
if (Test-Path -LiteralPath $ServiceDir) { Get-ChildItem -LiteralPath $ServiceDir -File | Copy-Item -Destination (Join-Path $backup 'service') }
Say "Backed up to $backup"

Say 'Agent files:'
foreach ($f in $ClientFiles) { Put-File (Join-Path $Source $f) $ClientDir }

if (-not $NoService) {
    $broker = Join-Path $Source 'Clocktail.Broker.exe'
    Say 'Service (Windows asks for admin rights once):'
    & $broker install
    if ($LASTEXITCODE -ne 0) {
        throw "The service did not install (exit $LASTEXITCODE). Roll back with: build\install-local.ps1 -Rollback `"$backup`""
    }
    $now = Wait-NewService $broker
    if ($null -eq $now) {
        Write-Warning "The new service did not answer within 45 s. Read $env:LOCALAPPDATA\Clocktail\broker.log, or roll back: build\install-local.ps1 -Rollback `"$backup`""
        exit 1
    }
    Say "  the new service is running (pid $($now.Pid), game protocol $($now.Protocol), GPU $($now.Gpu))"
}

Say ''
Say 'Done. Restart your agent(s) so they load the new server.'
Say ("To undo: build\install-local.ps1 -Rollback `"$backup`"" + $(if ($NoService) { '   (agent files only: the service was not touched)' } else { '' }))
