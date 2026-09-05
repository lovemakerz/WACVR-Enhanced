using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal static class Program
{
    private const string MutexName = @"Local\WACVR_INTEGRATED_SESSION_MUTEX_V020";
    private const string EscapeEventName = @"Local\WACVR_ESCAPE_QUIT_V020";

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int VK_ESCAPE = 0x1B;

    private const uint LLKHF_LOWER_IL_INJECTED = 0x00000002;
    private const uint LLKHF_INJECTED = 0x00000010;

    private static IntPtr hookHandle = IntPtr.Zero;
    private static LowLevelKeyboardProc hookProc = HookCallback;
    private static bool physicalEscapeDown = false;
    private static EventWaitHandle escapeEvent = null;

    private static readonly string[] RuntimeFiles = new string[]
    {
        ".__WACVR_V020_RUNTIME.bat",
        ".__WACVR_V020_AUTO.ps1",
        ".__WACVR_V020_FOCUS_GUARD.ps1",
        ".__WACVR_V020_RESTORE.ps1",
        ".__WACVR_V020_WATCHDOG.ps1"
    };

    private static readonly string[] ResourceNames = new string[]
    {
        "WACVR_RUNTIME_BAT",
        "WACVR_AUTO_PS1",
        "WACVR_FOCUS_PS1",
        "WACVR_RESTORE_PS1",
        "WACVR_WATCHDOG_PS1"
    };

    [STAThread]
    public static int Main()
    {
        bool created;

        using (Mutex mutex = new Mutex(true, MutexName, out created))
        {
            if (!created)
            {
                Console.WriteLine("WACVR est deja actif.");
                return 2;
            }

            bool eventCreated;
            using (escapeEvent = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                EscapeEventName,
                out eventCreated))
            {
                string wacvrDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
                string coreExe = Path.Combine(wacvrDir, "WACVR_Core.exe");
                string root = FindWaccaRoot(wacvrDir);

                try
                {
                    if (!File.Exists(coreExe))
                        throw new Exception("WACVR_Core.exe introuvable a cote de WACVR.exe.");

                    if (String.IsNullOrEmpty(root))
                        throw new Exception(
                            "Game\\app\\bin\\launch.bat introuvable dans le dossier parent. " +
                            "Copie le build dans ton dossier WACVR habituel, a cote du dossier Game."
                        );

                    ExtractRuntimeFiles(root);

                    Console.Title = "WACVR Custom V0.2.0 - Integrated WACCA Session Manager";
                    Console.WriteLine("============================================================");
                    Console.WriteLine(" WACVR CUSTOM V0.2.0 - INTEGRATED WACCA SESSION MANAGER");
                    Console.WriteLine("============================================================");
                    Console.WriteLine();
                    Console.WriteLine("Un seul EXE a lancer : WACVR.exe");
                    Console.WriteLine("OpenXR : runtime actif Windows respecte automatiquement.");
                    Console.WriteLine("SteamVR : lance uniquement si SteamVR est le runtime OpenXR actif.");
                    Console.WriteLine("ECHAP physique : quitte WACCA + WACVR et restaure l'affichage.");
                    Console.WriteLine();

                    hookHandle = InstallKeyboardHook();
                    if (hookHandle == IntPtr.Zero)
                    {
                        Console.WriteLine("[ATTENTION] Hook ECHAP global non installe.");
                        Console.WriteLine("Le runtime gardera un fallback GetAsyncKeyState(ECHAP).");
                    }

                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = Environment.GetEnvironmentVariable("COMSPEC");
                    if (String.IsNullOrEmpty(psi.FileName))
                        psi.FileName = "cmd.exe";

                    psi.Arguments = "/d /c \"\".__WACVR_V020_RUNTIME.bat\"\"";
                    psi.WorkingDirectory = root;
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = false;
                    psi.EnvironmentVariables["WACVR_INTEGRATED_ROOT"] = root;
                    psi.EnvironmentVariables["WACVR_INTEGRATED_DIR"] = wacvrDir;
                    psi.EnvironmentVariables["WACVR_WRAPPER_PID"] = Process.GetCurrentProcess().Id.ToString();

                    Process p = Process.Start(psi);
                    if (p == null)
                        throw new Exception("Impossible de lancer le runtime integre.");

                    while (!p.WaitForExit(20))
                        Application.DoEvents();

                    Application.DoEvents();
                    return p.ExitCode;
                }
                catch (Exception ex)
                {
                    Console.WriteLine();
                    Console.WriteLine("[ERREUR] " + ex.Message);
                    Console.WriteLine();
                    Console.WriteLine("Appuie sur Entree pour fermer.");
                    Console.ReadLine();
                    return 1;
                }
                finally
                {
                    if (hookHandle != IntPtr.Zero)
                    {
                        UnhookWindowsHookEx(hookHandle);
                        hookHandle = IntPtr.Zero;
                    }

                    CleanupRuntimeFiles(root);
                }
            }
        }
    }

    private static string FindWaccaRoot(string wacvrDir)
    {
        DirectoryInfo current = Directory.GetParent(wacvrDir);

        for (int depth = 0; current != null && depth < 4; depth++, current = current.Parent)
        {
            string gameBat = Path.Combine(current.FullName, "Game", "app", "bin", "launch.bat");
            if (File.Exists(gameBat))
                return current.FullName;
        }

        return null;
    }

    private static IntPtr InstallKeyboardHook()
    {
        using (Process curProcess = Process.GetCurrentProcess())
        using (ProcessModule curModule = curProcess.MainModule)
        {
            IntPtr moduleHandle = GetModuleHandle(curModule.ModuleName);
            return SetWindowsHookEx(WH_KEYBOARD_LL, hookProc, moduleHandle, 0);
        }
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            KBDLLHOOKSTRUCT kb =
                (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));

            if (kb.vkCode == VK_ESCAPE)
            {
                bool injected =
                    (kb.flags & LLKHF_INJECTED) != 0 ||
                    (kb.flags & LLKHF_LOWER_IL_INJECTED) != 0;

                if (!injected)
                {
                    int msg = wParam.ToInt32();

                    if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                    {
                        if (!physicalEscapeDown)
                        {
                            physicalEscapeDown = true;
                            if (escapeEvent != null)
                                escapeEvent.Set();
                        }

                        // ESC is dedicated to quitting the integrated session.
                        // Do not forward it to WACCA/WACVR.
                        return new IntPtr(1);
                    }

                    if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                    {
                        physicalEscapeDown = false;
                        return new IntPtr(1);
                    }
                }
            }
        }

        return CallNextHookEx(hookHandle, nCode, wParam, lParam);
    }

    private static void ExtractRuntimeFiles(string root)
    {
        Assembly asm = Assembly.GetExecutingAssembly();

        for (int i = 0; i < RuntimeFiles.Length; i++)
        {
            string dst = Path.Combine(root, RuntimeFiles[i]);

            using (Stream src = asm.GetManifestResourceStream(ResourceNames[i]))
            {
                if (src == null)
                    throw new Exception("Ressource embarquee absente : " + ResourceNames[i]);

                using (FileStream fs = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.Read))
                    src.CopyTo(fs);
            }
        }
    }

    private static void CleanupRuntimeFiles(string root)
    {
        if (String.IsNullOrEmpty(root))
            return;

        Thread.Sleep(500);

        for (int i = 0; i < RuntimeFiles.Length; i++)
        {
            try
            {
                string p = Path.Combine(root, RuntimeFiles[i]);
                if (File.Exists(p))
                    File.Delete(p);
            }
            catch { }
        }

        try
        {
            string clean = Path.Combine(root, "WACVR_SESSION_CLEAN.flag");
            if (File.Exists(clean))
                File.Delete(clean);
        }
        catch { }
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public int vkCode;
        public int scanCode;
        public uint flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}
