using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using TMPro;

/// <summary>
/// Switches between different environment presets.
/// Each environment is a root GameObject containing all its scenery
/// as children. Switching activates one and deactivates the rest.
///
/// The active environment's root is automatically assigned to
/// EnvironmentManager.environmentRoot so it moves with wall height.
///
/// SETUP:
///   1. Create your environments as root GameObjects in the scene
///      (e.g., "ForestEnvironment", "CityEnvironment", "CanyonEnvironment")
///   2. Put all scenery as children under each root, designed with Y=0 as ground
///   3. Drag all environment roots into the 'environments' list on this component
///   4. They'll be deactivated at start, and the first one is activated by default
///
/// RUNTIME SWITCHING:
///   - Left controller thumbstick LEFT/RIGHT to cycle environments
///     (only in calibration mode, disabled in climbing mode)
///   - Or call SwitchTo(index) / Next() / Previous() from code
/// </summary>
public class EnvironmentSwitcher : MonoBehaviour
{
    [Header("References")]
    public EnvironmentManager environmentManager;

    [Header("Environments")]
    [Tooltip("List of environment root GameObjects. First one is active by default.")]
    public List<EnvironmentPreset> environments = new List<EnvironmentPreset>();

    [Header("Controls")]
    [Tooltip("Which hand cycles environments")]
    public WallCalibrationManager.ControllerHand controlHand =
        WallCalibrationManager.ControllerHand.Left;

    [Tooltip("Dead zone for thumbstick horizontal input")]
    public float deadZone = 0.7f;

    [Header("UI")]
    public TextMeshProUGUI environmentStatusText;

    // ── State ──
    private int _activeIndex = -1;
    private bool _stickWasLeft;
    private bool _stickWasRight;

    /// <summary>Currently active environment index. -1 if none.</summary>
    public int ActiveIndex => _activeIndex;

    /// <summary>Currently active environment preset, or null.</summary>
    public EnvironmentPreset ActiveEnvironment =>
        (_activeIndex >= 0 && _activeIndex < environments.Count)
            ? environments[_activeIndex]
            : null;

    // ────────────────────────────────────────────
    // Lifecycle
    // ────────────────────────────────────────────

    void Start()
    {
        // Deactivate all environments
        for (int i = 0; i < environments.Count; i++)
        {
            if (environments[i].root != null)
                environments[i].root.SetActive(false);
        }

        // Activate the first one
        if (environments.Count > 0)
            SwitchTo(0);
    }

    void Update()
    {
        HandleInput();
        UpdateStatusText();
    }

    // ────────────────────────────────────────────
    // Switching
    // ────────────────────────────────────────────

    /// <summary>
    /// Switch to a specific environment by index.
    /// </summary>
    public void SwitchTo(int index)
    {
        if (environments.Count == 0) return;
        index = Mathf.Clamp(index, 0, environments.Count - 1);

        // Deactivate current
        if (_activeIndex >= 0 && _activeIndex < environments.Count)
        {
            var current = environments[_activeIndex];
            if (current.root != null)
                current.root.SetActive(false);

            // Deactivate any extra objects for this environment
            foreach (var obj in current.extraObjectsToActivate)
                if (obj != null) obj.SetActive(false);
        }

        // Activate new
        _activeIndex = index;
        var next = environments[_activeIndex];

        if (next.root != null)
        {
            next.root.SetActive(true);

            // Tell EnvironmentManager to position this environment
            if (environmentManager != null)
                environmentManager.environmentRoot = next.root.transform;
        }

        // Activate extra objects for this environment
        foreach (var obj in next.extraObjectsToActivate)
            if (obj != null) obj.SetActive(true);

        // Deactivate objects that should be hidden for this environment
        foreach (var obj in next.objectsToDeactivate)
            if (obj != null) obj.SetActive(false);

        Debug.Log($"Environment switched to: {next.displayName} ({_activeIndex})");
    }

    /// <summary>Cycle to the next environment.</summary>
    public void Next()
    {
        if (environments.Count == 0) return;
        SwitchTo((_activeIndex + 1) % environments.Count);
    }

    /// <summary>Cycle to the previous environment.</summary>
    public void Previous()
    {
        if (environments.Count == 0) return;
        SwitchTo((_activeIndex - 1 + environments.Count) % environments.Count);
    }

    /// <summary>Switch to an environment by name.</summary>
    public void SwitchTo(string name)
    {
        for (int i = 0; i < environments.Count; i++)
        {
            if (environments[i].displayName == name)
            {
                SwitchTo(i);
                return;
            }
        }
        Debug.LogWarning($"Environment '{name}' not found.");
    }

    // ────────────────────────────────────────────
    // Input
    // ────────────────────────────────────────────

    void HandleInput()
    {
        InputDevice device = GetDevice();
        if (!device.isValid) return;

        device.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 stick);

        bool left = stick.x < -deadZone;
        bool right = stick.x > deadZone;

        if (left && !_stickWasLeft)
            Previous();
        if (right && !_stickWasRight)
            Next();

        _stickWasLeft = left;
        _stickWasRight = right;
    }

    // ────────────────────────────────────────────
    // Status
    // ────────────────────────────────────────────

    void UpdateStatusText()
    {
        if (environmentStatusText == null) return;

        if (ActiveEnvironment != null)
        {
            environmentStatusText.text =
                $"Environment: {ActiveEnvironment.displayName} " +
                $"({_activeIndex + 1}/{environments.Count})  |  Stick ←→";
        }
    }

    // ────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────

    InputDevice GetDevice()
    {
        var flags = (controlHand == WallCalibrationManager.ControllerHand.Left)
            ? InputDeviceCharacteristics.Left | InputDeviceCharacteristics.Controller
            : InputDeviceCharacteristics.Right | InputDeviceCharacteristics.Controller;
        var devices = new List<InputDevice>();
        InputDevices.GetDevicesWithCharacteristics(flags, devices);
        return devices.Count > 0 ? devices[0] : default;
    }
}

/// <summary>
/// Defines one environment preset with its root object
/// and any additional objects to toggle.
/// </summary>
[System.Serializable]
public class EnvironmentPreset
{
    [Tooltip("Name shown in the UI")]
    public string displayName = "Environment";

    [Tooltip("Root GameObject containing all scenery for this environment. " +
             "Design children with Y=0 as ground level.")]
    public GameObject root;

    [Tooltip("Extra GameObjects to ACTIVATE when this environment is selected " +
             "(e.g., lighting rigs, particle systems, audio sources not under root)")]
    public List<GameObject> extraObjectsToActivate = new List<GameObject>();

    [Tooltip("GameObjects to DEACTIVATE when this environment is selected " +
             "(e.g., hide the default skybox, disable conflicting lights)")]
    public List<GameObject> objectsToDeactivate = new List<GameObject>();
}