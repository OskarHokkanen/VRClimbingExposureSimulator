using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using TMPro;
public class HeightController : MonoBehaviour
{
    [Header("References")]
    public EnvironmentManager environmentManager;
    public SimpleWallSystem wallSystem;

    [Header("Controls")]
    [Tooltip("Which hand adjusts height")]
    public SimpleWallSystem.ControllerHand controlHand =
        SimpleWallSystem.ControllerHand.Left;

    [Tooltip("Meters per second at full stick deflection")]
    public float adjustSpeed = 5f;

    [Tooltip("Dead zone to avoid accidental adjustments")]
    public float deadZone = 0.3f;

    [Header("UI")]
    public TextMeshProUGUI heightStatusText;

    private int _lastWallCount = 0;
    private bool _needsRebuild = false;

    void Update()
    {
        // Detect when new walls are finalized and rebuild
        if (wallSystem != null)
        {
            int wallCount = wallSystem.Walls.Count;
            if (wallCount != _lastWallCount && wallCount > 0)
            {
                _lastWallCount = wallCount;
                _needsRebuild = true;
            }
        }

        // Rebuild on next frame after wall count changes
        if (_needsRebuild)
        {
            _needsRebuild = false;
            Rebuild();
        }

        // Read thumbstick for height adjustment
        InputDevice device = GetDevice();
        if (!device.isValid) return;

        device.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 stick);

        if (Mathf.Abs(stick.y) > deadZone)
        {
            float input = (stick.y - Mathf.Sign(stick.y) * deadZone) / (1f - deadZone);
            environmentManager.AdjustWallHeight(input * adjustSpeed * Time.deltaTime);
            Rebuild();
        }

        UpdateUI();
    }

    /// <summary>
    /// Rebuild wall meshes.
    /// </summary>
    public void Rebuild()
    {
        if (wallSystem != null)
            wallSystem.RebuildMeshes();
    }

    /// <summary>
    /// Force a rebuild — call from a UI button if needed.
    /// </summary>
    public void ForceRebuild()
    {
        Rebuild();
    }

    void UpdateUI()
    {
        if (heightStatusText == null) return;

        float h = environmentManager != null ? environmentManager.wallHeight : 0;
        string hand = controlHand == SimpleWallSystem.ControllerHand.Left
            ? "LEFT" : "RIGHT";
        heightStatusText.text = $"Height: {h:F1}m  |  {hand} Stick ↕ to adjust";
    }

    InputDevice GetDevice()
    {
        var flags = (controlHand == SimpleWallSystem.ControllerHand.Left)
            ? InputDeviceCharacteristics.Left | InputDeviceCharacteristics.Controller
            : InputDeviceCharacteristics.Right | InputDeviceCharacteristics.Controller;
        var devices = new List<InputDevice>();
        InputDevices.GetDevicesWithCharacteristics(flags, devices);
        return devices.Count > 0 ? devices[0] : default;
    }
}