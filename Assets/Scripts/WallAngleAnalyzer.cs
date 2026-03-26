using System.Collections.Generic;
using UnityEngine;

/*
 * -----------------------------
 * -----------------------------
 * NOT IN USED ANYMORE, OLD TEST
 * -----------------------------
 * -----------------------------
 */
[RequireComponent(typeof(WallCalibrationManager))]
public class WallAngleAnalyzer : MonoBehaviour
{
    [Header("Visualization")]
    [Tooltip("Show edge/intersection lines between adjacent walls")]
    public bool showEdgeLines = true;

    [Tooltip("Width of edge line renderers")]
    public float edgeLineWidth = 0.01f;

    [Tooltip("Color for intersection edge lines")]
    public Color edgeColor = Color.yellow;

    [Tooltip("Show angle labels at wall junctions")]
    public bool showAngleLabels = true;

    private WallCalibrationManager _manager;
    private List<GameObject> _edgeVisuals = new List<GameObject>();

    // ── Result data ──

    /// <summary>
    /// Represents the geometric relationship between two walls.
    /// </summary>
    public struct WallPairAnalysis
    {
        public int wallIndexA;
        public int wallIndexB;

        /// <summary>Angle between normals (0° = parallel same direction, 180° = facing each other).</summary>
        public float normalAngle;

        /// <summary>Inside dihedral angle at the junction (what a climber experiences).</summary>
        public float dihedralAngle;

        /// <summary>Whether the walls are close enough to plausibly share an edge.</summary>
        public bool likelyAdjacent;

        /// <summary>The intersection line direction (if walls aren't parallel).</summary>
        public Vector3 edgeDirection;

        /// <summary>A point on the intersection line.</summary>
        public Vector3 edgePoint;

        /// <summary>Minimum distance between the wall planes.</summary>
        public float planeDistance;

        public override string ToString() =>
            $"Walls {wallIndexA}↔{wallIndexB}: dihedral={dihedralAngle:F1}° " +
            $"adjacent={likelyAdjacent} dist={planeDistance * 100:F1}cm";
    }

    void Awake()
    {
        _manager = GetComponent<WallCalibrationManager>();
    }

    void Update()
    {
        if (showEdgeLines)
            UpdateEdgeVisuals();
    }

    // ────────────────────────────────────────────────────────────
    // Analysis
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Analyze the relationship between two specific walls.
    /// </summary>
    public WallPairAnalysis AnalyzePair(int indexA, int indexB)
    {
        var walls = _manager.Walls;
        var a = walls[indexA];
        var b = walls[indexB];

        var result = new WallPairAnalysis
        {
            wallIndexA = indexA,
            wallIndexB = indexB
        };

        // Angle between normals
        result.normalAngle = Vector3.Angle(a.normal, b.normal);

        // Dihedral angle (inside angle at the junction)
        // For a convex corner (like two walls meeting outward): dihedral = 180 - normalAngle
        // For a concave corner (inside corner): dihedral = 180 + normalAngle... 
        // But we keep it simple: the angle between the wall surfaces.
        result.dihedralAngle = 180f - result.normalAngle;

        // Compute intersection line of the two planes
        ComputePlaneIntersection(a, b,
            out result.edgeDirection,
            out result.edgePoint,
            out result.planeDistance);

        // Adjacency heuristic: planes are close and not parallel
        result.likelyAdjacent = result.planeDistance < 0.15f && // within 15cm
                                 result.normalAngle > 5f &&     // not nearly parallel
                                 result.normalAngle < 175f;

        return result;
    }

    /// <summary>
    /// Analyze all wall pairs and return results.
    /// </summary>
    public List<WallPairAnalysis> AnalyzeAllPairs()
    {
        var results = new List<WallPairAnalysis>();
        var walls = _manager.Walls;

        for (int i = 0; i < walls.Count; i++)
        {
            for (int j = i + 1; j < walls.Count; j++)
            {
                results.Add(AnalyzePair(i, j));
            }
        }

        return results;
    }

    /// <summary>
    /// Find walls that are likely adjacent (sharing an edge).
    /// </summary>
    public List<WallPairAnalysis> FindAdjacentWalls()
    {
        var all = AnalyzeAllPairs();
        return all.FindAll(p => p.likelyAdjacent);
    }

    /// <summary>
    /// Get the tilt category for a wall.
    /// </summary>
    public static string GetTiltCategory(CalibratedWall wall)
    {
        float tilt = wall.TiltAngleDegrees;
        if (tilt < -15f) return "Slab";
        if (tilt < -2f) return "Slight Slab";
        if (tilt <= 2f) return "Vertical";
        if (tilt <= 15f) return "Slight Overhang";
        if (tilt <= 35f) return "Overhang";
        if (tilt <= 55f) return "Steep Overhang";
        return "Roof";
    }

    // ────────────────────────────────────────────────────────────
    // Plane–Plane Intersection
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Compute the intersection line of two planes defined by walls.
    /// </summary>
    static void ComputePlaneIntersection(CalibratedWall a, CalibratedWall b,
        out Vector3 lineDirection, out Vector3 linePoint, out float planeDistance)
    {
        Vector3 n1 = a.normal;
        Vector3 n2 = b.normal;

        // Direction of intersection line = cross product of normals
        lineDirection = Vector3.Cross(n1, n2);

        float dirMag = lineDirection.magnitude;

        if (dirMag < 0.001f)
        {
            // Planes are (nearly) parallel
            lineDirection = Vector3.zero;
            linePoint = (a.center + b.center) * 0.5f;
            planeDistance = Mathf.Abs(Vector3.Dot(n1, b.center - a.center));
            return;
        }

        lineDirection /= dirMag; // normalize

        // Find a point on the intersection line.
        // Solve the system:
        //   n1 · (p - c1) = 0
        //   n2 · (p - c2) = 0
        // We parameterize: p = c1 + α*n1 + β*n2 + γ*lineDir
        // Setting γ=0 and solving the 2×2 system for α, β:

        float d1 = Vector3.Dot(n1, a.center);
        float d2 = Vector3.Dot(n2, b.center);
        float n1n2 = Vector3.Dot(n1, n2);
        float denom = 1f - n1n2 * n1n2;

        if (Mathf.Abs(denom) < 1e-6f)
        {
            linePoint = (a.center + b.center) * 0.5f;
            planeDistance = Mathf.Abs(Vector3.Dot(n1, b.center - a.center));
            return;
        }

        float alpha = (d1 - d2 * n1n2) / denom;
        float beta = (d2 - d1 * n1n2) / denom;

        linePoint = alpha * n1 + beta * n2;

        // Plane distance: distance between the two planes along the shortest path
        // For non-parallel planes this is 0 at the intersection; 
        // we measure minimum distance between wall centers and their opposing planes
        float distAtoB = Mathf.Abs(b.SignedDistanceToPoint(a.center));
        float distBtoA = Mathf.Abs(a.SignedDistanceToPoint(b.center));
        planeDistance = Mathf.Min(distAtoB, distBtoA);
    }

    // ────────────────────────────────────────────────────────────
    // Edge Visualization
    // ────────────────────────────────────────────────────────────

    void UpdateEdgeVisuals()
    {
        // Clear old visuals
        foreach (var obj in _edgeVisuals)
            if (obj != null) Destroy(obj);
        _edgeVisuals.Clear();

        var adjacentPairs = FindAdjacentWalls();

        foreach (var pair in adjacentPairs)
        {
            if (pair.edgeDirection == Vector3.zero) continue;

            // Draw the edge line
            var lineObj = new GameObject($"Edge_{pair.wallIndexA}_{pair.wallIndexB}");
            var lr = lineObj.AddComponent<LineRenderer>();

            lr.startWidth = edgeLineWidth;
            lr.endWidth = edgeLineWidth;
            lr.material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            lr.material.color = edgeColor;
            lr.positionCount = 2;

            // Extend line 2m in each direction from the intersection point
            float extent = 2f;
            lr.SetPosition(0, pair.edgePoint - pair.edgeDirection * extent);
            lr.SetPosition(1, pair.edgePoint + pair.edgeDirection * extent);

            _edgeVisuals.Add(lineObj);
        }
    }

    // ────────────────────────────────────────────────────────────
    // Debug / Logging
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Log a full analysis report to the console.
    /// </summary>
    public void LogFullReport()
    {
        var walls = _manager.Walls;

        Debug.Log("══════════════════════════════════════════");
        Debug.Log("       WALL CALIBRATION REPORT");
        Debug.Log("══════════════════════════════════════════");

        for (int i = 0; i < walls.Count; i++)
        {
            var w = walls[i];
            Debug.Log($"Wall {i}: {GetTiltCategory(w)}");
            Debug.Log($"  Center:  {w.center:F3}");
            Debug.Log($"  Normal:  {w.normal:F3}");
            Debug.Log($"  Tilt:    {w.TiltAngleDegrees:F1}°");
            Debug.Log($"  Facing:  {w.FacingAngleDegrees:F1}°");
            Debug.Log($"  Size:    {w.width:F2} × {w.height:F2} m");
            Debug.Log($"  Samples: {w.samplePoints.Count}");
            Debug.Log($"  Error:   {w.MeanFittingError * 1000:F2} mm");
        }

        var pairs = AnalyzeAllPairs();
        if (pairs.Count > 0)
        {
            Debug.Log("──────────────────────────────────────────");
            Debug.Log("Wall Relationships:");
            foreach (var p in pairs)
            {
                Debug.Log($"  {p}");
            }
        }
    }
}
