using UnityEngine;
using TMPro;
using System;

public class TouchSettingManager : MonoBehaviour
{
    private const int StartupTouchSampleRateIndex = 3;
    private const int StartupTouchSampleRateHz = 60;

    void Start()
    {
        var sampleWidget =
            ConfigManager.GetConfigPanelWidget("TouchSampleRate");

        var sampleDropdown =
            sampleWidget.GetComponent<TMP_Dropdown>();

        sampleDropdown.onValueChanged.AddListener((int value) =>
        {
            string fpsString =
                Enum.GetName(typeof(CEnum.FPS), value);

            int hz =
                Convert.ToInt32(
                    fpsString.Remove(0, 3)
                );

            Time.fixedDeltaTime =
                1f / hz;

            Debug.Log(
                "Touch physics: " +
                hz +
                " Hz"
            );
        });

        // WACVR Custom V0.1.0.4:
        // Always start at the validated 60 Hz value.
        ConfigManager.config.TouchSampleRate =
            StartupTouchSampleRateIndex;

        sampleDropdown.value =
            StartupTouchSampleRateIndex;

        Time.fixedDeltaTime =
            1f / StartupTouchSampleRateHz;

        ConfigManager.SaveFileWait();

        sampleDropdown.onValueChanged?.Invoke(
            StartupTouchSampleRateIndex
        );

        Debug.Log(
            "[WACVR CUSTOM V0.1.0.4] Startup touch physics forced to 60 Hz."
        );
    }
}
