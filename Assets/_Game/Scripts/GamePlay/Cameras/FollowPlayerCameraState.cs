using UnityEngine;

[CreateAssetMenu(fileName = "FollowPlayerState", menuName = "Camera/Follow Player State")]
public class FollowPlayerCameraState : CameraState
{
    [Header("Follow Settings")]
    public Transform playerTransform;
    public Vector3 offset = new Vector3(0, 5, -8);
    public Vector3 rotationOffset = new Vector3(0, 0, 0);
    public float followSpeedX = 5f;
    public float followSpeed = 5f;
    public float rotationSpeed = 5f;

    private Vector3 _cachedRotationOffset = new Vector3(float.NaN, float.NaN, float.NaN);
    private Quaternion _cachedTargetRotation;

    private void Reset()
    {
        CameraStateName = CameraFollow.CameraStateName.FollowPlayer;
    }

    public override void OnEnter(CameraFollow cameraFollow, CameraFollow.TransitionMode transitionMode)
    {
        if (playerTransform == null)
        {
            Debug.LogWarning($"[{CameraStateName}] Player transform not set!");
        }
    }

    public override void OnUpdate(CameraFollow cameraFollow)
    {
        if (playerTransform == null)
        {
            // Debug.LogWarning($"[{CameraStateName}] Player transform is null in OnUpdate!"); // Commented out to avoid spam, but uncomment if needed
            return;
        }

        Camera camera = cameraFollow.GetCamera();
        if (camera == null)
            return;

        // Follow player position with offset
        Vector3 currentPosition = camera.transform.position;
        Vector3 desiredPosition = playerTransform.position + offset;

        // Smooth interpolation with easing (matching CameraFollow transition style)
        // Different speeds for X axis vs Y/Z axes
        float dt = Time.deltaTime;
        float tX = dt * followSpeedX;
        float tYZ = dt * followSpeed;
        tX = Mathf.Clamp01(tX);
        tYZ = Mathf.Clamp01(tYZ);

        // Apply OutQuart easing (same as CameraFollow transitions)
        float easedTX = EaseOutQuart(tX);
        float easedTYZ = EaseOutQuart(tYZ);

        Vector3 newPosition = new Vector3(
            Mathf.Lerp(currentPosition.x, desiredPosition.x, easedTX),
            Mathf.Lerp(currentPosition.y, desiredPosition.y, easedTYZ),
            Mathf.Lerp(currentPosition.z, desiredPosition.z, easedTYZ)
        );
        camera.transform.position = newPosition;

        // Apply rotation
        Quaternion targetRotation = GetTargetRotationCached();
        Quaternion currentRotation = camera.transform.rotation;
        if (Mathf.Abs(Quaternion.Dot(currentRotation, targetRotation)) < 0.999999f)
        {
            float rotationT = Mathf.Clamp01(dt * rotationSpeed);
            camera.transform.rotation = Quaternion.Slerp(currentRotation, targetRotation, EaseOutQuart(rotationT));
        }
    }

    public override void OnExit(CameraFollow cameraFollow)
    {
        // Cleanup when exiting state
    }

    public override Vector3 GetTargetPosition(CameraFollow cameraFollow)
    {
        if (playerTransform == null)
        {
            Camera camera = cameraFollow.GetCamera();
            return camera != null ? camera.transform.position : cameraFollow.transform.position;
        }

        return playerTransform.position + offset;
    }

    public override Quaternion GetTargetRotation(CameraFollow cameraFollow)
    {
        return GetTargetRotationCached();
    }

    private Quaternion GetTargetRotationCached()
    {
        if (_cachedRotationOffset != rotationOffset)
        {
            _cachedRotationOffset = rotationOffset;
            _cachedTargetRotation = Quaternion.Euler(rotationOffset);
        }

        return _cachedTargetRotation;
    }

    private static float EaseOutQuart(float value)
    {
        float inverse = 1f - value;
        float squared = inverse * inverse;
        return 1f - squared * squared;
    }

    public void SetPlayerTransform(Transform player)
    {

        playerTransform = player;
    }
}
