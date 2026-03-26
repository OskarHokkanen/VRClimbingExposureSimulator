using UnityEngine;

/// <summary>
/// Drives a rope asset's three control points to simulate a climbing harness rope.
///
///   ROOT   → anchored at the ground directly below the climber
///   MIDDLE → on the wall surface, below the climber (simulates the last quickdraw / anchor point)
///   END    → on the climber's body at hip height (harness attachment)
///
/// The rope asset should reference these three transforms as its
/// start, middle, and end points.
///
/// SETUP:
///   1. Place your rope asset in the scene
///   2. Create three empty GameObjects: "RopeRoot", "RopeMiddle", "RopeEnd"
///   3. Assign them to this script AND to your rope asset's point fields
///   4. Assign the camera (head) transform so we can derive the hip position
///   5. Assign the EnvironmentManager so we know where the ground is
///   6. Assign the SimpleWallSystem so the middle point sticks to a wall
/// </summary>
public class RopeController : MonoBehaviour
{
    [Header("Rope Control Points")]
    [Tooltip("The rope asset's root/start point — will be placed at ground level")]
    public Transform ropeRoot;

    [Tooltip("The rope asset's middle point — will be placed on the wall below the climber")]
    public Transform ropeMiddle;

    [Tooltip("The rope asset's end point — will be placed at the climber's harness")]
    public Transform ropeEnd;

    [Header("References")]
    [Tooltip("The VR camera / head transform (used to derive body position)")]
    public Transform headTransform;

    public EnvironmentManager environmentManager;
    public SimpleWallSystem wallSystem;

    [Header("Harness Settings")]
    [Tooltip("How far below the head the harness attachment sits (meters). " +
             "Roughly hip height relative to head — about 0.75m for most adults.")]
    public float harnessDropFromHead = 0.75f;

    [Tooltip("Small offset from body center toward the wall (meters). " +
             "Keeps the rope end slightly in front of the climber's hip.")]
    public float harnessForwardOffset = 0.1f;

    [Header("Middle Point Settings")]
    [Tooltip("How far below the harness point the wall anchor sits (meters)")]
    public float middleDropBelowHarness = 1.0f;

    [Tooltip("How far off the wall surface the middle point sits (meters). " +
             "Small offset prevents clipping into the wall mesh.")]
    public float middleWallOffset = 0.1f;

    [Header("Ground Point Settings")]
    [Tooltip("Offset from ground Y to prevent clipping into the floor")]
    public float groundOffset = 0.1f;

    [Header("Smoothing")]
    [Tooltip("How quickly the rope points follow movement (higher = snappier)")]
    public float followSpeed = 10f;

    // ── Internal ──
    private Vector3 _targetRoot;
    private Vector3 _targetMiddle;
    private Vector3 _targetEnd;
    private int _nearestWallIndex = -1;

    void LateUpdate()
    {
        if (headTransform == null) return;

        ComputeTargets();
        ApplySmoothed();
    }

    void ComputeTargets()
    {
        // ── END POINT: harness on the climber ──
        Vector3 headPos = headTransform.position;
        Vector3 headForward = headTransform.forward;
        // Flatten forward to horizontal (climber faces the wall)
        Vector3 flatForward = new Vector3(headForward.x, 0, headForward.z).normalized;

        Vector3 harnessPos = headPos;
        harnessPos.y -= harnessDropFromHead;
        harnessPos += flatForward * harnessForwardOffset;
        _targetEnd = harnessPos;

        // ── MIDDLE POINT: on the wall below the climber ──
        _targetMiddle = ComputeMiddlePoint(harnessPos);

        // ── ROOT POINT: at the ground below the climber ──
        float groundY = environmentManager != null
            ? environmentManager.GroundY + groundOffset
            : 0f;

        // Ground anchor sits directly below the middle point
        _targetRoot = new Vector3(_targetMiddle.x, groundY, _targetMiddle.z);
    }

    Vector3 ComputeMiddlePoint(Vector3 harnessPos)
    {
        if (wallSystem == null || wallSystem.Walls.Count == 0)
        {
            // No walls calibrated — just put it below the harness
            Vector3 fallback = harnessPos;
            fallback.y -= middleDropBelowHarness;
            return fallback;
        }

        // Find the nearest wall to the climber
        var walls = wallSystem.Walls;
        float bestDist = float.MaxValue;
        _nearestWallIndex = 0;

        for (int i = 0; i < walls.Count; i++)
        {
            float dist = Mathf.Abs(walls[i].SignedDistanceToPoint(harnessPos));
            if (dist < bestDist)
            {
                bestDist = dist;
                _nearestWallIndex = i;
            }
        }

        var wall = walls[_nearestWallIndex];

        // Project harness onto the wall surface — this gives us the
        // exact XZ position on the wall that the climber is closest to
        Vector3 onWall = wall.ProjectPointOntoWall(harnessPos);

        // Drop down along the wall's up axis (so the anchor is below the climber)
        onWall -= wall.localUp * middleDropBelowHarness;

        // Small offset outward from the wall surface so the rope
        // doesn't clip into the wall mesh
        onWall += wall.normal * middleWallOffset;

        return onWall;
    }

    void ApplySmoothed()
    {
        float t = followSpeed * Time.deltaTime;

        if (ropeRoot != null)
            ropeRoot.position = Vector3.Lerp(ropeRoot.position, _targetRoot, t);

        if (ropeMiddle != null)
            ropeMiddle.position = Vector3.Lerp(ropeMiddle.position, _targetMiddle, t);

        if (ropeEnd != null)
            ropeEnd.position = Vector3.Lerp(ropeEnd.position, _targetEnd, t);
    }
}