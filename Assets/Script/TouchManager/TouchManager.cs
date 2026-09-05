using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class TouchManager : MonoBehaviour
{
    public static event Action touchDidChange;
    static bool useIPCTouch = true;

    private static readonly Dictionary<int, bool> LastSentState = new Dictionary<int, bool>();

    void Start()
    {
        var widget = ConfigManager.GetConfigPanelWidget("UseIPCTouch");
        if (widget == null)
            return;

        var toggle = widget.GetComponent<Toggle>();
        if (toggle == null)
            return;

        toggle.onValueChanged.AddListener((value) =>
        {
            useIPCTouch = value;
            Debug.Log("UseIPCTouch: " + value);
        });

        toggle.onValueChanged.Invoke(toggle.isOn);
    }

    IEnumerator TouchTest(bool State)
    {
        for (int i = 0; i < 240; i++)
        {
            SetTouch(i, true);
            yield return new WaitForSeconds(0.05f);
            SetTouch(i, false);
            yield return new WaitForSeconds(0.05f);
        }
    }

    public static void SetTouch(int Area, bool State)
    {
        bool lastState;

        if (LastSentState.TryGetValue(Area, out lastState) && lastState == State)
        {
            HighSpeedTouchRuntime.RecordDeduplicatedTouch();
            return;
        }

        LastSentState[Area] = State;

        if (useIPCTouch)
            IPCManager.SetTouch(Area, State);
        else
            SerialManager.SetTouch(Area, State);

        touchDidChange?.Invoke();
    }
}
