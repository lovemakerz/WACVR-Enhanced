param(
    [string]$Root = (Split-Path -Parent $MyInvocation.MyCommand.Path)
)

$ErrorActionPreference = "SilentlyContinue"

$backup = Join-Path $Root "WACVR_DISPLAY_BACKUP.json"
if (-not (Test-Path -LiteralPath $backup)) { exit 0 }

Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class WaccaRestoreNative {
    public const int ENUM_CURRENT_SETTINGS = -1;
    public const int DM_DISPLAYORIENTATION = 0x00000080;
    public const int DM_PELSWIDTH = 0x00080000;
    public const int DM_PELSHEIGHT = 0x00100000;
    public const int DM_DISPLAYFREQUENCY = 0x00400000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
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
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
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
    public static extern bool EnumDisplaySettings(
        string deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int ChangeDisplaySettingsEx(
        string deviceName, ref DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr param);
}
"@

try {
    $b = Get-Content -LiteralPath $backup -Raw | ConvertFrom-Json
    $dm = New-Object WaccaRestoreNative+DEVMODE
    $dm.dmSize = [Runtime.InteropServices.Marshal]::SizeOf([type][WaccaRestoreNative+DEVMODE])

    if ([WaccaRestoreNative]::EnumDisplaySettings(
        [string]$b.DeviceName,
        [WaccaRestoreNative]::ENUM_CURRENT_SETTINGS,
        [ref]$dm)) {

        $dm.dmPelsWidth = [int]$b.Width
        $dm.dmPelsHeight = [int]$b.Height
        $dm.dmDisplayOrientation = [int]$b.Orientation

        $dm.dmFields =
            [WaccaRestoreNative]::DM_PELSWIDTH -bor
            [WaccaRestoreNative]::DM_PELSHEIGHT -bor
            [WaccaRestoreNative]::DM_DISPLAYORIENTATION

        if ([int]$b.Frequency -gt 0) {
            $dm.dmDisplayFrequency = [int]$b.Frequency
            $dm.dmFields = $dm.dmFields -bor [WaccaRestoreNative]::DM_DISPLAYFREQUENCY
        }

        [void][WaccaRestoreNative]::ChangeDisplaySettingsEx(
            [string]$b.DeviceName,
            [ref]$dm,
            [IntPtr]::Zero,
            0,
            [IntPtr]::Zero)
    }
} catch {}
