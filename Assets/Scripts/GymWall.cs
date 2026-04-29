using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class GymWall : MonoBehaviour
{
    [Header("References")]
    public EnvironmentManager environmentManager;

    [Header("Wall Dimensions")]
    public float width                = 4f;
    [Tooltip("How much of the wall is visible above ground at the start (meters)")]
    public float initialVisibleHeight = 6f;
    [Tooltip("Total wall height including the underground portion (meters)")]
    public float totalHeight          = 50f;

    [Header("Wall Appearance")]
    public Material wallMaterial;
    public Color wallColor = new Color(0.3f, 0.3f, 0.35f, 1f);

    [Header("Holds")]
    public Mesh holdMesh;
    public Material holdMaterial;
    public int holdCount = 25;
    public float edgeMargin    = 0.15f;
    public float holdProtrusion = 0.04f;
    public float holdMinScale  = 0.04f;
    public float holdMaxScale  = 0.09f;
    public int randomSeed      = 0;

    private Mesh        _wallMesh;
    private Matrix4x4[] _holdMatrices;
    private bool        _holdsReady;

    // ────────────────────────────────────────────────────────────────────────
    // Lifecycle
    // ────────────────────────────────────────────────────────────────────────

    void Start()
    {
        BuildWallMesh();
        SpawnHolds();
    }

    void Update()
    {
        // Wall mesh never changes — just draw holds every frame
        if (_holdsReady && holdMesh != null && holdMaterial != null)
        {
            int batchSize = 1023;
            for (int i = 0; i < _holdMatrices.Length; i += batchSize)
            {
                int count = Mathf.Min(batchSize, _holdMatrices.Length - i);
                var batch = new Matrix4x4[count];
                System.Array.Copy(_holdMatrices, i, batch, 0, count);
                Graphics.DrawMeshInstanced(holdMesh, 0, holdMaterial, batch);
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Wall Mesh — built once, never changes
    // ────────────────────────────────────────────────────────────────────────

    void BuildWallMesh()
{
    _wallMesh = new Mesh { name = "GymWall" };
    GetComponent<MeshFilter>().sharedMesh = _wallMesh;

    var mr = GetComponent<MeshRenderer>();
    mr.sharedMaterial = wallMaterial != null
        ? wallMaterial : CreateDefaultMaterial();

    float hw = width * 0.5f;

    // Top sits initialVisibleHeight above the transform origin (ground level)
    // Bottom extends the rest of totalHeight below
    float top    =  initialVisibleHeight;
    float bottom = -(totalHeight - initialVisibleHeight);

    var vertices = new Vector3[]
    {
        new Vector3(-hw, bottom, 0f),
        new Vector3( hw, bottom, 0f),
        new Vector3( hw, top,    0f),
        new Vector3(-hw, top,    0f),
    };

    var uvs = new Vector2[]
    {
        new Vector2(0,     0),
        new Vector2(width, 0),
        new Vector2(width, totalHeight),
        new Vector2(0,     totalHeight),
    };

    _wallMesh.vertices  = vertices;
    _wallMesh.uv        = uvs;
    _wallMesh.triangles = new int[]
    {
        0, 1, 2,  0, 2, 3,
        0, 2, 1,  0, 3, 2
    };
    _wallMesh.RecalculateNormals();
    _wallMesh.RecalculateBounds();
}

void SpawnHolds()
{
    if (holdMesh == null || holdMaterial == null) return;
    holdMaterial.enableInstancing = true;

    float hw     = width * 0.5f - edgeMargin;
    float top    =  initialVisibleHeight  - edgeMargin;
    float bottom = -(totalHeight - initialVisibleHeight) + edgeMargin;

    var rng = new System.Random(randomSeed + name.GetHashCode());
    _holdMatrices = new Matrix4x4[holdCount];

    for (int i = 0; i < holdCount; i++)
    {
        float x     = Mathf.Lerp(-hw, hw,     (float)rng.NextDouble());
        float y     = Mathf.Lerp(bottom, top,  (float)rng.NextDouble());
        float yRot  = (float)rng.NextDouble() * 360f;
        float scale = Mathf.Lerp(holdMinScale, holdMaxScale,
                                  (float)rng.NextDouble());

        Vector3    worldPos = transform.TransformPoint(
                                  new Vector3(x, y, holdProtrusion));
        Quaternion worldRot = transform.rotation *
                              Quaternion.Euler(0f, yRot, 0f);

        _holdMatrices[i] = Matrix4x4.TRS(worldPos, worldRot,
                                          Vector3.one * scale);
    }

    _holdsReady = true;
    Debug.Log($"GymWall '{name}': {holdCount} holds spawned across " +
              $"{totalHeight}m total height.");
}

    // ────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────

    Material CreateDefaultMaterial()
    {
        var mat = new Material(
            Shader.Find("Universal Render Pipeline/Lit") ??
            Shader.Find("Standard"));
        mat.color = wallColor;
        return mat;
    }
}