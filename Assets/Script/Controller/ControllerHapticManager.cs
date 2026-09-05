using UnityEngine.UI;
using UnityEngine;
using UnityEngine.XR;

public class ControllerHapticManager : MonoBehaviour
{
    public XRNode Hand;
    private InputDevice device;

    public float duration = 0.1f;
    public float amplitude = 1f;

    private float _lastPulseTime = -100f;
    private Transform _trackingTransform;
    private bool _usingRawTracking;

    private void OnEnable()
    {
        RegisterTrackingSource();
    }

    void Start()
    {
        var durationWidget = ConfigManager.GetConfigPanelWidget("HapticDuration");
        var amplitudeWidget = ConfigManager.GetConfigPanelWidget("HapticAmplitude");

        if (durationWidget != null)
        {
            var durationSlider = durationWidget.GetComponent<Slider>();
            if (durationSlider != null)
            {
                durationSlider.onValueChanged.AddListener((float value) => { duration = value; });
                durationSlider.onValueChanged?.Invoke(duration);
            }
        }

        if (amplitudeWidget != null)
        {
            var amplitudeSlider = amplitudeWidget.GetComponent<Slider>();
            if (amplitudeSlider != null)
            {
                amplitudeSlider.onValueChanged.AddListener((float value) => { amplitude = value; });
                amplitudeSlider.onValueChanged?.Invoke(amplitude);
            }
        }

        RegisterTrackingSource();
    }

    private void RegisterTrackingSource()
    {
        Transform candidate = transform;
        bool raw = false;

        HandFollowManager handFollow = GetComponent<HandFollowManager>();

        if (handFollow != null &&
            handFollow.Target != null &&
            handFollow.Target.transform != null)
        {
            candidate = handFollow.Target.transform;
            raw = candidate != transform;
        }

        _trackingTransform = candidate;
        _usingRawTracking = raw;

        HighSpeedTouchRuntime.RegisterHand(
            transform,
            _trackingTransform,
            PulseHaptic,
            _usingRawTracking
        );

    }

    private void OnTriggerEnter(Collider other)
    {
        PulseHaptic();
    }

    private void OnTriggerExit(Collider other)
    {
        // Keep the V0.1.0.4 validated haptic behavior:
        // do not stop an impulse on every zone exit.
    }

    private void OnDisable()
    {
        HighSpeedTouchRuntime.UnregisterHand(transform);

        device = InputDevices.GetDeviceAtXRNode(Hand);
        if (device.isValid)
            device.StopHaptics();
    }

    public void PulseHaptic()
    {
        if (!HighSpeedTouchRuntime.HapticsEnabled)
            return;

        float cooldown =
            HighSpeedTouchRuntime.Config.HapticRetriggerCooldownMilliseconds / 1000f;

        float now = Time.unscaledTime;

        if (now - _lastPulseTime < cooldown)
            return;

        _lastPulseTime = now;

        device = InputDevices.GetDeviceAtXRNode(Hand);
        if (!device.isValid)
            return;

        device.SendHapticImpulse(0, amplitude, duration);
        HighSpeedTouchRuntime.RecordHaptic();
    }
}
