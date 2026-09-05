using UnityEngine;
using System.Collections.Generic;

public class HighSpeedTouchPhysicsBridge : MonoBehaviour
{
    private static HighSpeedTouchPhysicsBridge _instance;

    private static readonly Dictionary<Collider, ColliderToTouch> ZoneByCollider =
        new Dictionary<Collider, ColliderToTouch>();

    private static int _touchLayerMask = 0;

    private Collider[] _hitBuffer;

    private readonly HashSet<ColliderToTouch> _activeSweptZones =
        new HashSet<ColliderToTouch>();

    private readonly List<ColliderToTouch> _zonesToRemove =
        new List<ColliderToTouch>(16);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (_instance != null)
            return;

        HighSpeedTouchPhysicsBridge existing =
            FindObjectOfType<HighSpeedTouchPhysicsBridge>();

        if (existing != null)
        {
            _instance = existing;
            return;
        }

        GameObject go =
            new GameObject("WACVR HybridTouch Physics Bridge V0.1.3 CleanStable");

        DontDestroyOnLoad(go);
        _instance = go.AddComponent<HighSpeedTouchPhysicsBridge>();
    }

    private void Awake()
    {
        _instance = this;

        int requested =
            Mathf.Clamp(
                HighSpeedTouchRuntime.Config.PhysicsHitBufferSize,
                16,
                256
            );

        _hitBuffer = new Collider[requested];

    }

    public static void RegisterZone(
        ColliderToTouch zone,
        Collider zoneCollider)
    {
        if (zone == null || zoneCollider == null)
            return;

        ZoneByCollider[zoneCollider] = zone;
        RebuildTouchLayerMask();
    }

    public static void UnregisterZone(
        ColliderToTouch zone,
        Collider zoneCollider)
    {
        if (zoneCollider != null)
            ZoneByCollider.Remove(zoneCollider);

        RebuildTouchLayerMask();
    }

    private static void RebuildTouchLayerMask()
    {
        int mask = 0;
        List<Collider> dead = null;

        foreach (KeyValuePair<Collider, ColliderToTouch> pair in ZoneByCollider)
        {
            Collider col = pair.Key;

            if (col == null || pair.Value == null)
            {
                if (dead == null)
                    dead = new List<Collider>();

                dead.Add(col);
                continue;
            }

            mask |= 1 << col.gameObject.layer;
        }

        if (dead != null)
        {
            for (int i = 0; i < dead.Count; i++)
                ZoneByCollider.Remove(dead[i]);
        }

        _touchLayerMask = mask;
    }

    private void Update()
    {
        float now = Time.unscaledTime;

        TickActiveZones(now);

        if (!HighSpeedTouchRuntime.SweptTouchEnabled)
            return;

        if (_touchLayerMask == 0 || ZoneByCollider.Count == 0)
            return;

        HighSpeedRegisteredHand[] hands =
            HighSpeedTouchRuntime.GetHands();

        if (hands == null || hands.Length == 0)
            return;

        HighSpeedTouchConfig cfg =
            HighSpeedTouchRuntime.Config;

        float holdUntil =
            now + (cfg.HoldMilliseconds / 1000f);

        for (int h = 0; h < hands.Length; h++)
        {
            HighSpeedRegisteredHand hand = hands[h];

            if (hand == null ||
                hand.ProxyTransform == null ||
                !hand.ProxyTransform.gameObject.activeInHierarchy)
            {
                continue;
            }

            Vector3 previous;
            Vector3 current;
            float speed;

            if (!HighSpeedTouchRuntime.TryGetHandMotion(
                    hand,
                    out previous,
                    out current,
                    out speed))
            {
                continue;
            }

            if (speed < cfg.MinimumSweepSpeedMetersPerSecond)
                continue;

            float travel =
                Vector3.Distance(previous, current);

            if (travel < cfg.MinimumSweepMeters)
                continue;

            int hitCount =
                Physics.OverlapCapsuleNonAlloc(
                    previous,
                    current,
                    cfg.TouchPaddingMeters,
                    _hitBuffer,
                    _touchLayerMask,
                    QueryTriggerInteraction.Collide
                );

            bool saturated =
                hitCount >= _hitBuffer.Length;

            HighSpeedTouchRuntime.RecordPhysicsQuery(
                hitCount,
                saturated
            );

            for (int i = 0; i < hitCount; i++)
            {
                Collider hit = _hitBuffer[i];

                if (hit == null)
                    continue;

                ColliderToTouch zone;

                if (!ZoneByCollider.TryGetValue(hit, out zone))
                    continue;

                if (zone == null)
                    continue;

                zone.RegisterSweptHit(
                    hand,
                    speed,
                    holdUntil
                );

                _activeSweptZones.Add(zone);
            }
        }
    }

    private void TickActiveZones(float now)
    {
        if (_activeSweptZones.Count == 0)
            return;

        _zonesToRemove.Clear();

        foreach (ColliderToTouch zone in _activeSweptZones)
        {
            if (zone == null || !zone.TickSweptHold(now))
                _zonesToRemove.Add(zone);
        }

        for (int i = 0; i < _zonesToRemove.Count; i++)
            _activeSweptZones.Remove(_zonesToRemove[i]);
    }
}
