using UnityEngine;
using UnityEngine.SceneManagement;
using System;
using System.Collections.Generic;
using System.IO;

[Serializable]
public class HighSpeedTouchConfig
{
    public bool Enabled = true;
    public float TouchPaddingMeters = 0.025f;
    public float MinimumSweepMeters = 0.004f;
    public float MinimumSweepSpeedMetersPerSecond = 0.35f;
    public float HoldMilliseconds = 30f;
    public bool HapticsOnSweptHit = true;
    public float HapticRetriggerCooldownMilliseconds = 24f;
    public int PhysicsHitBufferSize = 64;

    // V0.1.1.2 Tracking Hardening. Normal touch sensitivity is unchanged.
    public float MaximumSingleFrameTrackingJumpMeters = 0.35f;
    public float MaximumTrackingSpeedMetersPerSecond = 20f;
}

public sealed class HighSpeedRegisteredHand
{
    public Transform ProxyTransform;
    public Transform TrackingTransform;
    public Action PulseHaptic;
    public int InstanceId;
    public bool UsesRawTracking;
}

public class HighSpeedTouchRuntime : MonoBehaviour
{
    private class MotionState
    {
        public Vector3 LastPosition;
        public Vector3 Previous;
        public Vector3 Current;
        public Transform LastSource;
        public bool HasPosition;
        public int UpdatedFrame = -1;
        public float Speed;
    }

    public static HighSpeedTouchConfig Config { get; private set; } = new HighSpeedTouchConfig();
    public static bool SweptTouchEnabled { get; private set; } = true;
    public static bool DiagnosticsVisible { get; private set; } = false;
    public static bool HapticsEnabled { get; private set; } = true;

    private static readonly List<HighSpeedRegisteredHand> Hands =
        new List<HighSpeedRegisteredHand>();

    private static HighSpeedRegisteredHand[] _handsSnapshot =
        new HighSpeedRegisteredHand[0];

    private static readonly Dictionary<int, MotionState> Motion =
        new Dictionary<int, MotionState>();

    private static long _physicalEnter;
    private static long _physicalExit;
    private static long _sweptHits;
    private static long _deduplicatedTouches;
    private static long _touchOn;
    private static long _touchOff;
    private static long _haptics;
    private static long _physicsQueries;
    private static long _physicsCandidates;
    private static long _physicsBufferSaturated;
    private static long _rawMotionSamples;
    private static long _proxyFallbackSamples;
    private static long _sourceSwitchResets;
    private static long _invalidPositionResets;
    private static long _trackingJumpResets;
    private static float _maxHandSpeed;
    private static float _maxAcceptedHandSpeed;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindObjectOfType<HighSpeedTouchRuntime>() != null)
            return;

        GameObject go =
            new GameObject("WACVR Custom High-Speed Touch V0.1.3 CleanStable");

        DontDestroyOnLoad(go);
        go.AddComponent<HighSpeedTouchRuntime>();
    }

    private void Awake()
    {
        LoadConfig();
        SweptTouchEnabled = Config.Enabled;
        SceneManager.sceneLoaded += OnSceneLoaded;

        Debug.Log(
            "[WACVR CUSTOM V0.1.3] CleanStable loaded. " +
            "Validated touch/tracking/haptics preserved; automatic diagnostics disabled."
        );
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        CleanupRegistrations();
        Motion.Clear();
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F7))
        {
            SweptTouchEnabled = !SweptTouchEnabled;
            Debug.Log("[WACVR CUSTOM] Swept Touch = " + SweptTouchEnabled);
        }

        if (Input.GetKeyDown(KeyCode.F8))
        {
            DiagnosticsVisible = !DiagnosticsVisible;
            Debug.Log("[WACVR CUSTOM] Stats overlay = " + DiagnosticsVisible);
        }

        if (Input.GetKeyDown(KeyCode.F9))
        {
            HapticsEnabled = !HapticsEnabled;
            Debug.Log("[WACVR CUSTOM] Haptics = " + HapticsEnabled);
        }

        CleanupRegistrations();

    }

    private void OnGUI()
    {
        if (!DiagnosticsVisible)
            return;

        int rawHands;
        int proxyHands;
        CountTrackingModes(out rawHands, out proxyHands);

        GUI.Box(
            new Rect(10, 10, 850, 185),
            "WACVR CUSTOM V0.1.1.2 - Tracking Hardening\n" +
            "F7 Hybrid: " + SweptTouchEnabled +
            " | F9 Haptics: " + HapticsEnabled + "\n" +
            "Hands: " + _handsSnapshot.Length +
            " | RAW Target: " + rawHands +
            " | Proxy fallback: " + proxyHands + "\n" +
            "Max observed/accepted: " + _maxHandSpeed.ToString("F2") + "/" +
            _maxAcceptedHandSpeed.ToString("F2") + " m/s" +
            " | Raw/Proxy samples: " + _rawMotionSamples + "/" + _proxyFallbackSamples + "\n" +
            "Tracking resets - Jump: " + _trackingJumpResets +
            " | Invalid: " + _invalidPositionResets +
            " | Source: " + _sourceSwitchResets + "\n" +
            "Physical Enter/Exit: " + _physicalEnter + "/" + _physicalExit +
            " | Swept hits: " + _sweptHits +
            " | Dedup: " + _deduplicatedTouches + "\n" +
            "Touch ON/OFF: " + _touchOn + "/" + _touchOff +
            " | Haptics: " + _haptics + "\n" +
            "Physics queries/candidates: " + _physicsQueries + "/" + _physicsCandidates +
            " | Buffer full: " + _physicsBufferSaturated
        );
    }

    public static void RegisterHand(
        Transform proxyTransform,
        Transform trackingTransform,
        Action pulseHaptic,
        bool usesRawTracking)
    {
        if (proxyTransform == null)
            return;

        if (trackingTransform == null)
        {
            trackingTransform = proxyTransform;
            usesRawTracking = false;
        }

        int id = proxyTransform.GetInstanceID();

        for (int i = 0; i < Hands.Count; i++)
        {
            if (Hands[i].InstanceId != id)
                continue;

            bool sourceChanged =
                Hands[i].TrackingTransform != trackingTransform;

            Hands[i].ProxyTransform = proxyTransform;
            Hands[i].TrackingTransform = trackingTransform;
            Hands[i].PulseHaptic = pulseHaptic;
            Hands[i].UsesRawTracking = usesRawTracking;

            if (sourceChanged)
            {
                Motion.Remove(id);
                _sourceSwitchResets++;
            }

            RebuildHandSnapshot();
            return;
        }

        Hands.Add(
            new HighSpeedRegisteredHand
            {
                ProxyTransform = proxyTransform,
                TrackingTransform = trackingTransform,
                PulseHaptic = pulseHaptic,
                InstanceId = id,
                UsesRawTracking = usesRawTracking
            }
        );

        Motion.Remove(id);
        RebuildHandSnapshot();
    }

    public static void UnregisterHand(Transform proxyTransform)
    {
        if (proxyTransform == null)
            return;

        int id = proxyTransform.GetInstanceID();

        for (int i = Hands.Count - 1; i >= 0; i--)
        {
            if (Hands[i].InstanceId == id)
                Hands.RemoveAt(i);
        }

        Motion.Remove(id);
        RebuildHandSnapshot();
    }

    public static HighSpeedRegisteredHand[] GetHands()
    {
        return _handsSnapshot;
    }

    public static bool TryGetHandMotion(
        HighSpeedRegisteredHand hand,
        out Vector3 previous,
        out Vector3 current,
        out float speed)
    {
        previous = Vector3.zero;
        current = Vector3.zero;
        speed = 0f;

        if (hand == null || hand.ProxyTransform == null)
            return false;

        Transform source = hand.TrackingTransform;
        bool usingRaw = hand.UsesRawTracking;

        if (source == null || !source.gameObject.activeInHierarchy)
        {
            source = hand.ProxyTransform;
            usingRaw = false;
        }

        if (source == null)
            return false;

        int id = hand.InstanceId;
        MotionState state;

        if (!Motion.TryGetValue(id, out state))
        {
            state = new MotionState();
            Motion[id] = state;
        }

        if (state.UpdatedFrame != Time.frameCount)
        {
            if (state.LastSource != source)
            {
                state.HasPosition = false;
                state.LastSource = source;
                _sourceSwitchResets++;
            }

            Vector3 position = source.position;

            if (!IsFinite(position))
            {
                state.HasPosition = false;
                state.Speed = 0f;
                state.UpdatedFrame = Time.frameCount;
                _invalidPositionResets++;
                return false;
            }

            if (!state.HasPosition)
            {
                ResetMotionBaseline(state, position);
            }
            else
            {
                state.Previous = state.LastPosition;
                state.Current = position;

                float dt =
                    Mathf.Max(Time.unscaledDeltaTime, 0.0001f);

                float travel =
                    Vector3.Distance(state.Previous, state.Current);

                float observedSpeed = travel / dt;

                if (observedSpeed > _maxHandSpeed)
                    _maxHandSpeed = observedSpeed;

                bool impossibleJump =
                    (Config.MaximumSingleFrameTrackingJumpMeters > 0f &&
                     travel > Config.MaximumSingleFrameTrackingJumpMeters) ||
                    (Config.MaximumTrackingSpeedMetersPerSecond > 0f &&
                     observedSpeed > Config.MaximumTrackingSpeedMetersPerSecond);

                if (impossibleJump)
                {
                    // A tracking recovery/recenter must never create a huge
                    // swept capsule through unrelated touch zones.
                    ResetMotionBaseline(state, position);
                    _trackingJumpResets++;
                }
                else
                {
                    state.Speed = observedSpeed;
                    state.LastPosition = position;

                    if (observedSpeed > _maxAcceptedHandSpeed)
                        _maxAcceptedHandSpeed = observedSpeed;
                }
            }

            if (usingRaw)
                _rawMotionSamples++;
            else
                _proxyFallbackSamples++;

            state.UpdatedFrame = Time.frameCount;
        }

        previous = state.Previous;
        current = state.Current;
        speed = state.Speed;

        return state.HasPosition;
    }

    public static void PulseHaptic(HighSpeedRegisteredHand hand)
    {
        if (!HapticsEnabled ||
            hand == null ||
            hand.PulseHaptic == null)
        {
            return;
        }

        hand.PulseHaptic();
    }

    public static void RecordPhysicalEnter()
    {
        _physicalEnter++;
    }

    public static void RecordPhysicalExit()
    {
        _physicalExit++;
    }

    public static void RecordSweptHit(float speed)
    {
        _sweptHits++;

        if (speed > _maxHandSpeed)
            _maxHandSpeed = speed;
    }

    public static void RecordDeduplicatedTouch()
    {
        _deduplicatedTouches++;
    }

    public static void RecordLogicalTouch(bool state)
    {
        if (state)
            _touchOn++;
        else
            _touchOff++;
    }

    public static void RecordHaptic()
    {
        _haptics++;
    }

    public static void RecordPhysicsQuery(
        int candidates,
        bool saturated)
    {
        _physicsQueries++;
        _physicsCandidates += candidates;

        if (saturated)
            _physicsBufferSaturated++;
    }

    private static void CleanupRegistrations()
    {
        bool changed = false;

        for (int i = Hands.Count - 1; i >= 0; i--)
        {
            HighSpeedRegisteredHand hand = Hands[i];

            if (hand == null || hand.ProxyTransform == null)
            {
                if (hand != null)
                    Motion.Remove(hand.InstanceId);

                Hands.RemoveAt(i);
                changed = true;
                continue;
            }

            if (hand.TrackingTransform == null)
            {
                hand.TrackingTransform = hand.ProxyTransform;
                hand.UsesRawTracking = false;
                Motion.Remove(hand.InstanceId);
                _sourceSwitchResets++;
                changed = true;
            }
        }

        if (changed)
            RebuildHandSnapshot();
    }

    private static void RebuildHandSnapshot()
    {
        _handsSnapshot = Hands.ToArray();
    }

    private static void CountTrackingModes(
        out int rawHands,
        out int proxyHands)
    {
        rawHands = 0;
        proxyHands = 0;

        for (int i = 0; i < _handsSnapshot.Length; i++)
        {
            HighSpeedRegisteredHand hand = _handsSnapshot[i];

            if (hand != null &&
                hand.UsesRawTracking &&
                hand.TrackingTransform != null &&
                hand.TrackingTransform.gameObject.activeInHierarchy)
            {
                rawHands++;
            }
            else
            {
                proxyHands++;
            }
        }
    }

    private static void ResetMotionBaseline(
        MotionState state,
        Vector3 position)
    {
        state.LastPosition = position;
        state.Previous = position;
        state.Current = position;
        state.Speed = 0f;
        state.HasPosition = true;
    }

    private static bool IsFinite(Vector3 value)
    {
        return
            !float.IsNaN(value.x) &&
            !float.IsNaN(value.y) &&
            !float.IsNaN(value.z) &&
            !float.IsInfinity(value.x) &&
            !float.IsInfinity(value.y) &&
            !float.IsInfinity(value.z);
    }

    private static void LoadConfig()
    {
        string parent =
            Directory.GetParent(Application.dataPath).FullName;

        string path =
            Path.Combine(parent, "WACVR_HighSpeedTouch.json");

        try
        {
            if (File.Exists(path))
            {
                HighSpeedTouchConfig loaded =
                    JsonUtility.FromJson<HighSpeedTouchConfig>(
                        File.ReadAllText(path)
                    );

                if (loaded != null)
                    Config = loaded;
            }
            else
            {
                File.WriteAllText(
                    path,
                    JsonUtility.ToJson(Config, true)
                );
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                "[WACVR CUSTOM] Config read/write failed: " +
                ex.Message
            );
        }
    }


}
