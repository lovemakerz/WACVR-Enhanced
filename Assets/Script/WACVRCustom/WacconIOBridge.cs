using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// WACVR V0.2.3 Waccon-IO Clean Stable / Production.
///
/// This bridge preserves the validated WACVR touch / haptic / lighting pipeline.
/// Input remains the validated V0.2.1.2 path. WACCA LED output is copied 1:1 from
/// Waccon-IO into the legacy WACVR LED region and reproduces the original WACVR
/// mercuryio active flag (RGBA byte 3 = 0xFF).
/// </summary>
public sealed class WacconIOBridge : MonoBehaviour
{
    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    [Serializable]
    private sealed class Config
    {
        public bool Enabled = true;
        public bool MirrorButtons = true;
        public bool MirrorTouch = true;
        public bool MirrorLEDs = true;
        public int PollMilliseconds = 2;
        public int InputRefreshMilliseconds = 20;
        public int LeaseMilliseconds = 250;
        public bool PatchSegatoolsForSession = true;
        public bool RestoreSegatoolsOnExit = true;
        public bool DisableLocalWinTouch = true;
        public bool DisableLocalMouse = true;
        public bool HideMouseCursor = true;
        public bool WacconDebug = false;
    }

    private const string LegacyMapName = "Local\\WACVR_SHARED_BUFFER";
    private const int LegacyMapSize = 2164;
    private const int LegacyButtonsOffset = 0;
    private const int LegacyTouchOffset = 4;
    private const int LegacyTouchCount = 240;
    private const int LegacyLedOffset = 244;
    private const int LegacyLedBytes = 1920;

    private const string WconMapName = "Local\\WACCON_SHARED_BUFFER";
    private const uint WconMagic = 0x4E4F4357u;
    private const ushort WconMajor = 1;
    private const ushort WconMinor = 1;
    private const int WconHeaderSize = 24;
    private const int WconInputOffset = 24;
    private const int WconInputSize = 262;
    private const int WconOutputOffset = 286;
    private const int WconOutputSize = 1932;
    private const int WconServerEndpointOffset = 2218;
    private const int WconIoEndpointOffset = 2250;
    private const int WconTotalSize = 2282;
    private const uint WconCapabilityInput = 0x00000001u;
    private const uint WconCapabilityLedOutput = 0x00000002u;
    private const uint WconCapabilityServerStatus = 0x00000008u;
    private const uint SourceIdWacvr = 0x57565231u; // "WVR1"

    private Config _config;
    private MemoryMappedFile _legacyMap;
    private MemoryMappedViewAccessor _legacyView;
    private MemoryMappedFile _wconMap;
    private MemoryMappedViewAccessor _wconView;
    private Thread _thread;
    private volatile bool _stop;
    private volatile bool _threadFault;
    private string _threadFaultText;
    private bool _preparedSegatools;
    private string _root;
    private string _gameBin;
    private string _segatoolsIni;
    private string _sessionIniBackup;
    private string _gameWacconDll;
    private string _dllBackup;
    private string _createdDllMarker;
    private string _activeMarker;

    // Legacy V0.2.1.x / V0.2.2 names are read only for one-time crash recovery.
    // V0.2.3 never creates these legacy files.
    private string _legacySessionIniBackup;
    private string _legacyDllBackup;
    private string _legacyCreatedDllMarker;
    private string _legacyActiveMarker;
    private ulong _startedMs;
    private uint _lastLedSequence;
    private volatile int _ioPid;
    private volatile bool _ioConnected;
    private bool _reportedConnected;
    private bool _reportedLed;
    private volatile int _ledUnitCount;
    private long _publishedInputs;
    private long _copiedLedFrames;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        var go = new GameObject("WACVR V0.2.3 Waccon-IO Clean Stable");
        DontDestroyOnLoad(go);
        go.AddComponent<WacconIOBridge>();
    }

    private void Awake()
    {
        try
        {
            _config = LoadConfig();
            if (!_config.Enabled)
            {
                Debug.Log("[WACVR WACCON V0.2.3] Disabled: legacy V0.2.0 backend remains active.");
                enabled = false;
                return;
            }

            _root = FindWaccaRoot();
            if (string.IsNullOrEmpty(_root))
                throw new InvalidOperationException("WACCA root not found (Game/app/bin/segatools.ini missing).");

            _gameBin = Path.Combine(_root, "Game", "app", "bin");
            _segatoolsIni = Path.Combine(_gameBin, "segatools.ini");
            _sessionIniBackup = Path.Combine(_gameBin, "segatools.before_WACVR_WACCON.ini");
            _gameWacconDll = Path.Combine(_gameBin, "waccon_io.dll");
            _dllBackup = Path.Combine(_gameBin, "waccon_io.before_WACVR_WACCON.dll");
            _createdDllMarker = Path.Combine(_gameBin, ".WACVR_WACCON_CREATED_DLL");
            _activeMarker = Path.Combine(_gameBin, ".WACVR_WACCON_ACTIVE");

            _legacySessionIniBackup = Path.Combine(_gameBin, "segatools.before_WACVR_WACCON_LAB.ini");
            _legacyDllBackup = Path.Combine(_gameBin, "waccon_io.before_WACVR_WACCON_LAB.dll");
            _legacyCreatedDllMarker = Path.Combine(_gameBin, ".WACVR_WACCON_LAB_CREATED_DLL");
            _legacyActiveMarker = Path.Combine(_gameBin, ".WACVR_WACCON_LAB_ACTIVE");

            if (_config.PatchSegatoolsForSession)
                PrepareWacconForSession();

            OpenMappings();
            // WACVR's original mercuryio treated byte 3 as an IPC-active flag.
            // Start idle until Waccon provides the first valid LED frame.
            ClearLegacyLedOutput();
            _startedMs = (ulong)GetTickCount64();
            _stop = false;
            _thread = new Thread(BridgeLoop)
            {
                IsBackground = true,
                Name = "WACVR-WacconIO-Bridge"
            };
            _thread.Start();

            Debug.Log(
                "[WACVR WACCON V0.2.3] Active. " +
                "Legacy WACVR -> WCON 1.1 touch/buttons; WCON LEDs -> WACVR."
            );
        }
        catch (Exception ex)
        {
            Debug.LogError("[WACVR WACCON V0.2.3] Startup failed: " + ex);
            SafeShutdown(false);
            enabled = false;
        }
    }

    private void Update()
    {
        if (_threadFault)
        {
            _threadFault = false;
            Debug.LogError("[WACVR WACCON V0.2.3] Bridge thread fault: " + _threadFaultText);
        }

        if (_ioConnected && !_reportedConnected)
        {
            _reportedConnected = true;
            Debug.Log("[WACVR WACCON V0.2.3] waccon_io.dll connected. PID=" + _ioPid);
        }
        else if (!_ioConnected && _reportedConnected)
        {
            _reportedConnected = false;
            Debug.LogWarning("[WACVR WACCON V0.2.3] waccon_io.dll connection lost.");
        }

        if (_ledUnitCount > 0 && !_reportedLed)
        {
            _reportedLed = true;
            Debug.Log(
                "[WACVR WACCON V0.2.3] Real WACCA LED stream active. Units=" +
                _ledUnitCount + "; mapping=direct RGBA480 + WACVR active flag"
            );
        }
    }

    private void OnApplicationQuit()
    {
        SafeShutdown(true);
    }

    private void OnDestroy()
    {
        SafeShutdown(true);
    }

    private Config LoadConfig()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string path = Path.Combine(baseDir, "WACVR_WacconIO.json");
        string legacyPath = Path.Combine(baseDir, "WACVR_WacconIO_Lab.json");

        // One-time migration from the validated Lab/Polish builds.
        // If both files exist after an in-place upgrade, the old user config wins once.
        if (File.Exists(legacyPath))
        {
            try
            {
                var migrated = JsonUtility.FromJson<Config>(File.ReadAllText(legacyPath));
                if (migrated == null)
                    migrated = new Config();

                ClampConfig(migrated);
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonUtility.ToJson(migrated, true), new UTF8Encoding(false));

                if (File.Exists(path))
                    File.Delete(path);
                File.Move(temp, path);
                File.Delete(legacyPath);

                Debug.Log("[WACVR WACCON V0.2.3] Legacy Waccon config migrated to WACVR_WacconIO.json.");
                return migrated;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[WACVR WACCON V0.2.3] Legacy config migration skipped: " + ex.Message);
            }
        }

        if (!File.Exists(path))
        {
            var defaults = new Config();
            File.WriteAllText(path, JsonUtility.ToJson(defaults, true), new UTF8Encoding(false));
            return defaults;
        }

        var config = JsonUtility.FromJson<Config>(File.ReadAllText(path));
        if (config == null)
            config = new Config();

        ClampConfig(config);
        return config;
    }

    private static void ClampConfig(Config config)
    {
        config.PollMilliseconds = Mathf.Clamp(config.PollMilliseconds, 1, 100);
        config.InputRefreshMilliseconds = Mathf.Clamp(config.InputRefreshMilliseconds, 5, 500);
        config.LeaseMilliseconds = Mathf.Clamp(config.LeaseMilliseconds, 50, 5000);
    }

    private string FindWaccaRoot()
    {
        var current = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        for (int depth = 0; current != null && depth < 5; depth++, current = current.Parent)
        {
            string ini = Path.Combine(current.FullName, "Game", "app", "bin", "segatools.ini");
            if (File.Exists(ini))
                return current.FullName;
        }
        return null;
    }

    private void PrepareWacconForSession()
    {
        RecoverStaleState();

        string bundledDll = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WacconIO", "waccon_io.dll");
        if (!File.Exists(bundledDll))
            throw new FileNotFoundException("Bundled Waccon-IO DLL missing", bundledDll);
        if (!File.Exists(_segatoolsIni))
            throw new FileNotFoundException("segatools.ini missing", _segatoolsIni);

        File.Copy(_segatoolsIni, _sessionIniBackup, true);

        if (File.Exists(_gameWacconDll))
        {
            File.Copy(_gameWacconDll, _dllBackup, true);
            DeleteIfExists(_createdDllMarker);
        }
        else
        {
            File.WriteAllText(_createdDllMarker, "created by WACVR V0.2.3 Waccon-IO Clean Stable");
            DeleteIfExists(_dllBackup);
        }

        File.Copy(bundledDll, _gameWacconDll, true);

        string ini = File.ReadAllText(_segatoolsIni);
        ini = SetIniValue(ini, "mercuryio", "path", "waccon_io.dll");
        ini = SetIniValue(ini, "waccon", "debug", _config.WacconDebug ? "1" : "0");
        ini = SetIniValue(ini, "waccon", "console", "0");
        ini = SetIniValue(ini, "waccon", "cursor", _config.HideMouseCursor ? "0" : "1");
        ini = SetIniValue(ini, "waccon", "wintouch", _config.DisableLocalWinTouch ? "0" : "1");
        ini = SetIniValue(ini, "waccon", "mouse", _config.DisableLocalMouse ? "0" : "1");
        File.WriteAllText(_segatoolsIni, ini, new UTF8Encoding(false));
        File.WriteAllText(_activeMarker, DateTime.Now.ToString("O"));
        _preparedSegatools = true;

    }

    private void RecoverStaleState()
    {
        // First unwind a possible V0.2.3 interrupted session.
        RecoverStateFiles(
            _sessionIniBackup,
            _dllBackup,
            _createdDllMarker,
            _activeMarker
        );

        // Then unwind a possible pre-V0.2.3 interrupted session.
        // This keeps upgrades safe without creating any new LAB-named files.
        RecoverStateFiles(
            _legacySessionIniBackup,
            _legacyDllBackup,
            _legacyCreatedDllMarker,
            _legacyActiveMarker
        );
    }

    private void RecoverStateFiles(
        string iniBackup,
        string dllBackup,
        string createdMarker,
        string activeMarker)
    {
        if (File.Exists(iniBackup))
        {
            File.Copy(iniBackup, _segatoolsIni, true);
            DeleteIfExists(iniBackup);
        }

        if (File.Exists(dllBackup))
        {
            File.Copy(dllBackup, _gameWacconDll, true);
            DeleteIfExists(dllBackup);
            DeleteIfExists(createdMarker);
        }
        else if (File.Exists(createdMarker))
        {
            DeleteIfExists(_gameWacconDll);
            DeleteIfExists(createdMarker);
        }

        DeleteIfExists(activeMarker);
    }

    private void RestoreSessionFiles()
    {
        if (!_preparedSegatools)
            return;

        try
        {
            if (File.Exists(_sessionIniBackup))
            {
                File.Copy(_sessionIniBackup, _segatoolsIni, true);
                DeleteIfExists(_sessionIniBackup);
            }

            if (File.Exists(_dllBackup))
            {
                File.Copy(_dllBackup, _gameWacconDll, true);
                DeleteIfExists(_dllBackup);
                DeleteIfExists(_createdDllMarker);
            }
            else if (File.Exists(_createdDllMarker))
            {
                DeleteIfExists(_gameWacconDll);
                DeleteIfExists(_createdDllMarker);
            }

            DeleteIfExists(_activeMarker);
            _preparedSegatools = false;
        }
        catch (Exception ex)
        {
            Debug.LogError("[WACVR WACCON V0.2.3] Restore failed: " + ex.Message);
        }
    }

    private static string SetIniValue(string input, string section, string key, string value)
    {
        var lines = new System.Collections.Generic.List<string>(
            input.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
        );

        int sectionStart = -1;
        int sectionEnd = lines.Count;
        string sectionHeader = "[" + section + "]";

        for (int i = 0; i < lines.Count; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.Equals(sectionHeader, StringComparison.OrdinalIgnoreCase))
            {
                sectionStart = i;
                for (int j = i + 1; j < lines.Count; j++)
                {
                    string next = lines[j].Trim();
                    if (next.StartsWith("[") && next.EndsWith("]"))
                    {
                        sectionEnd = j;
                        break;
                    }
                }
                break;
            }
        }

        if (sectionStart < 0)
        {
            if (lines.Count > 0 && lines[lines.Count - 1].Length != 0)
                lines.Add(string.Empty);
            lines.Add(sectionHeader);
            lines.Add(key + "=" + value);
            return string.Join(Environment.NewLine, lines);
        }

        for (int i = sectionStart + 1; i < sectionEnd; i++)
        {
            string trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith(";") || trimmed.StartsWith("#"))
                continue;

            int eq = trimmed.IndexOf('=');
            if (eq <= 0)
                continue;

            string existingKey = trimmed.Substring(0, eq).Trim();
            if (existingKey.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = key + "=" + value;
                return string.Join(Environment.NewLine, lines);
            }
        }

        lines.Insert(sectionEnd, key + "=" + value);
        return string.Join(Environment.NewLine, lines);
    }

    private void OpenMappings()
    {
        _legacyMap = MemoryMappedFile.CreateOrOpen(LegacyMapName, LegacyMapSize, MemoryMappedFileAccess.ReadWrite);
        _legacyView = _legacyMap.CreateViewAccessor(0, LegacyMapSize, MemoryMappedFileAccess.ReadWrite);

        _wconMap = MemoryMappedFile.CreateOrOpen(WconMapName, WconTotalSize, MemoryMappedFileAccess.ReadWrite);
        _wconView = _wconMap.CreateViewAccessor(0, WconTotalSize, MemoryMappedFileAccess.ReadWrite);
        InitializeWconHeader();
    }

    private void InitializeWconHeader()
    {
        uint magic = _wconView.ReadUInt32(0);
        if (magic == 0)
        {
            _wconView.Write(0, WconMagic);
            _wconView.Write(4, WconMajor);
            _wconView.Write(6, WconMinor);
            _wconView.Write(8, (uint)WconTotalSize);
            _wconView.Write(12, WconCapabilityInput | WconCapabilityLedOutput);
        }
        else if (magic != WconMagic ||
                 _wconView.ReadUInt16(4) != WconMajor ||
                 _wconView.ReadUInt16(6) != WconMinor ||
                 _wconView.ReadUInt32(8) != WconTotalSize)
        {
            throw new InvalidOperationException("WACCON_SHARED_BUFFER ABI mismatch; expected WCON 1.1 size 2282.");
        }

        UpdateServerStatus(_wconView.ReadUInt32(16));
        _wconView.Flush();
    }

    private void BridgeLoop()
    {
        var legacy = new byte[LegacyLedOffset];
        var previousInput = new byte[242];
        var inputPayload = new byte[WconInputSize];
        var ledBytes = new byte[LegacyLedBytes];
        bool havePreviousInput = false;
        long lastPublish = 0;
        long lastStatus = 0;

        try
        {
            while (!_stop)
            {
                long now = unchecked((long)GetTickCount64());

                _legacyView.ReadArray(0, legacy, 0, legacy.Length);

                byte opbtn = 0;
                if (_config.MirrorButtons)
                {
                    if (legacy[LegacyButtonsOffset] != 0) opbtn |= 0x01;      // TEST
                    if (legacy[LegacyButtonsOffset + 1] != 0) opbtn |= 0x02;  // SERVICE
                    if (legacy[LegacyButtonsOffset + 2] != 0) opbtn |= 0x04;  // COIN
                }

                inputPayload[0] = opbtn;
                inputPayload[1] = 0;

                if (_config.MirrorTouch)
                    Buffer.BlockCopy(legacy, LegacyTouchOffset, inputPayload, 2, LegacyTouchCount);
                else
                    Array.Clear(inputPayload, 2, LegacyTouchCount);

                bool changed = !havePreviousInput;
                if (!changed)
                {
                    for (int i = 0; i < 242; i++)
                    {
                        if (inputPayload[i] != previousInput[i])
                        {
                            changed = true;
                            break;
                        }
                    }
                }

                if (changed || now - lastPublish >= _config.InputRefreshMilliseconds)
                {
                    WriteUInt32(inputPayload, 242, SourceIdWacvr);
                    WriteUInt64(inputPayload, 246, (ulong)now * 1000UL);
                    WriteUInt64(inputPayload, 254, (ulong)_config.LeaseMilliseconds);
                    PublishWconInput(inputPayload);
                    Buffer.BlockCopy(inputPayload, 0, previousInput, 0, 242);
                    havePreviousInput = true;
                    lastPublish = now;
                    Interlocked.Increment(ref _publishedInputs);
                }

                if (_config.MirrorLEDs)
                    MirrorLedFrame(ledBytes);

                if (now - lastStatus >= 100)
                {
                    UpdateConnectionStatus(now);
                    UpdateServerStatus(_wconView.ReadUInt32(16));
                    lastStatus = now;
                }

                Thread.Sleep(_config.PollMilliseconds);
            }
        }
        catch (Exception ex)
        {
            _threadFaultText = ex.ToString();
            _threadFault = true;
        }
        finally
        {
            try { ClearWconInput(); } catch { }
        }
    }

    private void PublishWconInput(byte[] payload)
    {
        uint odd = (_wconView.ReadUInt32(16) + 1u) | 1u;
        uint even = odd + 1u;
        _wconView.Write(16, odd);
        _wconView.WriteArray(WconInputOffset, payload, 0, payload.Length);
        _wconView.Write(16, even);
        UpdateServerStatus(even);
    }

    private void ClearWconInput()
    {
        var clear = new byte[WconInputSize];
        WriteUInt32(clear, 242, SourceIdWacvr);
        WriteUInt64(clear, 246, (ulong)GetTickCount64() * 1000UL);
        WriteUInt64(clear, 254, 1UL);
        PublishWconInput(clear);
    }

    private void MirrorLedFrame(byte[] ledBytes)
    {
        uint before = _wconView.ReadUInt32(20);
        if ((before & 1u) != 0 || before == _lastLedSequence)
            return;

        uint unitCount = Math.Min(_wconView.ReadUInt32(WconOutputOffset), 480u);
        Array.Clear(ledBytes, 0, ledBytes.Length);
        if (unitCount > 0)
            _wconView.ReadArray(WconOutputOffset + 4, ledBytes, 0, (int)unitCount * 4);

        uint after = _wconView.ReadUInt32(20);
        if (before != after || (after & 1u) != 0)
            return;

        if (unitCount == 0)
        {
            ClearLegacyLedOutput();
            _lastLedSequence = after;
            return;
        }

        // Exact compatibility with WACVR's original mercuryio:
        // it copied the 480 RGBA units 1:1, but forced rgba[3] to 0xFF as
        // the LightManager IPC-active sentinel. LightManager ignores alpha
        // for emitted color, so this does not alter the visible first LED RGB.
        ledBytes[3] = 0xFF;
        _legacyView.WriteArray(LegacyLedOffset, ledBytes, 0, ledBytes.Length);

        _lastLedSequence = after;
        _ledUnitCount = (int)unitCount;
        Interlocked.Increment(ref _copiedLedFrames);
    }

    private void ClearLegacyLedOutput()
    {
        if (_legacyView == null)
            return;

        var clear = new byte[LegacyLedBytes];
        _legacyView.WriteArray(LegacyLedOffset, clear, 0, clear.Length);
        _ledUnitCount = 0;
        _reportedLed = false;
    }

    private void UpdateConnectionStatus(long now)
    {
        uint pid = _wconView.ReadUInt32(WconIoEndpointOffset);
        ulong heartbeat = _wconView.ReadUInt64(WconIoEndpointOffset + 16);
        bool connected = pid != 0 && heartbeat != 0 &&
                         (ulong)now >= heartbeat &&
                         (ulong)now - heartbeat <= 3000UL;

        bool wasConnected = _ioConnected;
        _ioPid = (int)pid;
        _ioConnected = connected;

        if (wasConnected && !connected)
            ClearLegacyLedOutput();
    }

    private void UpdateServerStatus(uint inputSequence)
    {
        uint capabilities = _wconView.ReadUInt32(12);
        _wconView.Write(12, capabilities | WconCapabilityServerStatus);
        _wconView.Write(WconServerEndpointOffset, (uint)System.Diagnostics.Process.GetCurrentProcess().Id);
        _wconView.Write(WconServerEndpointOffset + 4, WconMajor);
        _wconView.Write(WconServerEndpointOffset + 6, WconMinor);
        _wconView.Write(WconServerEndpointOffset + 8, _startedMs);
        _wconView.Write(WconServerEndpointOffset + 16, (ulong)GetTickCount64());
        _wconView.Write(WconServerEndpointOffset + 24, (ulong)inputSequence);
    }

    private static void WriteUInt32(byte[] target, int offset, uint value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        Buffer.BlockCopy(bytes, 0, target, offset, 4);
    }

    private static void WriteUInt64(byte[] target, int offset, ulong value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        Buffer.BlockCopy(bytes, 0, target, offset, 8);
    }

    private void SafeShutdown(bool restoreFiles)
    {
        _stop = true;
        try
        {
            if (_thread != null && _thread.IsAlive)
                _thread.Join(800);
        }
        catch { }

        try { ClearWconInput(); } catch { }
        try { ClearLegacyLedOutput(); } catch { }

        try { _legacyView?.Dispose(); } catch { }
        try { _legacyMap?.Dispose(); } catch { }
        try { _wconView?.Dispose(); } catch { }
        try { _wconMap?.Dispose(); } catch { }
        _legacyView = null;
        _legacyMap = null;
        _wconView = null;
        _wconMap = null;

        if (restoreFiles && _config != null && _config.RestoreSegatoolsOnExit)
            RestoreSessionFiles();
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }
}
