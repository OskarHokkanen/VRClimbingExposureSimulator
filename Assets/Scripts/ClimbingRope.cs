using UnityEngine;
using UnityEngine.XR;
using System.Collections.Generic;

/// <summary>
/// Manages the OptimizedRopesAndCables RopeGenerator, driving its two
/// anchor transforms from the frustum calibration and the player's head.
/// </summary>
public class ClimbingRope : MonoBehaviour
{
    public enum AnchorMode { TopOfWall, BottomOfWall }

    [Header("References")]
    public Transform playerHead;
    public WallFrustumCalibrator frustumCalibrator;

    [Header("Rope Plugin")]
    [Tooltip("The GameObject that has the RopeGenerator component on it")]
    public GameObject ropeGeneratorObject;
    [Tooltip("The Transform assigned as the wall-end anchor in RopeGenerator")]
    public Transform wallAnchorTransform;
    [Tooltip("The Transform assigned as the harness-end anchor in RopeGenerator")]
    public Transform harnessTransform;

    [Header("Anchor Settings")]
    public AnchorMode anchorMode = AnchorMode.TopOfWall;
    [Tooltip("How far below the head the harness point sits")]
    public float harnessOffset = 0.5f;
    [Tooltip("Small offset so the anchor sits just in front of the wall surface")]
    public float anchorSurfaceOffset = 0.03f;

    [Header("Carabiner")]
    public GameObject carabinerPrefab;

    [Header("Controls")]
    public InputDeviceCharacteristics controllerHand =
        InputDeviceCharacteristics.Right | InputDeviceCharacteristics.Controller;

    // State
    private bool _isVisible;
    private GameObject _carabiner;
    private bool _gripPrev;
    private bool _thumbPrev;

    public bool IsVisible => _isVisible;

    // ────────────────────────────────────────────────────────────────────
    // Lifecycle
    // ────────────────────────────────────────────────────────────────────

    void Start()
    {
        // Start hidden
        SetVisible(false);

        if (carabinerPrefab != null)
        {
            _carabiner = Instantiate(carabinerPrefab);
            _carabiner.SetActive(false);
        }
    }

    void Update()
    {
        HandleInput();

        if (!_isVisible) return;
        if (playerHead == null) return;

        // Drive the harness transform to follow the player every frame
        if (harnessTransform != null)
            harnessTransform.position = playerHead.position - Vector3.up * harnessOffset;
    }

    // ────────────────────────────────────────────────────────────────────
    // Input
    // ────────────────────────────────────────────────────────────────────

    void HandleInput()
    {
        var devs = new List<InputDevice>();
        InputDevices.GetDevicesWithCharacteristics(controllerHand, devs);
        if (devs.Count == 0) return;
        var dev = devs[0];

        // Grip → toggle rope on/off
        dev.TryGetFeatureValue(CommonUsages.gripButton, out bool grip);
        if (grip && !_gripPrev)
            SetVisible(!_isVisible);
        _gripPrev = grip;

        // Thumbstick click → swap top/bottom anchor
        dev.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out bool thumb);
        if (thumb && !_thumbPrev)
            SetAnchorMode(anchorMode == AnchorMode.TopOfWall
                ? AnchorMode.BottomOfWall : AnchorMode.TopOfWall);
        _thumbPrev = thumb;
    }

    // ────────────────────────────────────────────────────────────────────
    // Anchor
    // ────────────────────────────────────────────────────────────────────

    void UpdateWallAnchor()
    {
        if (frustumCalibrator == null || !frustumCalibrator.IsCalibrated) return;
        if (wallAnchorTransform == null) return;

        CalibratedWall nearPlane = frustumCalibrator.GetNearPlaneAsWall();

        Vector3 center  = nearPlane.center;
        Vector3 up      = nearPlane.localUp;
        Vector3 outward = nearPlane.normal; // already faces toward climber

        float halfH = (frustumCalibrator.mode == WallFrustumCalibrator.FrustumMode.FlatWall
            ? frustumCalibrator.farHeight
            : frustumCalibrator.nearHeight) * 0.5f;

        Vector3 edge = anchorMode == AnchorMode.TopOfWall
            ? center + up *  halfH
            : center + up * -halfH;

        // Move the wall anchor transform — RopeGenerator reads it automatically
        wallAnchorTransform.position = edge + outward * anchorSurfaceOffset;
        wallAnchorTransform.rotation = Quaternion.LookRotation(outward, up);

        // Sync carabiner to the same spot
        if (_carabiner != null)
        {
            _carabiner.transform.position = wallAnchorTransform.position;
            _carabiner.transform.rotation = wallAnchorTransform.rotation;
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // Public API
    // ────────────────────────────────────────────────────────────────────

    public void SetVisible(bool visible)
    {
        _isVisible = visible;

        if (ropeGeneratorObject != null)
            ropeGeneratorObject.SetActive(visible);

        if (_carabiner != null)
            _carabiner.SetActive(visible);

        if (visible)
            UpdateWallAnchor();
    }

    public void SetAnchorMode(AnchorMode mode)
    {
        anchorMode = mode;
        UpdateWallAnchor();
    }
}