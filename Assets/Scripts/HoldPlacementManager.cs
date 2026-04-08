using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

public class HoldPlacementManager : MonoBehaviour
{
    [Header("References")]
    public SimpleWallSystem wallSystem;
    public Transform rightController;

    [Header("Hold Prefab")]
    [Tooltip("The hold model to place. If empty, a small sphere is created.")]
    public GameObject holdPrefab;

    [Tooltip("Scale applied to the prefab")]
    public float holdScale = 1f;

    [Header("Manual Placement Settings")]
    [Tooltip("Max distance from wall plane to accept placement (meters)")]
    public float maxDistance = 0.4f;

    [Tooltip("Offset from wall surface toward the climber (meters)")]
    public float wallOffset = 0.02f;

    [Tooltip("Height offset to compensate for controller tracking position (meters)")]
    public float heightOffset = -0.05f;

    [Tooltip("Holds per vertical meter of wall height")]
    public float holdsPerMeter = 1.0f;

    [Tooltip("Minimum vertical spacing between holds on the same wall (meters)")]
    public float minVerticalSpacing = 0.6f;

    [Tooltip("Minimum horizontal spacing between holds on the same wall (meters)")]
    public float minHorizontalSpacing = 0.6f;

    [Tooltip("Margin inset from wall edges so holds don't spawn right on the border (meters)")]
    public float edgeMargin = 0.15f;

    [Tooltip("Reference to the player's head/camera transform")]
    public Transform playerHead;

    [Tooltip("How far below the player's feet holds will spawn (meters)")]
    public float spawnBelowFeet = 0.5f;
    
    // Hold colors — Red, Blue, Green
    private static readonly Color[] HoldColors = new Color[]
    {
        new Color(0.85f, 0.15f, 0.15f), // Red
        new Color(0.15f, 0.35f, 0.90f), // Blue
        new Color(0.15f, 0.75f, 0.25f), // Green
    };

    // ── State ──
    private List<GameObject> _holds = new List<GameObject>();
    private bool _triggerPrev;
    private bool _thumbPrev;

    public int HoldCount => _holds.Count;

    void Update()
    {
        if (wallSystem == null || wallSystem.Walls.Count == 0) return;
        if (wallSystem.CurrentPhase != SimpleWallSystem.Phase.Done) return;
        if (rightController == null) return;

        InputDevice dev = GetDevice();
        if (!dev.isValid) return;

        dev.TryGetFeatureValue(CommonUsages.triggerButton, out bool trigger);
        dev.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out bool thumb);

        // Trigger → manual place
        if (trigger && !_triggerPrev)
            TryPlace();

        // Thumbstick click → undo last hold
        if (thumb && !_thumbPrev && _holds.Count > 0)
        {
            Destroy(_holds[_holds.Count - 1]);
            _holds.RemoveAt(_holds.Count - 1);
        }

        _triggerPrev = trigger;
        _thumbPrev = thumb;
    }

    // ────────────────────────────────────────────
    // Auto Population
    // ────────────────────────────────────────────
    public void AutoPopulate()
    {
        ClearAll();

        var walls = wallSystem.Walls;
        if (walls == null || walls.Count == 0) return;

        foreach (var wall in walls)
        {
            var spawns = GenerateHoldPositions(wall);
            foreach (var (pos, rot, col) in spawns)
            {
                GameObject hold = CreateHoldObject(pos, rot, col);
                _holds.Add(hold);
            }
        }
    }
    

    /// <summary>
    /// Generate candidate hold positions for one wall panel using
    /// rejection sampling to enforce minimum spacing.
    /// </summary>
    List<(Vector3, Quaternion, Color)> GenerateHoldPositions(CalibratedWall wall)
{
    var result = new List<(Vector3, Quaternion, Color)>();

    float halfW = wall.width * 0.5f - edgeMargin;

    // Clamp spawn zone to below player's feet
    float feetY = playerHead != null
        ? playerHead.position.y - 1.6f
        : wall.surfaceTopY;
    float spawnTopY    = Mathf.Min(wall.surfaceTopY, feetY - spawnBelowFeet);
    float spawnBottomY = wall.surfaceBottomY + edgeMargin;

    float usableHeight = spawnTopY - spawnBottomY;
    float halfH = usableHeight * 0.5f;

    if (halfW <= 0 || halfH <= 0) return result;

    int targetCount = Mathf.Max(1, Mathf.RoundToInt(usableHeight * holdsPerMeter));
    int maxAttempts = targetCount * 20;
    int placed = 0;

    float spawnCenterY = (spawnTopY + spawnBottomY) * 0.5f;

    for (int attempt = 0; attempt < maxAttempts && placed < targetCount; attempt++)
    {
        float u = Random.Range(-halfW, halfW);
        float v = Random.Range(-halfH, halfH);

        // Move along the wall's local axes from the panel center
        Vector3 worldPos = wall.center
            + wall.localRight * u
            + wall.localUp    * v;

        // Shift to the correct vertical spawn zone
        worldPos.y = spawnCenterY + v;

        // ── Key fix: project onto the actual wall plane before offsetting ──
        // This mirrors what TryPlace() does and corrects any axis drift
        worldPos = wall.ProjectPointOntoWall(worldPos);
        worldPos += wall.normal * wallOffset;

        bool tooClose = false;
        foreach (var (existingPos, _, _) in result)
        {
            Vector3 delta = existingPos - worldPos;
            float dH = Mathf.Abs(Vector3.Dot(delta, wall.localRight));
            float dV = Mathf.Abs(Vector3.Dot(delta, wall.localUp));
            if (dH < minHorizontalSpacing && dV < minVerticalSpacing)
            {
                tooClose = true;
                break;
            }
        }
        if (tooClose) continue;

        Quaternion rot = Quaternion.LookRotation(wall.normal, wall.localUp);
        Color col = HoldColors[Random.Range(0, HoldColors.Length)];
        result.Add((worldPos, rot, col));
        placed++;
    }

    return result;
}
    

    // ────────────────────────────────────────────
    // Manual Placement
    // ────────────────────────────────────────────

    void TryPlace()
    {
        Vector3 controllerPos = rightController.position;
        controllerPos.y += heightOffset;

        float bestDist = float.MaxValue;
        int bestWall = -1;

        var walls = wallSystem.Walls;
        for (int i = 0; i < walls.Count; i++)
        {
            float dist = Mathf.Abs(walls[i].SignedDistanceToPoint(controllerPos));
            if (dist < bestDist && dist < maxDistance)
            {
                bestDist = dist;
                bestWall = i;
            }
        }

        if (bestWall < 0) return;

        var wall = walls[bestWall];
        Vector3 onWall  = wall.ProjectPointOntoWall(controllerPos);
        Vector3 holdPos = onWall + wall.normal * wallOffset;
        Quaternion rot  = Quaternion.LookRotation(wall.normal, wall.localUp);
        Color col       = HoldColors[Random.Range(0, HoldColors.Length)];

        GameObject obj = CreateHoldObject(holdPos, rot, col);
        obj.name = $"Hold_Manual_{_holds.Count}";
        _holds.Add(obj);
    }

    // ────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────

    GameObject CreateHoldObject(Vector3 pos, Quaternion rot, Color col)
    {
        GameObject obj;

        if (holdPrefab != null)
        {
            obj = Instantiate(holdPrefab, pos, rot);
            // Apply color to all renderers on the prefab
            foreach (var r in obj.GetComponentsInChildren<Renderer>())
            {
                // Clone material so holds don't share the same instance
                r.material = new Material(r.material);
                r.material.color = col;
            }
        }
        else
        {
            obj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            obj.transform.position = pos;
            obj.transform.rotation = rot;
            obj.transform.localScale = Vector3.one * 0.06f;
            Destroy(obj.GetComponent<Collider>());
            obj.GetComponent<Renderer>().material.color = col;
        }

        obj.transform.localScale *= holdScale;
        obj.name = $"Hold_{_holds.Count}";
        return obj;
    }
    

    public void ClearAll()
    {
        foreach (var h in _holds) if (h != null) Destroy(h);
        _holds.Clear();
    }

    InputDevice GetDevice()
    {
        var flags = InputDeviceCharacteristics.Right | InputDeviceCharacteristics.Controller;
        var devs = new List<InputDevice>();
        InputDevices.GetDevicesWithCharacteristics(flags, devs);
        return devs.Count > 0 ? devs[0] : default;
    }
}