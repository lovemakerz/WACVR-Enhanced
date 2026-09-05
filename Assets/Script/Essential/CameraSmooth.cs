using UnityEngine;

[RequireComponent(typeof(Camera))]
public class CameraSmooth : MonoBehaviour
{
    public Transform target;
    public float smoothSpeed = 0.05f;
    public int FPS = 60;

    private float _nextPoseUpdateTime = 0f;

    private void Start()
    {
        // V0.1.3 CleanStable: spectator timing remains local only.
        // OpenXR/VDXR owns VR frame pacing. FPS is spectator-local only.
        _nextPoseUpdateTime = 0f;
    }

    public void SetSpectatorFPS(int fps)
    {
        FPS = Mathf.Clamp(fps, 1, 1000);
        _nextPoseUpdateTime = 0f;

    }

    private void Update()
    {
        if (target == null)
            return;

        float now = Time.unscaledTime;
        float interval = 1f / Mathf.Max(1, FPS);

        if (now < _nextPoseUpdateTime)
            return;

        // Do not run catch-up loops after stalls. One current pose is enough.
        _nextPoseUpdateTime = now + interval;

        transform.position = Vector3.Lerp(
            transform.position,
            target.position,
            smoothSpeed
        );

        transform.rotation = Quaternion.Lerp(
            transform.rotation,
            target.rotation,
            smoothSpeed
        );
    }
}
