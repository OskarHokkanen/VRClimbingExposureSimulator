using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

public class HoldPlacementManager : MonoBehaviour
{
    [Header("References")]
    public SimpleWallSystem wallSystem;
    public Transform leftController;
    public WallFrustumCalibrator frustumCalibrator;

    [Header("Hold Library")]
    public HoldLibrary holdLibrary;
    public int activeHoldIndex = 0;

    [Header("Settings")]
    public float maxDistance = 0.4f;
    public float wallOffset  = 0.02f;
    public float heightOffset = 0.00f;
    public float holdScale   = 1f;
    [Tooltip("Positive = up, negative = down.")]
    public float holdVerticalOffset = .05f;

    // ── State ──────────────────────────────────────────────────────────
    private List<GameObject> _holds = new List<GameObject>();
    private bool _triggerPrev;
    private bool _thumbPrev;

    public int HoldCount => _holds.Count;

    public HoldDefinition ActiveHold =>
        holdLibrary != null && holdLibrary.holds.Count > 0
            ? holdLibrary.holds[Mathf.Clamp(activeHoldIndex, 0,
                holdLibrary.holds.Count - 1)]
            : null;

    // ────────────────────────────────────────────────────────────────────
    // Public API
    // ────────────────────────────────────────────────────────────────────

    public void SetActiveHold(int index)
    {
        if (holdLibrary == null) return;
        activeHoldIndex = Mathf.Clamp(index, 0, holdLibrary.holds.Count - 1);
        Debug.Log($"HoldPlacement: Active hold set to '{ActiveHold?.name}'");
    }

    public void ClearAll()
    {
        foreach (var h in _holds) if (h != null) Destroy(h);
        _holds.Clear();
    }

    // ────────────────────────────────────────────────────────────────────
    // Update
    // ────────────────────────────────────────────────────────────────────

    void Update()
    {
        bool wallsReady   = wallSystem != null
                            && wallSystem.Walls.Count > 0
                            && wallSystem.CurrentPhase == SimpleWallSystem.Phase.Done;
        bool frustumReady = frustumCalibrator != null
                            && frustumCalibrator.IsCalibrated;

        if (!wallsReady && !frustumReady) return;
        if (leftController == null)
        {
            Debug.LogWarning("HoldPlacement: leftController not assigned.");
            return;
        }

        InputDevice dev = GetDevice();
        if (!dev.isValid) return;

        dev.TryGetFeatureValue(CommonUsages.triggerButton,      out bool trigger);
        dev.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out bool thumb);

        if (trigger && !_triggerPrev)
            TryPlace();

        // Thumbstick click → undo last hold
        if (thumb && !_thumbPrev && _holds.Count > 0)
        {
            Destroy(_holds[_holds.Count - 1]);
            _holds.RemoveAt(_holds.Count - 1);
        }

        _triggerPrev = trigger;
        _thumbPrev   = thumb;
    }

    // ────────────────────────────────────────────────────────────────────
    // Placement
    // ────────────────────────────────────────────────────────────────────

    void TryPlace()
    {
        if (holdLibrary == null || holdLibrary.holds.Count == 0)
        {
            Debug.LogWarning("HoldPlacement: No HoldLibrary assigned or library is empty.");
            return;
        }

        Vector3 controllerPos = leftController.position;
        controllerPos.y += heightOffset;
        controllerPos.y += holdVerticalOffset;

        var walls = new List<CalibratedWall>();
        if (wallSystem != null && wallSystem.Walls.Count > 0)
            walls.AddRange(wallSystem.Walls);
        if (frustumCalibrator != null && frustumCalibrator.IsCalibrated)
            walls.Add(frustumCalibrator.GetNearPlaneAsWall());

        if (walls.Count == 0)
        {
            Debug.LogWarning("HoldPlacement: No walls available.");
            return;
        }

        float bestDist = float.MaxValue;
        int   bestWall = -1;

        for (int i = 0; i < walls.Count; i++)
        {
            float dist = Mathf.Abs(walls[i].SignedDistanceToPoint(controllerPos));
            Debug.Log($"HoldPlacement: Wall {i} distance = {dist:F3}m (max={maxDistance}m)");
            if (dist < bestDist && dist < maxDistance)
            {
                bestDist = dist;
                bestWall = i;
            }
        }

        if (bestWall < 0)
        {
            Debug.LogWarning($"HoldPlacement: No wall within {maxDistance}m. " +
                             $"Closest was {bestDist:F3}m.");
            return;
        }

        var    wall    = walls[bestWall];
        Vector3 onWall  = wall.ProjectPointOntoWall(controllerPos);
        Vector3 holdPos = onWall + wall.normal * wallOffset;
        Quaternion holdRot = Quaternion.LookRotation(wall.normal, wall.localUp);

        HoldDefinition def = ActiveHold;
        GameObject obj;

        if (def?.prefab != null)
        {
            // Spawn at origin first so bounds are clean
            obj = Instantiate(def.prefab, holdPos, holdRot);
            obj.transform.localScale = Vector3.one * holdScale * def.scale;

            // Use actual rendered bounds to re-center the hold on the press point.
            // This corrects for pivot offset regardless of prefab setup or scale.
            var rend = obj.GetComponentInChildren<Renderer>();
            if (rend != null)
            {
                // Find where the bounds center currently sits relative to holdPos
                Vector3 boundsCenter = rend.bounds.center;

                // Offset along the wall's up and right axes to align visual center
                // with the press point — preserve the normal-direction offset
                Vector3 correction = boundsCenter - holdPos;

                // Remove the normal component (we want to keep depth placement)
                // and only correct lateral/vertical drift caused by pivot offset
                Vector3 normalComponent = Vector3.Project(correction, wall.normal);
                Vector3 pivotDrift      = correction - normalComponent;

                obj.transform.position -= pivotDrift;

                // Apply color tint
                var mat = new Material(rend.sharedMaterial);
                mat.color = def.color;
                rend.material = mat;
            }
        }
        else
        {
            float radius = 0.06f * holdScale * (def?.scale ?? 1f);
            obj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            obj.transform.position   = holdPos;
            obj.transform.rotation   = holdRot;
            obj.transform.localScale = Vector3.one * radius;
            Destroy(obj.GetComponent<Collider>());

            var mat = new Material(
                Shader.Find("Universal Render Pipeline/Lit") ??
                Shader.Find("Standard"));
            mat.color = def?.color ?? Color.red;
            obj.GetComponent<Renderer>().material = mat;
        }

        obj.name = $"Hold_{def?.name ?? "Default"}_{_holds.Count}";
        _holds.Add(obj);
    }

    // ────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────

    InputDevice GetDevice()
    {
        var flags = InputDeviceCharacteristics.Left
                  | InputDeviceCharacteristics.Controller;
        var devs = new List<InputDevice>();
        InputDevices.GetDevicesWithCharacteristics(flags, devs);
        return devs.Count > 0 ? devs[0] : default;
    }
}