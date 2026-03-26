using System.Collections.Generic;
using UnityEngine;
/*
 * -----------------------------
 * -----------------------------
 * NOT IN USED ANYMORE, OLD TEST
 * -----------------------------
 * -----------------------------
 */
public class WallMeshBuilder : MonoBehaviour
{
    [Header("References")]
    public WallCalibrationManager calibrationManager;
    public EnvironmentManager environmentManager;

    [Header("Wall Extension")]
    [Tooltip("Extra meters above the calibrated top edge")]
    public float topMargin = 0.5f;

    [Header("Clipping")]
    public bool clipCorners = true;
    public float minClipAngle = 5f;

    [Tooltip("Max distance between wall planes to consider adjacent")]
    public float adjacencyDistance = 1f;

    [Header("Appearance")]
    public Material wallMaterial;
    public Color wallColor = new Color(0.35f, 0.35f, 0.4f, 1f);
    public Color bottomColor = new Color(0.03f, 0.03f, 0.05f, 1f);

    [Range(0f, 1f)]
    public float fadeStart = 0.3f;

    private List<GameObject> _meshObjects = new List<GameObject>();

    // ────────────────────────────────────────────
    // Public API
    // ────────────────────────────────────────────

    public void RebuildAllWalls()
    {
        ClearMeshes();

        var walls = calibrationManager.Walls;
        if (walls.Count == 0) return;

        float groundY = environmentManager != null
            ? environmentManager.GroundY
            : -20f;

        // Step 1: build base polygons
        var polygons = new List<List<Vector3>>();
        for (int i = 0; i < walls.Count; i++)
            polygons.Add(BuildWallPolygon(walls[i], groundY));

        // Step 2: clip each pair at their intersection line
        if (clipCorners)
            ClipAllPairs(polygons, walls);

        // Step 3: create mesh objects
        for (int i = 0; i < walls.Count; i++)
        {
            if (polygons[i].Count < 3) continue;

            var obj = BuildMeshObject(walls[i], polygons[i], groundY, i);
            _meshObjects.Add(obj);

            if (walls[i].visualObject != null)
                walls[i].visualObject.SetActive(false);
        }

        Debug.Log($"WallMeshBuilder: built {_meshObjects.Count} meshes, groundY={groundY:F1}");
    }

    public void ClearMeshes()
    {
        foreach (var obj in _meshObjects)
            if (obj != null) Object.Destroy(obj);
        _meshObjects.Clear();
    }

    // ────────────────────────────────────────────
    // Polygon Construction
    // ────────────────────────────────────────────

    List<Vector3> BuildWallPolygon(CalibratedWall wall, float groundY)
    {
        float halfW = wall.width * 0.5f;
        float halfH = wall.height * 0.5f;

        float topT = halfH + topMargin;

        float bottomT;
        if (Mathf.Abs(wall.localUp.y) > 0.01f)
            bottomT = (groundY - wall.center.y) / wall.localUp.y;
        else
            bottomT = -(halfH + 50f);

        if (bottomT > -halfH)
            bottomT = -(halfH + 5f);

        Vector3 BL = wall.center + wall.localRight * (-halfW) + wall.localUp * bottomT;
        Vector3 BR = wall.center + wall.localRight * ( halfW) + wall.localUp * bottomT;
        Vector3 TR = wall.center + wall.localRight * ( halfW) + wall.localUp * topT;
        Vector3 TL = wall.center + wall.localRight * (-halfW) + wall.localUp * topT;

        return new List<Vector3> { BL, BR, TR, TL };
    }

    // ────────────────────────────────────────────
    // Intersection-Line Clipping
    // ────────────────────────────────────────────

    /// <summary>
    /// For every pair of adjacent walls, clip BOTH walls at their
    /// intersection line. Each wall keeps its own side.
    /// </summary>
    void ClipAllPairs(List<List<Vector3>> polygons, IReadOnlyList<CalibratedWall> walls)
    {
        for (int i = 0; i < walls.Count; i++)
        {
            for (int j = i + 1; j < walls.Count; j++)
            {
                if (polygons[i].Count < 3 || polygons[j].Count < 3)
                    continue;

                if (!ShouldClip(walls[i], walls[j]))
                    continue;

                ClipPairAtIntersection(
                    polygons, walls, i, j);
            }
        }
    }

    /// <summary>
    /// Clip wall i and wall j against a plane placed at their
    /// intersection line. Wall i keeps the side containing wall i's
    /// center; wall j keeps the side containing wall j's center.
    /// </summary>
    void ClipPairAtIntersection(
        List<List<Vector3>> polygons,
        IReadOnlyList<CalibratedWall> walls,
        int i, int j)
    {
        var wallA = walls[i];
        var wallB = walls[j];

        // Build clip plane for wall A (keeps A's side)
        if (PolygonClipper.BuildIntersectionClipPlane(
                wallA.center, wallA.normal,
                wallB.center, wallB.normal,
                wallA.center,
                out Vector3 clipPointA, out Vector3 clipNormalA))
        {
            polygons[i] = PolygonClipper.ClipByPlane(
                polygons[i], clipPointA, clipNormalA,
                keepPositiveSide: true);
        }

        // Build clip plane for wall B (keeps B's side)
        if (PolygonClipper.BuildIntersectionClipPlane(
                wallA.center, wallA.normal,
                wallB.center, wallB.normal,
                wallB.center,
                out Vector3 clipPointB, out Vector3 clipNormalB))
        {
            polygons[j] = PolygonClipper.ClipByPlane(
                polygons[j], clipPointB, clipNormalB,
                keepPositiveSide: true);
        }
    }

    bool ShouldClip(CalibratedWall a, CalibratedWall b)
    {
        float angle = Vector3.Angle(a.normal, b.normal);
        if (angle < minClipAngle || angle > 180f - minClipAngle)
            return false;

        // Check if the walls are geometrically close enough to intersect
        // when extended. Use center-to-plane distance.
        float distAtoB = Mathf.Abs(b.SignedDistanceToPoint(a.center));
        float distBtoA = Mathf.Abs(a.SignedDistanceToPoint(b.center));
        float minDist = Mathf.Min(distAtoB, distBtoA);

        float threshold = Mathf.Max(a.width, b.width) * 0.5f + adjacencyDistance;
        return minDist < threshold;
    }

    // ────────────────────────────────────────────
    // Mesh Generation
    // ────────────────────────────────────────────

    GameObject BuildMeshObject(CalibratedWall wall, List<Vector3> verts,
        float groundY, int index)
    {
        var obj = new GameObject($"WallMesh_{index}");
        obj.AddComponent<MeshFilter>().mesh = BuildMesh(wall, verts, groundY);

        var mr = obj.AddComponent<MeshRenderer>();
        mr.material = wallMaterial != null
            ? new Material(wallMaterial)
            : CreateDefaultMaterial();

        return obj;
    }

    Mesh BuildMesh(CalibratedWall wall, List<Vector3> worldVerts, float groundY)
    {
        var mesh = new Mesh { name = $"Wall_{wall.wallIndex}" };

        int n = worldVerts.Count;
        var vertices = new Vector3[n];
        var normals  = new Vector3[n];
        var uvs      = new Vector2[n];
        var colors   = new Color[n];

        float halfH = wall.height * 0.5f;
        float calibratedBottomY = wall.center.y - wall.localUp.y * halfH;
        float topY = wall.center.y + wall.localUp.y * (halfH + topMargin);
        float totalHeight = topY - groundY;

        for (int i = 0; i < n; i++)
        {
            vertices[i] = worldVerts[i];
            normals[i]  = wall.normal;

            Vector2 wuv = wall.WorldToWallUV(worldVerts[i]);
            uvs[i] = new Vector2(
                wuv.x / Mathf.Max(wall.width, 0.1f) + 0.5f,
                Mathf.Clamp01((worldVerts[i].y - groundY)
                    / Mathf.Max(totalHeight, 0.1f)));

            float belowCalibrated = calibratedBottomY - worldVerts[i].y;
            float extensionHeight = calibratedBottomY - groundY;

            float t = (extensionHeight <= 0.01f || belowCalibrated <= 0f)
                ? 0f
                : Mathf.Clamp01(belowCalibrated / extensionHeight);

            float fadeT = Mathf.Clamp01(Mathf.InverseLerp(fadeStart, 1f, t));
            fadeT *= fadeT;

            colors[i] = Color.Lerp(wallColor, bottomColor, fadeT);
        }

        mesh.vertices  = vertices;
        mesh.normals   = normals;
        mesh.uv        = uvs;
        mesh.colors    = colors;
        mesh.triangles = PolygonClipper.Triangulate(n);
        mesh.RecalculateBounds();

        return mesh;
    }

    Material CreateDefaultMaterial()
    {
        var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                  ?? Shader.Find("Particles/Standard Unlit")
                  ?? Shader.Find("Universal Render Pipeline/Unlit")
                  ?? Shader.Find("Unlit/Color");

        var mat = new Material(shader);
        mat.color = Color.white;
        mat.SetFloat("_Surface", 0);

        if (mat.HasProperty("_SoftParticlesEnabled"))
            mat.SetFloat("_SoftParticlesEnabled", 0);
        if (mat.HasProperty("_Cull"))
            mat.SetFloat("_Cull", 0);

        return mat;
    }
}