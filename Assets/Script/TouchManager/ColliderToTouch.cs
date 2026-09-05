using UnityEngine;
using System;
using System.Collections.Generic;

public class ColliderToTouch : MonoBehaviour
{
    public LightManager LightManager;

    private int _insideColliderCount = 0;
    private int _area;
    private bool _logicalPressed = false;
    private Collider _zoneCollider;

    private readonly Dictionary<int, float> _sweptHoldUntil =
        new Dictionary<int, float>();

    private readonly List<int> _expiredHandIds =
        new List<int>(2);

    private void Awake()
    {
        _zoneCollider = GetComponent<Collider>();
    }

    private void Start()
    {
        _area = Convert.ToInt32(gameObject.name);

        if (_zoneCollider != null)
            HighSpeedTouchPhysicsBridge.RegisterZone(
                this,
                _zoneCollider
            );
    }

    private void OnEnable()
    {
        if (_zoneCollider == null)
            _zoneCollider = GetComponent<Collider>();

        if (_zoneCollider != null)
            HighSpeedTouchPhysicsBridge.RegisterZone(
                this,
                _zoneCollider
            );
    }

    private void OnDisable()
    {
        if (_zoneCollider != null)
            HighSpeedTouchPhysicsBridge.UnregisterZone(
                this,
                _zoneCollider
            );
    }

    private void OnTriggerEnter(Collider collider)
    {
        _insideColliderCount += 1;
        SetLogicalPressed(true);
        HighSpeedTouchRuntime.RecordPhysicalEnter();
    }

    private void OnTriggerExit(Collider collider)
    {
        _insideColliderCount -= 1;

        _insideColliderCount =
            Mathf.Max(0, _insideColliderCount);

        ReconcileLogicalState();
        HighSpeedTouchRuntime.RecordPhysicalExit();
    }

    public void RegisterSweptHit(
        HighSpeedRegisteredHand hand,
        float speed,
        float holdUntil)
    {
        if (hand == null)
            return;

        int handId = hand.InstanceId;

        float previousUntil;

        bool wasHeld =
            _sweptHoldUntil.TryGetValue(
                handId,
                out previousUntil
            ) &&
            previousUntil > Time.unscaledTime;

        if (!wasHeld || holdUntil > previousUntil)
            _sweptHoldUntil[handId] = holdUntil;

        if (!wasHeld)
        {
            SetLogicalPressed(true);
            HighSpeedTouchRuntime.RecordSweptHit(speed);

            if (HighSpeedTouchRuntime.Config.HapticsOnSweptHit)
                HighSpeedTouchRuntime.PulseHaptic(hand);
        }
    }

    public bool TickSweptHold(float now)
    {
        if (_sweptHoldUntil.Count == 0)
        {
            ReconcileLogicalState();
            return false;
        }

        _expiredHandIds.Clear();

        foreach (
            KeyValuePair<int, float> pair
            in _sweptHoldUntil)
        {
            if (pair.Value <= now)
                _expiredHandIds.Add(pair.Key);
        }

        for (int i = 0; i < _expiredHandIds.Count; i++)
            _sweptHoldUntil.Remove(
                _expiredHandIds[i]
            );

        ReconcileLogicalState();

        return _sweptHoldUntil.Count > 0;
    }

    private void ReconcileLogicalState()
    {
        SetLogicalPressed(
            _insideColliderCount > 0 ||
            _sweptHoldUntil.Count > 0
        );
    }

    private void SetLogicalPressed(bool pressed)
    {
        if (_logicalPressed == pressed)
            return;

        _logicalPressed = pressed;

        TouchManager.SetTouch(
            _area,
            pressed
        );

        if (LightManager != null)
            LightManager.UpdateFadeLight(
                _area,
                pressed
            );

        HighSpeedTouchRuntime.RecordLogicalTouch(
            pressed
        );
    }
}
