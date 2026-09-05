param(
    [Parameter(Mandatory=$true)][int]$ParentPid,
    [int]$WrapperPid = 0,
    [Parameter(Mandatory=$true)][string]$Root
)

$ErrorActionPreference = "SilentlyContinue"
$clean = Join-Path $Root "WACVR_SESSION_CLEAN.flag"
$restore = Join-Path $Root ".__WACVR_V020_RESTORE.ps1"

function Is-Alive([int]$PidValue) {
    if ($PidValue -le 0) { return $true }
    try {
        $p = Get-Process -Id $PidValue -ErrorAction Stop
        return (-not $p.HasExited)
    } catch { return $false }
}

while ($true) {
    if (-not (Is-Alive $ParentPid)) { break }
    if ($WrapperPid -gt 0 -and -not (Is-Alive $WrapperPid)) {
        try { Stop-Process -Id $ParentPid -Force -ErrorAction SilentlyContinue } catch {}
        break
    }
    Start-Sleep -Milliseconds 500
}

if (Test-Path -LiteralPath $clean) { exit 0 }

# Emergency cleanup if wrapper/runtime died unexpectedly.
foreach ($name in @("Mercury-Win64-Shipping", "amdaemon", "WACVR_Core")) {
    Get-Process -Name $name -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
}

if (Test-Path -LiteralPath $restore) {
    powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $restore -Root $Root
}
