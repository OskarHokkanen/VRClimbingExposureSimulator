using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using TMPro;

/// <summary>
/// Manages Calibration ↔ Climbing mode transitions using
/// Unity XR Interaction Toolkit's XR Input Modality Manager.
///
/// The XR Input Modality Manager already handles switching between
/// controller models and hand models automatically. This script
/// hooks into its events and toggles calibration scripts on/off.
///
/// SETUP:
///   1. Find the XR Input Modality Manager on your XR rig
///   2. In its "Tracked Hand Mode Started" event, add ModeManager.OnHandTrackingStarted
///   3. In its "Controller Mode Started" event, add ModeManager.OnControllerModeStarted
///   4. Wire calibration script references in the inspector
///
/// WORKFLOW:
///   - App starts → controllers are active → calibration mode
///   - Operator calibrates walls, places holds, adjusts height
///   - Operator holds both grips for 1.5s → triggers switch
///   - Operator puts controllers down → Quest detects hands → 
///     XR Input Modality Manager fires "Tracked Hand Mode Started" →
///     ModeManager disables calibration, climber sees hands
///   - Operator picks up controller → "Controller Mode Started" →
///     ModeManager re-enables calibration
/// </summary>
public class ModeManager : MonoBehaviour
{
    [Header("Calibration Scripts (disabled in climbing mode)")]
    public SimpleWallSystem wallSystem;
    public HoldPlacementManager holdPlacementManager;
    public HeightController heightController;

    [Header("Always-Active Scripts")]
    public EnvironmentManager environmentManager;

    [Header("UI")]
    [Tooltip("World-space canvas or panel shown only during calibration")]
    public GameObject calibrationUI;

    public TextMeshProUGUI statusText;

    [Header("Manual Switch (optional)")]
    [Tooltip("Hold both grips this long to request climbing mode. " +
             "The actual switch happens when you put controllers down " +
             "and hand tracking activates.")]
    public float gripHoldDuration = 1.5f;

    // ── State ──
    public enum AppMode { Calibration, Climbing }
    public AppMode CurrentMode { get; private set; } = AppMode.Calibration;

    private float _gripHoldTimer;
    private bool _readyToSwitchToClimbing;

    // ────────────────────────────────────────────
    // Lifecycle
    // ────────────────────────────────────────────

    void Start()
    {
        ApplyCalibrationMode();
    }

    void Update()
    {
        if (CurrentMode == AppMode.Calibration)
            CheckGripHold();

        UpdateStatusText();
    }

    // ────────────────────────────────────────────
    // XR Input Modality Manager Event Callbacks
    // ────────────────────────────────────────────
    // Hook these up in the Inspector on the XR Input Modality Manager:
    //   "Tracked Hand Mode Started" → ModeManager.OnHandTrackingStarted()
    //   "Controller Mode Started"   → ModeManager.OnControllerModeStarted()

    /// <summary>
    /// Called by XR Input Modality Manager when hand tracking activates
    /// (user put down controllers, Quest sees hands).
    /// </summary>
    public void OnHandTrackingStarted()
    {
        Debug.Log("ModeManager: Hand tracking started");

        // Only switch to climbing if operator signalled readiness
        // OR if we're already in climbing mode (hands re-detected)
        if (_readyToSwitchToClimbing || CurrentMode == AppMode.Climbing)
        {
            ApplyClimbingMode();
            _readyToSwitchToClimbing = false;
        }
    }

    /// <summary>
    /// Called by XR Input Modality Manager when controllers are detected
    /// (user picked up a controller).
    /// </summary>
    public void OnControllerModeStarted()
    {
        Debug.Log("ModeManager: Controller mode started");

        // Always go back to calibration when controllers appear
        ApplyCalibrationMode();
    }

    // ────────────────────────────────────────────
    // Grip Hold Detection
    // ────────────────────────────────────────────

    /// <summary>
    /// While calibrating, holding both grips flags that we WANT
    /// to switch to climbing. The actual switch happens when the
    /// XR Input Modality Manager detects hand tracking.
    /// </summary>
    void CheckGripHold()
    {
        if (_readyToSwitchToClimbing) return; // already flagged

        bool leftGrip = false, rightGrip = false;

        var leftDev = GetDevice(InputDeviceCharacteristics.Left);
        var rightDev = GetDevice(InputDeviceCharacteristics.Right);

        if (leftDev.isValid)
            leftDev.TryGetFeatureValue(CommonUsages.gripButton, out leftGrip);
        if (rightDev.isValid)
            rightDev.TryGetFeatureValue(CommonUsages.gripButton, out rightGrip);

        if (leftGrip && rightGrip)
        {
            _gripHoldTimer += Time.deltaTime;
            if (_gripHoldTimer >= gripHoldDuration)
            {
                _readyToSwitchToClimbing = true;
                _gripHoldTimer = 0f;
                Debug.Log("ModeManager: Ready to switch — put controllers down");
            }
        }
        else
        {
            _gripHoldTimer = 0f;
        }
    }

    // ────────────────────────────────────────────
    // Mode Application
    // ────────────────────────────────────────────

    void ApplyCalibrationMode()
    {
        CurrentMode = AppMode.Calibration;
        _readyToSwitchToClimbing = false;

        SetEnabled(wallSystem, true);
        SetEnabled(holdPlacementManager, true);
        SetEnabled(heightController, true);

        SetActive(calibrationUI, true);

        Debug.Log("ModeManager: CALIBRATION mode active");
    }

    void ApplyClimbingMode()
    {
        CurrentMode = AppMode.Climbing;

        SetEnabled(wallSystem, false);
        SetEnabled(holdPlacementManager, false);
        SetEnabled(heightController, false);

        SetActive(calibrationUI, false);

        // Final rebuild of walls when entering climbing mode
        if (wallSystem != null && wallSystem.IsCalibrated)
            wallSystem.RebuildMeshes();

        Debug.Log("ModeManager: CLIMBING mode active");
    }

    /// <summary>
    /// Force switch to climbing mode from code (e.g. a UI button).
    /// If controllers are still active, flags ready and waits for hands.
    /// </summary>
    public void RequestClimbingMode()
    {
        _readyToSwitchToClimbing = true;
    }

    /// <summary>
    /// Force switch to calibration mode from code.
    /// </summary>
    public void RequestCalibrationMode()
    {
        ApplyCalibrationMode();
    }

    // ────────────────────────────────────────────
    // Status
    // ────────────────────────────────────────────

    void UpdateStatusText()
    {
        if (statusText == null) return;

        if (CurrentMode == AppMode.Calibration)
        {
            if (_readyToSwitchToClimbing)
            {
                statusText.text = "READY — Put controllers down to start climbing";
            }
            else
            {
                float progress = _gripHoldTimer / gripHoldDuration;
                string hint = progress > 0.1f
                    ? $"Switching... {progress * 100:F0}%"
                    : "Hold BOTH GRIPS to enter climbing mode";
                statusText.text = $"CALIBRATION MODE\n{hint}";
            }
        }
        else
        {
            statusText.text = "";
        }
    }

    // ────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────

    void SetEnabled(MonoBehaviour comp, bool on)
    {
        if (comp != null) comp.enabled = on;
    }

    void SetActive(GameObject obj, bool on)
    {
        if (obj != null) obj.SetActive(on);
    }

    InputDevice GetDevice(InputDeviceCharacteristics side)
    {
        var flags = side | InputDeviceCharacteristics.Controller;
        var devices = new List<InputDevice>();
        InputDevices.GetDevicesWithCharacteristics(flags, devices);
        return devices.Count > 0 ? devices[0] : default;
    }
}