$ErrorActionPreference = "Stop"

# ==============================================================
# WACVR CUSTOM V0.2.0 - INTEGRATED WACCA SESSION MANAGER
# ==============================================================
# Preserves validated V1.4 console-host/display chain.
# No WACCA window resize / ExactClientFit.
# OpenXR runtime-aware: SteamVR is only auto-launched when the
# ACTIVE Windows OpenXR runtime points to SteamVR.
# ESC physical = clean quit of the whole session.
# ==============================================================

$Root = $env:WACVR_INTEGRATED_ROOT
$WacvrDir = $env:WACVR_INTEGRATED_DIR
if ([string]::IsNullOrWhiteSpace($Root)) { $Root = Split-Path -Parent $MyInvocation.MyCommand.Path }
if ([string]::IsNullOrWhiteSpace($WacvrDir)) { $WacvrDir = Join-Path $Root "WACVR" }

$WacvrExe = Join-Path $WacvrDir "WACVR_Core.exe"
$WacvrConfig = Join-Path $WacvrDir "config.json"
$GameBin = Join-Path $Root "Game\app\bin"
$GameBat = Join-Path $GameBin "launch.bat"
$SegatoolsIni = Join-Path $GameBin "segatools.ini"
$MercuryIoDll = Join-Path $GameBin "mercuryio.dll"

$DisplayBackup = Join-Path $Root "WACVR_DISPLAY_BACKUP.json"
$LogFile = Join-Path $Root "WACVR_SESSION.log"
$CleanFlag = Join-Path $Root "WACVR_SESSION_CLEAN.flag"
$RestoreScript = Join-Path $Root ".__WACVR_V020_RESTORE.ps1"
$WatchdogScript = Join-Path $Root ".__WACVR_V020_WATCHDOG.ps1"

function Log([string]$Text) {
    $line = "[{0}] {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Text
    Write-Host $line
    Add-Content -LiteralPath $LogFile -Value $line -Encoding UTF8
}

Add-Type -AssemblyName System.Windows.Forms

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class WaccaNativeV20
{
    public const int ENUM_CURRENT_SETTINGS = -1;
    public const int DM_DISPLAYORIENTATION = 0x00000080;
    public const int DM_PELSWIDTH = 0x00080000;
    public const int DM_PELSHEIGHT = 0x00100000;
    public const int DM_DISPLAYFREQUENCY = 0x00400000;
    public const uint CDS_TEST = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr param);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
'@

function Get-PrimaryDeviceName {
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen
    if ($null -eq $screen -or [string]::IsNullOrWhiteSpace($screen.DeviceName)) {
        throw "Moniteur principal Windows introuvable."
    }
    return $screen.DeviceName
}

function Get-DisplayMode([string]$DeviceName) {
    $dm = New-Object WaccaNativeV20+DEVMODE
    $dm.dmSize = [Runtime.InteropServices.Marshal]::SizeOf([type][WaccaNativeV20+DEVMODE])
    if (-not [WaccaNativeV20]::EnumDisplaySettings($DeviceName, [WaccaNativeV20]::ENUM_CURRENT_SETTINGS, [ref]$dm)) {
        throw "Impossible de lire le mode video de $DeviceName."
    }
    return $dm
}

function Save-DisplayBackup([string]$DeviceName, $Mode) {
    [ordered]@{
        DeviceName  = $DeviceName
        Width       = [int]$Mode.dmPelsWidth
        Height      = [int]$Mode.dmPelsHeight
        Orientation = [int]$Mode.dmDisplayOrientation
        Frequency   = [int]$Mode.dmDisplayFrequency
        SavedAt     = (Get-Date).ToString("o")
    } | ConvertTo-Json | Set-Content -LiteralPath $DisplayBackup -Encoding UTF8
}

function Apply-DisplayMode([string]$DeviceName, [int]$Width, [int]$Height, [int]$Orientation, [int]$Frequency = 0) {
    $dm = Get-DisplayMode $DeviceName
    $dm.dmPelsWidth = $Width
    $dm.dmPelsHeight = $Height
    $dm.dmDisplayOrientation = $Orientation
    $dm.dmFields = [WaccaNativeV20]::DM_PELSWIDTH -bor [WaccaNativeV20]::DM_PELSHEIGHT -bor [WaccaNativeV20]::DM_DISPLAYORIENTATION

    if ($Frequency -gt 0) {
        $dm.dmDisplayFrequency = $Frequency
        $dm.dmFields = $dm.dmFields -bor [WaccaNativeV20]::DM_DISPLAYFREQUENCY
    }

    $test = [WaccaNativeV20]::ChangeDisplaySettingsEx($DeviceName, [ref]$dm, [IntPtr]::Zero, [WaccaNativeV20]::CDS_TEST, [IntPtr]::Zero)
    if ($test -ne 0) {
        Log "Mode video refuse au test Windows (code $test)."
        return $false
    }

    return ([WaccaNativeV20]::ChangeDisplaySettingsEx($DeviceName, [ref]$dm, [IntPtr]::Zero, 0, [IntPtr]::Zero) -eq 0)
}

function Restore-Display {
    if (-not (Test-Path -LiteralPath $DisplayBackup)) {
        Log "Aucune sauvegarde affichage a restaurer."
        return
    }

    try {
        $b = Get-Content -LiteralPath $DisplayBackup -Raw | ConvertFrom-Json
        Log ("Restauration affichage : {0}x{1}, orientation {2}, {3} Hz" -f $b.Width, $b.Height, $b.Orientation, $b.Frequency)
        $ok = Apply-DisplayMode -DeviceName ([string]$b.DeviceName) -Width ([int]$b.Width) -Height ([int]$b.Height) -Orientation ([int]$b.Orientation) -Frequency ([int]$b.Frequency)
        if ($ok) { Log "Affichage original restaure." }
        else { Log "ATTENTION : Windows a refuse la restauration automatique." }
    }
    catch { Log ("ERREUR restauration affichage : " + $_.Exception.Message) }
}

function Patch-WacvrConfig {
    if (-not (Test-Path -LiteralPath $WacvrConfig)) {
        throw "config.json WACVR introuvable."
    }

    $backup = Join-Path $WacvrDir "config_before_WACVR_INTEGRATED_V020.json"
    if (-not (Test-Path -LiteralPath $backup)) {
        Copy-Item -LiteralPath $WacvrConfig -Destination $backup -Force
    }

    $j = Get-Content -LiteralPath $WacvrConfig -Raw | ConvertFrom-Json

    # Preserve the validated V0.1.3 startup profile.
    $j.CaptureDesktop = $false
    $j.TouchSampleRate = 3
    $j.TestKeyBind = 99
    $j.ServiceKeyBind = 100
    $j.CoinKeyBind = 101

    [IO.File]::WriteAllText(
        $WacvrConfig,
        ($j | ConvertTo-Json -Depth 20),
        (New-Object Text.UTF8Encoding($false))
    )

    Log "WACVR configure : CaptureDesktop OFF + Touch 60 Hz + F1/F2/F3."
}

function Is-KeyDown([int]$Vk) {
    return (([int][WaccaNativeV20]::GetAsyncKeyState($Vk) -band 0x8000) -ne 0)
}

function Get-ActiveOpenXRRuntimePath {
    try {
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
            [Microsoft.Win32.RegistryHive]::LocalMachine,
            [Microsoft.Win32.RegistryView]::Registry64
        )
        $key = $base.OpenSubKey("SOFTWARE\Khronos\OpenXR\1")
        if ($key) {
            $value = [string]$key.GetValue("ActiveRuntime", "")
            $key.Dispose()
            $base.Dispose()
            if (-not [string]::IsNullOrWhiteSpace($value)) { return $value }
        }
        $base.Dispose()
    } catch {}

    try {
        return [string](Get-ItemPropertyValue -Path "HKLM:\SOFTWARE\Khronos\OpenXR\1" -Name "ActiveRuntime" -ErrorAction Stop)
    } catch {}

    return ""
}

function Start-OpenXRRuntime-IfNeeded {
    $runtimePath = Get-ActiveOpenXRRuntimePath

    if ([string]::IsNullOrWhiteSpace($runtimePath)) {
        Log "OpenXR : runtime actif non lisible. Aucun runtime n'est force."
        return
    }

    Log ("OpenXR runtime actif : " + $runtimePath)
    $lower = $runtimePath.ToLowerInvariant()

    $isSteamVR = $lower.Contains("steamvr") -or $lower.Contains("steamxr")

    if (-not $isSteamVR) {
        Log "OpenXR : runtime non-SteamVR -> SteamVR ne sera PAS lance."
        return
    }

    if (Get-Process -Name "vrserver" -ErrorAction SilentlyContinue) {
        Log "OpenXR/SteamVR : vrserver deja actif."
        return
    }

    Log "OpenXR/SteamVR actif : lancement SteamVR..."

    try {
        Start-Process "steam://rungameid/250820"
    }
    catch {
        Log "Impossible de lancer SteamVR par Steam URI."
        return
    }

    $deadline = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $deadline) {
        if (Get-Process -Name "vrserver" -ErrorAction SilentlyContinue) {
            Log "SteamVR detecte."
            Start-Sleep -Seconds 2
            return
        }
        Start-Sleep -Milliseconds 500
    }

    Log "SteamVR non detecte apres 45 s ; WACVR sera lance quand meme."
}

function Wait-ForWaccaProcess {
    $deadline = (Get-Date).AddSeconds(75)
    while ((Get-Date) -lt $deadline) {
        $p = Get-Process -Name "Mercury-Win64-Shipping" -ErrorAction SilentlyContinue |
             Sort-Object StartTime -ErrorAction SilentlyContinue |
             Select-Object -Last 1
        if ($p) { return $p }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

function Focus-Wacca($Process) {
    if (-not $Process) { return $false }
    $deadline = (Get-Date).AddSeconds(20)

    while ((Get-Date) -lt $deadline) {
        try {
            $Process.Refresh()
            $h = $Process.MainWindowHandle
            if ($h -ne [IntPtr]::Zero) {
                [void][WaccaNativeV20]::ShowWindow($h, 9)
                Start-Sleep -Milliseconds 100
                if ([WaccaNativeV20]::SetForegroundWindow($h)) {
                    Log "Focus WACCA applique automatiquement."
                    return $true
                }
                try {
                    $shell = New-Object -ComObject WScript.Shell
                    if ($shell.AppActivate($Process.Id)) {
                        Log "Focus WACCA applique via AppActivate."
                        return $true
                    }
                } catch {}
            }
        } catch {}
        Start-Sleep -Milliseconds 400
    }

    Log "ATTENTION : Windows a refuse le focus automatique WACCA."
    return $false
}

function Open-WacvrIPC {
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        try {
            return [IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting(
                "Local\WACVR_SHARED_BUFFER",
                [IO.MemoryMappedFiles.MemoryMappedFileRights]::ReadWrite
            )
        }
        catch { Start-Sleep -Milliseconds 200 }
    }
    return $null
}

function Log-LoadedModules {
    $targets = @("amdaemon", "Mercury-Win64-Shipping")
    foreach ($name in $targets) {
        $ps = @(Get-Process -Name $name -ErrorAction SilentlyContinue)
        foreach ($p in $ps) {
            try {
                $mods = @($p.Modules | ForEach-Object { $_.FileName })
                if ($mods | Where-Object { $_ -match "\\mercuryio\.dll$" }) {
                    Log "$name PID $($p.Id) : mercuryio.dll CHARGEE."
                }
                if ($mods | Where-Object { $_ -match "\\mercuryhook\.dll$" }) {
                    Log "$name PID $($p.Id) : mercuryhook.dll CHARGEE."
                }
            } catch {}
        }
    }
}

function Request-CloseProcessByName([string]$Name, [int]$WaitMs = 2500) {
    $ps = @(Get-Process -Name $Name -ErrorAction SilentlyContinue)
    foreach ($p in $ps) {
        try {
            Log "Fermeture $Name PID $($p.Id)..."
            if ($p.MainWindowHandle -ne 0) { [void]$p.CloseMainWindow() }
        } catch {}
    }
    if ($ps.Count -gt 0) { Start-Sleep -Milliseconds $WaitMs }
    foreach ($p in $ps) {
        try {
            $p.Refresh()
            if (-not $p.HasExited) {
                Log "Arret force $Name PID $($p.Id)."
                Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
            }
        } catch {}
    }
}

function Stop-SessionProcesses {
    Request-CloseProcessByName "Mercury-Win64-Shipping" 1800
    Request-CloseProcessByName "amdaemon" 1200
    Request-CloseProcessByName "WACVR_Core" 1500
}

# -------------------- START --------------------
"" | Set-Content -LiteralPath $LogFile -Encoding UTF8
Remove-Item -LiteralPath $CleanFlag -Force -ErrorAction SilentlyContinue

Log "=== WACVR CUSTOM V0.2.0 - INTEGRATED SESSION ==="
Log ("WACCA Root : " + $Root)
Log ("WACVR Dir  : " + $WacvrDir)

foreach ($p in @($WacvrExe, $WacvrConfig, $GameBat, $SegatoolsIni, $MercuryIoDll, $RestoreScript, $WatchdogScript)) {
    if (-not (Test-Path -LiteralPath $p)) { throw "Fichier obligatoire introuvable : $p" }
}

$deviceName = Get-PrimaryDeviceName
$currentMode = Get-DisplayMode $deviceName
Save-DisplayBackup $deviceName $currentMode
Log ("Affichage sauvegarde : {0}x{1} orientation={2} {3}Hz" -f $currentMode.dmPelsWidth, $currentMode.dmPelsHeight, $currentMode.dmDisplayOrientation, $currentMode.dmDisplayFrequency)

$wrapperPid = 0
[int]::TryParse([string]$env:WACVR_WRAPPER_PID, [ref]$wrapperPid) | Out-Null

$watchdog = Start-Process -FilePath "powershell.exe" -ArgumentList @(
    "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden",
    "-File", "`"$WatchdogScript`"", "-ParentPid", $PID, "-WrapperPid", $wrapperPid, "-Root", "`"$Root`""
) -WindowStyle Hidden -PassThru
Log "Watchdog de secours lance."

$EscapeEvent = $null
try {
    $EscapeEvent = [Threading.EventWaitHandle]::OpenExisting("Local\WACVR_ESCAPE_QUIT_V020")
    Log "ECHAP physique global : actif."
}
catch {
    Log "AVERTISSEMENT : event ECHAP indisponible ; fallback clavier actif."
}

$displayChanged = $false
$ipc = $null
$accessor = $null

try {
    # DO NOT patch Segatools: preserve AquaNet / NoTimeLock / existing setup.
    Patch-WacvrConfig

    Log "Passage en 1080x1920 Portrait..."
    if (Apply-DisplayMode -DeviceName $deviceName -Width 1080 -Height 1920 -Orientation 1) {
        $displayChanged = $true
        Log "Portrait applique."
    }
    elseif (Apply-DisplayMode -DeviceName $deviceName -Width 1080 -Height 1920 -Orientation 3) {
        $displayChanged = $true
        Log "Portrait inverse applique."
    }
    else { throw "Windows refuse le mode 1080x1920 Portrait." }

    Start-OpenXRRuntime-IfNeeded

    Log "Lancement WACVR Core..."
    $wacvrProcess = Start-Process -FilePath $WacvrExe -WorkingDirectory $WacvrDir -PassThru
    Start-Sleep -Seconds 4

    Log "Lancement WACCA..."
    Start-Process -FilePath "cmd.exe" -ArgumentList "/d /c `"$GameBat`"" -WorkingDirectory $GameBin

    $waccaProcess = Wait-ForWaccaProcess
    if ($waccaProcess) {
        Log "WACCA detecte PID $($waccaProcess.Id)."
        Start-Sleep -Seconds 2
        [void](Focus-Wacca $waccaProcess)
    } else {
        Log "ATTENTION : Mercury-Win64-Shipping non detecte apres 75 s."
    }

    Start-Sleep -Seconds 2
    Log-LoadedModules

    Log "Connexion IPC WACVR..."
    $ipc = Open-WacvrIPC
    if ($ipc) {
        $accessor = $ipc.CreateViewAccessor()
        Log "IPC WACVR connecte."
    } else {
        Log "ATTENTION : IPC WACVR introuvable."
    }

    Log "COMMANDES : F1=TEST / F2=SERVICE / F3=CREDIT / ECHAP=QUITTER TOUT"

    $lastTest = $false
    $lastService = $false
    $lastCoinPhysical = $false
    $lastEscapeFallback = $false
    $coinPulseActive = $false
    $coinPulseUntil = [DateTime]::MinValue
    $nextCoinAllowed = [DateTime]::MinValue

    while (-not $wacvrProcess.HasExited) {
        $now = Get-Date

        if ($EscapeEvent -and $EscapeEvent.WaitOne(0)) {
            Log "SORTIE PROPRE demandee via ECHAP physique."
            break
        }

        if (-not $EscapeEvent) {
            $esc = Is-KeyDown 0x1B
            if ($esc -and -not $lastEscapeFallback) {
                Log "SORTIE PROPRE demandee via fallback ECHAP."
                break
            }
            $lastEscapeFallback = $esc
        }

        if ($accessor) {
            $test = Is-KeyDown 0x70
            $service = Is-KeyDown 0x71

            if ($test -ne $lastTest) {
                $accessor.Write([long]0, [byte]([int]$test))
                $lastTest = $test
            }

            if ($service -ne $lastService) {
                $accessor.Write([long]1, [byte]([int]$service))
                $lastService = $service
            }

            $coinPhysical = Is-KeyDown 0x72
            if ($coinPhysical -and -not $lastCoinPhysical -and $now -ge $nextCoinAllowed) {
                $accessor.Write([long]2, [byte]1)
                $coinPulseActive = $true
                $coinPulseUntil = $now.AddMilliseconds(160)
                $nextCoinAllowed = $now.AddMilliseconds(450)
            }

            if ($coinPulseActive -and $now -ge $coinPulseUntil) {
                $accessor.Write([long]2, [byte]0)
                $coinPulseActive = $false
            }

            $lastCoinPhysical = $coinPhysical
        }

        Start-Sleep -Milliseconds 4
        try { $wacvrProcess.Refresh() } catch { break }
    }
}
catch {
    Log ("ERREUR : " + $_.Exception.Message)
}
finally {
    try {
        if ($accessor) {
            $accessor.Write([long]0, [byte]0)
            $accessor.Write([long]1, [byte]0)
            $accessor.Write([long]2, [byte]0)
            $accessor.Dispose()
        }
        if ($ipc) { $ipc.Dispose() }
    } catch {}

    Stop-SessionProcesses

    if ($displayChanged) { Restore-Display }

    try {
        "OK " + (Get-Date).ToString("o") | Set-Content -LiteralPath $CleanFlag -Encoding ASCII
    } catch {}

    try { if ($EscapeEvent) { $EscapeEvent.Dispose() } } catch {}
    Log "Session terminee proprement."
}

Write-Host ""
Write-Host "WACVR V0.2.0 : session fermee, affichage restaure." -ForegroundColor Green
Start-Sleep -Seconds 1
