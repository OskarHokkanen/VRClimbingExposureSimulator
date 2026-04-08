using UnityEngine;
using UnityEngine.XR;
using System.Collections.Generic;

public class ViewRecenter : MonoBehaviour
{
    [Header("References")]
    public Transform xrOrigin;
    public Transform headTransform;
    public SimpleWallSystem wallSystem;

    [Header("Settings")]
    public Vector3 targetForward = Vector3.forward;
    public bool recenterOnStart = true;

    [Tooltip("Automatically recenter when someone puts the headset on")]
    public bool recenterOnWear = true;

    [Tooltip("Delay after headset is worn before recentering (seconds). " +
             "Gives tracking time to stabilize.")]
    public float wearDelay = 1.2f;

    private bool _wasWorn = false;
    private bool _pendingRecenter = false;
    private float _wearTimer = 0f;

    void Start()
    {
        if (recenterOnStart)
            Invoke(nameof(Recenter), 0.5f);
    }

    void Update()
    {
        if (!recenterOnWear) return;

        // Get the HMD device
        var hmds = new List<InputDevice>();
        InputDevices.GetDevicesWithCharacteristics(
            InputDeviceCharacteristics.HeadMounted, hmds);

        if (hmds.Count == 0) return;

        hmds[0].TryGetFeatureValue(CommonUsages.userPresence, out bool isWorn);

        // Trigger on the rising edge — headset just put on
        if (isWorn && !_wasWorn)
        {
            _pendingRecenter = true;
            _wearTimer = 0f;
        }

        // Wait for tracking to stabilize before recentering
        if (_pendingRecenter)
        {
            _wearTimer += Time.deltaTime;
            if (_wearTimer >= wearDelay)
            {
                _pendingRecenter = false;
                Recenter();
            }
        }

        _wasWorn = isWorn;
    }

    public void Recenter()
    {
        if (xrOrigin == null || headTransform == null) return;

        Vector3 headForward = headTransform.forward;
        headForward.y = 0;
        headForward.Normalize();
        if (headForward.sqrMagnitude < 0.001f) return;

        Vector3 desired = targetForward;
        desired.y = 0;
        desired.Normalize();
        if (desired.sqrMagnitude < 0.001f) desired = Vector3.forward;

        float currentYaw = Mathf.Atan2(headForward.x, headForward.z) * Mathf.Rad2Deg;
        float desiredYaw  = Mathf.Atan2(desired.x,    desired.z)    * Mathf.Rad2Deg;
        float rotationNeeded = desiredYaw - currentYaw;

        Vector3 pivot = headTransform.position;
        pivot.y = xrOrigin.position.y;
        xrOrigin.RotateAround(pivot, Vector3.up, rotationNeeded);

        Debug.Log($"ViewRecenter: rotated {rotationNeeded:F1}° to face {desired}");
    }

    public void RecenterTowardWall()
    {
        if (wallSystem == null || !wallSystem.IsCalibrated) return;

        Vector3 toCorner = wallSystem.CornerPoint - headTransform.position;
        toCorner.y = 0;
        if (toCorner.sqrMagnitude < 0.01f) return;

        targetForward = toCorner.normalized;
        Recenter();
    }

    public void RecenterToDirection(Vector3 worldDirection)
    {
        targetForward = worldDirection;
        Recenter();
    }
}