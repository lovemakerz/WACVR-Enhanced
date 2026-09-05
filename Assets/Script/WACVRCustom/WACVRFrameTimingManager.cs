using UnityEngine;

public static class WACVRFrameTimingManager
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        // Validated V0.1.2 behavior, kept exactly for V0.1.3 CleanStable:
        // no Unity VSync and no global FPS cap. OpenXR/VDXR owns VR pacing.
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;

        Debug.Log(
            "[WACVR CUSTOM V0.1.3] CleanStable: " +
            "vSync=0, targetFrameRate=-1, OpenXR/VDXR pacing enabled."
        );
    }
}
