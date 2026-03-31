using UnityEngine;

/// <summary>
/// Rotates the XR Origin so the player always faces a specific virtual
/// direction, regardless of their physical orientation in the room.
///
/// HOW IT WORKS:
///   When Recenter() is called, it measures the player's current head yaw
///   (horizontal look direction) and rotates the XR Origin so that yaw
///   maps to the desired virtual forward direction.
///
///   Example: if targetForward is (0,0,1) (north) and the player is
///   physically facing east, the XR Origin rotates so that east = north
///   in the virtual world.
///
/// USAGE:
///   - Call Recenter() at app start, or from the phone UI, or from a button
///   - Set targetForward to the direction you want the player to face
///     (e.g., toward the climbing wall)
///   - Call RecenterTowardWall() after calibration to auto-face the wall corner
///
/// SETUP:
///   - Assign xrOrigin (the root XR Origin / OVRCameraRig)
///   - Assign headTransform (the Main Camera under Camera Offset)
/// </summary>
public class ViewRecenter : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The XR Origin root (the object we rotate)")]
    public Transform xrOrigin;

    [Tooltip("The Main Camera / head transform")]
    public Transform headTransform;

    [Tooltip("Optional: SimpleWallSystem for auto-facing the wall")]
    public SimpleWallSystem wallSystem;

    [Header("Settings")]
    [Tooltip("The virtual world direction the player should face after recentering. " +
             "Only the horizontal (XZ) component matters.")]
    public Vector3 targetForward = Vector3.forward; // default: face +Z

    [Tooltip("Recenter automatically when the app starts")]
    public bool recenterOnStart = true;

    void Start()
    {
        if (recenterOnStart)
        {
            // Small delay so tracking has time to initialize
            Invoke(nameof(Recenter), 0.5f);
        }
    }

    /// <summary>
    /// Rotate the XR Origin so the player's current look direction
    /// maps to targetForward in the virtual world.
    /// </summary>
    public void Recenter()
    {
        if (xrOrigin == null || headTransform == null) return;

        // Get the player's current horizontal look direction
        Vector3 headForward = headTransform.forward;
        headForward.y = 0;
        headForward.Normalize();

        if (headForward.sqrMagnitude < 0.001f) return;

        // Get the desired horizontal forward
        Vector3 desired = targetForward;
        desired.y = 0;
        desired.Normalize();

        if (desired.sqrMagnitude < 0.001f) desired = Vector3.forward;

        // Calculate the yaw difference
        float currentYaw = Mathf.Atan2(headForward.x, headForward.z) * Mathf.Rad2Deg;
        float desiredYaw = Mathf.Atan2(desired.x, desired.z) * Mathf.Rad2Deg;
        float rotationNeeded = desiredYaw - currentYaw;

        // Rotate the XR Origin around the player's current position
        // (so the player doesn't move, only the world rotates around them)
        Vector3 pivot = headTransform.position;
        pivot.y = xrOrigin.position.y; // keep vertical position

        xrOrigin.RotateAround(pivot, Vector3.up, rotationNeeded);

        Debug.Log($"ViewRecenter: rotated {rotationNeeded:F1}° to face {desired}");
    }

    /// <summary>
    /// Recenter so the player faces toward the wall corner.
    /// Call after walls are calibrated.
    /// </summary>
    public void RecenterTowardWall()
    {
        if (wallSystem == null || !wallSystem.IsCalibrated) return;

        // Direction from player to the corner point
        Vector3 toCorner = wallSystem.CornerPoint - headTransform.position;
        toCorner.y = 0;

        if (toCorner.sqrMagnitude < 0.01f) return;

        targetForward = toCorner.normalized;
        Recenter();
    }

    /// <summary>
    /// Set the target direction and recenter immediately.
    /// </summary>
    public void RecenterToDirection(Vector3 worldDirection)
    {
        targetForward = worldDirection;
        Recenter();
    }
}