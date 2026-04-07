using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
public class HoldPlacementManager : MonoBehaviour
{
    [Header("Frustum (optional)")]
    [Tooltip("If assigned, holds can also be placed on the frustum's near plane")]
    public WallFrustumCalibrator frustumCalibrator;
    
    [Header("References")]
    public SimpleWallSystem wallSystem;
    public Transform rightController;

    [Header("Hold Prefab")]
    [Tooltip("The hold model to place. If empty, a small sphere is created.")]
    public GameObject holdPrefab;

    [Tooltip("Scale applied to the prefab")]
    public float holdScale = 1f;

    [Header("Settings")]
    [Tooltip("Max distance from wall plane to accept placement (meters)")]
    public float maxDistance = 0.4f;

    [Tooltip("Offset from wall surface toward the climber (meters)")]
    public float wallOffset = 0.02f;

    [Tooltip("Height offset to compensate for the controller's tracked position " +
             "being above the contact point (meters). Negative = place lower.")]
    public float heightOffset = -0.05f;

    // ── State ──
    private List<GameObject> _holds = new List<GameObject>();
    private bool _triggerPrev;
    private bool _thumbPrev;

    public int HoldCount => _holds.Count;

    void Update()
    {
        // bool wallsReady = wallSystem != null && wallSystem.Walls.Count > 0
        //                                      && wallSystem.CurrentPhase == SimpleWallSystem.Phase.Done;
        bool frustumReady = frustumCalibrator != null && frustumCalibrator.IsCalibrated;

        // if (!wallsReady && !frustumReady) return;
        
        // if (wallSystem == null || wallSystem.Walls.Count == 0)
        //     return;
        // if (wallSystem.CurrentPhase != SimpleWallSystem.Phase.Done)
        //     return;
        if (rightController == null)
            return;

        InputDevice dev = GetDevice();
        if (!dev.isValid) return;

        dev.TryGetFeatureValue(CommonUsages.triggerButton, out bool trigger);
        dev.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out bool thumb);

        // Trigger → place
        if (trigger && !_triggerPrev)
            TryPlace();

        // Thumbstick click → undo
        if (thumb && !_thumbPrev && _holds.Count > 0)
        {
            Destroy(_holds[_holds.Count - 1]);
            _holds.RemoveAt(_holds.Count - 1);
        }

        _triggerPrev = trigger;
        _thumbPrev = thumb;
    }

    void TryPlace()
    {
        // Offset to compensate for tracking point vs contact point
        Vector3 controllerPos = rightController.position;
        controllerPos.y += heightOffset;

        // // Find nearest wall
        // float bestDist = float.MaxValue;
        // int bestWall = -1;
        //
        // var walls = wallSystem.Walls;
        // for (int i = 0; i < walls.Count; i++)
        // {
        //     float dist = Mathf.Abs(walls[i].SignedDistanceToPoint(controllerPos));
        //     if (dist < bestDist && dist < maxDistance)
        //     {
        //         bestDist = dist;
        //         bestWall = i;
        //     }
        // }
        //
        // if (bestWall < 0) return;
        //
        // var wall = walls[bestWall];

        // Collect walls from both sources
        
        // Frustum start
        var walls = new List<CalibratedWall>(wallSystem != null ? wallSystem.Walls : new List<CalibratedWall>());
        if (frustumCalibrator != null && frustumCalibrator.IsCalibrated)
            walls.Add(frustumCalibrator.GetNearPlaneAsWall());

        if (walls.Count == 0)
        {
            Debug.LogWarning("Holdplacement: No walls available.");
            return;
        } 
            

        float bestDist = float.MaxValue;
        int bestWall = -1;

        for (int i = 0; i < walls.Count; i++)
        {
            float dist = Mathf.Abs(walls[i].SignedDistanceToPoint(controllerPos));
            if (dist < bestDist && dist < maxDistance)
            {
                bestDist = dist;
                bestWall = i;
            }
        }

        if (bestWall < 0)
        {
            Debug.LogWarning($"HoldPlacement: No wall within maxDistance ({maxDistance}m). " +
                             $"Best was {bestDist:F3}m. Try increasing maxDistance.");
            return;
        }

        var wall = walls[bestWall];
        
        
        // Frustum end
        
        
        // Project controller onto wall, then offset slightly outward
        Vector3 onWall = wall.ProjectPointOntoWall(controllerPos);
        Vector3 holdPos = onWall + wall.normal * wallOffset;

        // Rotation: face outward from wall
        Quaternion holdRot = Quaternion.LookRotation(wall.normal, wall.localUp);

        // Create
        GameObject obj;
        if (holdPrefab != null)
        {
            obj = Instantiate(holdPrefab, holdPos, holdRot);
        }
        else
        {
            obj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            obj.transform.position = holdPos;
            obj.transform.rotation = holdRot;
            obj.transform.localScale = Vector3.one * 0.06f;
            Destroy(obj.GetComponent<Collider>());
            obj.GetComponent<Renderer>().material.color = Color.red;
        }

        obj.transform.localScale *= holdScale;
        obj.name = $"Hold_{_holds.Count}";
        _holds.Add(obj);
        Debug.Log($"Hold_{_holds.Count}");
    }

    /// <summary>Remove all placed holds.</summary>
    public void ClearAll()
    {
        foreach (var h in _holds) if (h != null) Destroy(h);
        _holds.Clear();
    }

    InputDevice GetDevice()
    {
        var flags = InputDeviceCharacteristics.Left | InputDeviceCharacteristics.Controller;
        var devs = new List<InputDevice>();
        InputDevices.GetDevicesWithCharacteristics(flags, devs);
        Debug.Log("Devices found: " + devs.Count);
        return devs.Count > 0 ? devs[0] : default;
    }
}