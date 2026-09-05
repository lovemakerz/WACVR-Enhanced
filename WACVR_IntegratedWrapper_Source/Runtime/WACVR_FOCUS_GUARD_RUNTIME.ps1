$ErrorActionPreference = "SilentlyContinue"

Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class WaccaFocusNative
{
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags
    );
}
"@

$ProcessName = "Mercury-Win64-Shipping"

# Wait for WACCA to actually exist. This period is NOT counted in the 60 s.
$deadline = (Get-Date).AddMinutes(3)
$game = $null

while ((Get-Date) -lt $deadline) {
    $game = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } |
        Select-Object -First 1

    if ($game) {
        break
    }

    Start-Sleep -Milliseconds 500
}

if (-not $game) {
    exit 0
}

# User requested: every 2 seconds for approximately 1 minute.
$checks = 30

$HWND_TOPMOST = [IntPtr](-1)
$HWND_NOTOPMOST = [IntPtr](-2)

$SW_RESTORE = 9

$SWP_NOMOVE = 0x0002
$SWP_NOSIZE = 0x0001
$SWP_SHOWWINDOW = 0x0040
$SWP_NOACTIVATE = 0x0010

$flagsTop = $SWP_NOMOVE -bor $SWP_NOSIZE -bor $SWP_SHOWWINDOW
$flagsNormal = $SWP_NOMOVE -bor $SWP_NOSIZE -bor $SWP_SHOWWINDOW -bor $SWP_NOACTIVATE

for ($i = 0; $i -lt $checks; $i++) {

    # Refresh process/window handle each time in case UE recreates its window.
    $game = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } |
        Select-Object -First 1

    if (-not $game) {
        break
    }

    $hwnd = $game.MainWindowHandle
    $foreground = [WaccaFocusNative]::GetForegroundWindow()

    # Do nothing if WACCA already owns foreground.
    if ($foreground -ne $hwnd) {

        # Restore it if Windows temporarily minimized/changed its state.
        [void][WaccaFocusNative]::ShowWindowAsync($hwnd, $SW_RESTORE)

        # Brief TOPMOST pulse. This is much more reliable than
        # SetForegroundWindow alone when SteamVR/WACVR consoles steal focus.
        [void][WaccaFocusNative]::SetWindowPos(
            $hwnd,
            $HWND_TOPMOST,
            0, 0, 0, 0,
            $flagsTop
        )

        [void][WaccaFocusNative]::SetForegroundWindow($hwnd)

        Start-Sleep -Milliseconds 80

        # Immediately remove TOPMOST so WACCA is NOT permanently always-on-top.
        [void][WaccaFocusNative]::SetWindowPos(
            $hwnd,
            $HWND_NOTOPMOST,
            0, 0, 0, 0,
            $flagsNormal
        )

        [void][WaccaFocusNative]::SetForegroundWindow($hwnd)
    }

    Start-Sleep -Seconds 2
}
