using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using TMPro;

/// <summary>
/// Calibrates a wall surface using VR controller samples, then builds a
/// pyramidal frustum whose near face is centered on that wall, oriented
/// along the wall's outward normal — like a camera frustum projected out
/// of the climbing wall.
///
/// CONTROLS:
///   [Trigger]  Sample a point on the wall (hold controller flat against wall)
///   [A]        Finalize calibration (needs 3+ points)
///   [B]        Undo last sample
///   [Grip]     Reset everything
///
/// After calibration, the frustum is built and editable in the Inspector.
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
[ExecuteAlways]
public class WallFrustumCalibrator : MonoBehaviour
{
    public enum ControllerHand { Right, Left }

    [Header("Orientation")]
    [Tooltip("Flip the frustum 180° so it opens into the wall instead of outward")]
    public bool flipDirection = false;
    
    // ── References ──────────────────────────────────────────────────────
    [Header("References")]
    public Transform rightController;
    public Transform leftController;
    public TextMeshProUGUI statusText;

    // ── Controller ──────────────────────────────────────────────────────
    [Header("Controls")]
    public ControllerHand activeHand = ControllerHand.Right;

    // ── Frustum Shape ───────────────────────────────────────────────────
    [Header("Frustum Shape")]
    [Tooltip("Distance from the wall to the near (small) face")]
    public float nearDistance = 0.1f;

    [Tooltip("Distance from the wall to the far (large) face")]
    public float farDistance = 3f;

    [Tooltip("Width of the near face")]
    public float nearWidth = 0.4f;

    [Tooltip("Height of the near face")]
    public float nearHeight = 0.3f;

    [Tooltip("Width of the far face")]
    public float farWidth = 2f;

    [Tooltip("Height of the far face")]
    public float farHeight = 1.5f;

    // ── Appearance ──────────────────────────────────────────────────────
    [Header("Appearance")]
    public Material frustumMaterial;
    public Color frustumColor = new Color(0.2f, 0.6f, 1f, 0.4f);

    // ── State ────────────────────────────────────────────────────────────
    public enum Phase { Sampling, Done }
    public Phase CurrentPhase { get; private set; } = Phase.Sampling;

    // Calibration
    private List<Vector3> _samplePoints  = new List<Vector3>();
    private List<Vector3> _sampleNormals = new List<Vector3>();
    private List<GameObject> _sampleMarkers = new List<GameObject>();

    // Fitted plane result
    private Vector3 _wallCenter;
    private Vector3 _wallNormal;
    private Vector3 _wallUp;
    private Vector3 _wallRight;
    private bool    _calibrated;

    // Mesh
    private Mesh _mesh;

    // Input state
    private bool _triggerPrev, _primaryPrev, _secondaryPrev, _gripPrev;

    // ────────────────────────────────────────────────────────────────────
    // Lifecycle
    // ────────────────────────────────────────────────────────────────────

    private void Awake()
    {
        // Pre-assign the mesh so the MeshFilter has something from the start
        _mesh = new Mesh { name = "WallFrustum" };
        GetComponent<MeshFilter>().sharedMesh = _mesh;

        MeshRenderer mr = GetComponent<MeshRenderer>();
        if (mr.sharedMaterial == null)
            mr.sharedMaterial = BuildDefaultMaterial();
    }

    private void Update()
    {
        InputDevice dev = GetDevice();
        if (!dev.isValid) { UpdateStatusText(); return; }

        dev.TryGetFeatureValue(CommonUsages.triggerButton, out bool trigger);
        dev.TryGetFeatureValue(CommonUsages.primaryButton,   out bool primary);
        dev.TryGetFeatureValue(CommonUsages.secondaryButton, out bool secondary);
        dev.TryGetFeatureValue(CommonUsages.gripButton,      out bool grip);

        // Grip always resets
        if (grip && !_gripPrev)
            ResetCalibration();

        switch (CurrentPhase)
        {
            case Phase.Sampling:
                if (trigger && !_triggerPrev)
                    SamplePoint();

                if (primary && !_primaryPrev && _samplePoints.Count >= 3)
                    FinalizeCalibration();

                if (secondary && !_secondaryPrev && _samplePoints.Count > 0)
                    UndoLastSample();
                break;

            case Phase.Done:
                // Allow going back to re-sample
                if (secondary && !_secondaryPrev)
                    ResetCalibration();

                if (primary && !_primaryPrev)
                    flipDirection = !flipDirection;
                    BuildFrustumMesh();    
                break;
        }  

        _triggerPrev  = trigger;
        _primaryPrev  = primary;
        _secondaryPrev = secondary;
        _gripPrev     = grip;

        UpdateStatusText();
    }

    // ────────────────────────────────────────────────────────────────────
    // Sampling
    // ────────────────────────────────────────────────────────────────────

    private void SamplePoint()
    {
        Transform ctrl = GetController();
        if (ctrl == null) return;

        // Controller -forward is the face of the controller (pointing into the wall)
        Vector3 pos    = ctrl.position;
        Vector3 normal = -ctrl.forward;

        _samplePoints.Add(pos);
        _sampleNormals.Add(normal);

        // Small sphere marker at sample location
        GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        marker.transform.position   = pos;
        marker.transform.localScale = Vector3.one * 0.015f;
        marker.GetComponent<Renderer>().material.color = Color.cyan;
        Destroy(marker.GetComponent<Collider>());
        _sampleMarkers.Add(marker);

        SendHaptic(0.3f, 0.1f);
    }

    private void UndoLastSample()
    {
        int last = _samplePoints.Count - 1;
        _samplePoints.RemoveAt(last);
        _sampleNormals.RemoveAt(last);
        Destroy(_sampleMarkers[last]);
        _sampleMarkers.RemoveAt(last);
    }

    private void ClearSamples()
    {
        _samplePoints.Clear();
        _sampleNormals.Clear();
        foreach (var m in _sampleMarkers) if (m != null) Destroy(m);
        _sampleMarkers.Clear();
    }

    // ────────────────────────────────────────────────────────────────────
    // Calibration
    // ────────────────────────────────────────────────────────────────────

    private void FinalizeCalibration()
    {
        // Fit the plane for the normal only
        PlaneFit.FitPlane(_samplePoints, out _, out _wallNormal);

        // Use the raw average of controller positions as the exact origin
        _wallCenter = Vector3.zero;
        foreach (var p in _samplePoints) _wallCenter += p;
        _wallCenter /= _samplePoints.Count;
        
        transform.position = _wallCenter;

        // Flip normal to face toward the climber (same direction as controller normals)
        Vector3 avgN = Vector3.zero;
        foreach (var n in _sampleNormals) avgN += n;
        if (Vector3.Dot(_wallNormal, avgN) < 0f) _wallNormal = -_wallNormal;

        // Build an orthonormal frame on the wall surface
        ComputeWallFrame(_wallNormal, out _wallRight, out _wallUp);

        _calibrated   = true;
        CurrentPhase  = Phase.Done;

        ClearSamples();
        BuildFrustumMesh();
        SendHaptic(0.8f, 0.4f);
    }

    private static void ComputeWallFrame(Vector3 normal, out Vector3 right, out Vector3 up)
    {
        // Use world-up unless the wall is nearly horizontal, then use forward
        Vector3 refUp = (Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > 0.95f)
            ? Vector3.forward : Vector3.up;

        right = Vector3.Cross(refUp, normal).normalized;
        up    = Vector3.Cross(normal, right).normalized;
    }

    // ────────────────────────────────────────────────────────────────────
    // Frustum Mesh
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds the frustum mesh. The near face sits at wallCenter + normal *
    /// nearDistance; the far face opens outward along the normal. Can be called
    /// from OnValidate in the Editor to live-preview shape changes.
    /// </summary>
    public void BuildFrustumMesh()
    {
        if (!_calibrated) return;

        // ── The frustum coordinate system ──
        // Origin  : _wallCenter  (fitted centroid on the wall surface)
        // +Z axis : _wallNormal  (outward from wall → frustum opens this way)
        // +X axis : _wallRight   (wall's horizontal axis)
        // +Y axis : _wallUp      (wall's vertical axis)

        float nHW = nearWidth  * 0.5f;
        float nHH = nearHeight * 0.5f;
        float fHW = farWidth   * 0.5f;
        float fHH = farHeight  * 0.5f;

        Vector3 normal = flipDirection ? -_wallNormal : _wallNormal;
        
        // Helper: world position of a point in frustum-local space
        // Local-space helper — origin is now the GameObject's position (= _wallCenter)
        Vector3 P(float x, float y, float z) =>
            _wallRight  * x +
            _wallUp     * y +
            normal      * z;

        // Near face corners (small rectangle, close to wall)
        Vector3 n0 = P(-nHW, -nHH, 0f); // bottom-left
        Vector3 n1 = P( nHW, -nHH, 0f); // bottom-right
        Vector3 n2 = P( nHW,  nHH, 0f); // top-right
        Vector3 n3 = P(-nHW,  nHH, 0f); // top-left

        // Far face corners (large rectangle, far from wall) — not rendered
        Vector3 f0 = P(-fHW, -fHH, farDistance);
        Vector3 f1 = P( fHW, -fHH, farDistance);
        Vector3 f2 = P( fHW,  fHH, farDistance);
        Vector3 f3 = P(-fHW,  fHH, farDistance);

        // ── 5 faces, each duplicated for double-sided rendering ──
        // Faces: Near cap | Bottom | Top | Left | Right
        // Each face = 4 verts front + 4 verts back = 8 verts
        // Total: 5 faces × 8 = 40 verts

        Vector3[] vertices =
        {
            // ── FRONT FACES ──────────────────────────────────
            n0, n1, n2, n3,          // Near cap   [0-3]
            n0, n1, f1, f0,          // Bottom     [4-7]
            n3, n2, f2, f3,          // Top        [8-11]
            f0, n0, n3, f3,          // Left       [12-15]
            n1, f1, f2, n2,          // Right      [16-19]

            // ── BACK FACES (same positions, reversed winding) ─
            n0, n1, n2, n3,          // Near cap   [20-23]
            n0, n1, f1, f0,          // Bottom     [24-27]
            n3, n2, f2, f3,          // Top        [28-31]
            f0, n0, n3, f3,          // Left       [32-35]
            n1, f1, f2, n2,          // Right      [36-39]
        };

        int[] triangles =
        {
            // ── FRONT (outward normals) ──────────────────────

            // Near cap (faces away from wall, toward climber)
             2,  1,  0,
             3,  2,  0,

            // Bottom
             4,  5,  6,
             4,  6,  7,

            // Top
             8,  9, 10,
             8, 10, 11,

            // Left
            12, 13, 14,
            12, 14, 15,

            // Right
            16, 17, 18,
            16, 18, 19,

            // ── BACK (inward normals, reversed winding) ──────

            // Near cap back
            20, 21, 22,
            20, 22, 23,

            // Bottom back
            25, 24, 26,
            26, 24, 27,

            // Top back
            29, 28, 30,
            30, 28, 31,

            // Left back
            33, 32, 34,
            34, 32, 35,

            // Right back
            37, 36, 38,
            38, 36, 39,
        };

        // Simple planar UVs — same layout repeated per face
        Vector2[] uvs = new Vector2[40];
        for (int i = 0; i < 40; i += 4)
        {
            uvs[i + 0] = new Vector2(0, 0);
            uvs[i + 1] = new Vector2(1, 0);
            uvs[i + 2] = new Vector2(1, 1);
            uvs[i + 3] = new Vector2(0, 1);
        }

        _mesh.Clear();
        _mesh.vertices  = vertices;
        _mesh.triangles = triangles;
        _mesh.uv        = uvs;
        _mesh.RecalculateNormals();
        _mesh.RecalculateBounds();
    }

    // Rebuild live in the editor when inspector values change
    private void OnValidate() => BuildFrustumMesh();

    // ────────────────────────────────────────────────────────────────────
    // Public API
    // ────────────────────────────────────────────────────────────────────

    /// <summary>Clear the calibration and frustum mesh, and restart sampling.</summary>
    public void ResetCalibration()
    {
        _calibrated  = false;
        CurrentPhase = Phase.Sampling;
        ClearSamples();

        if (_mesh != null) _mesh.Clear();
    }

    /// <summary>The fitted wall normal (outward). Valid only after calibration.</summary>
    public Vector3 WallNormal => _wallNormal;

    /// <summary>The fitted wall centroid. Valid only after calibration.</summary>
    public Vector3 WallCenter => _wallCenter;

    /// <summary>Whether the frustum has been calibrated and built.</summary>
    public bool IsCalibrated => _calibrated;

    // ────────────────────────────────────────────────────────────────────
    // UI
    // ────────────────────────────────────────────────────────────────────

    private void UpdateStatusText()
    {
        if (statusText == null) return;

        statusText.text = CurrentPhase switch
        {
            Phase.Sampling => $"FRUSTUM CAL  |  Samples: {_samplePoints.Count}/3+\n"
                            + "[Trigger] Sample  [A] Finalize  [B] Undo  [Grip] Reset",

            Phase.Done     => "FRUSTUM READY\n"
                            + "[B] Re-calibrate  [Grip] Reset",

            _              => string.Empty
        };
    }

    // ────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────

    private Transform GetController() =>
        activeHand == ControllerHand.Right ? rightController : leftController;

    private InputDevice GetDevice()
    {
        var flags = activeHand == ControllerHand.Right
            ? InputDeviceCharacteristics.Right | InputDeviceCharacteristics.Controller
            : InputDeviceCharacteristics.Left  | InputDeviceCharacteristics.Controller;

        var devs = new List<InputDevice>();
        InputDevices.GetDevicesWithCharacteristics(flags, devs);
        return devs.Count > 0 ? devs[0] : default;
    }

    private void SendHaptic(float amplitude, float duration)
    {
        InputDevice dev = GetDevice();
        if (dev.isValid) dev.SendHapticImpulse(0, amplitude, duration);
    }

    private Material BuildDefaultMaterial()
    {
        string[] candidates =
        {
            "Universal Render Pipeline/Unlit",
            "Unlit/Color",
            "Standard"
        };

        Shader shader = null;
        foreach (var n in candidates)
        {
            shader = Shader.Find(n);
            if (shader != null) break;
        }

        var mat = new Material(shader ?? Shader.Find("Hidden/InternalErrorShader"));
        mat.color = frustumColor;

        // Transparent-ish so the frustum reads as a volume indicator
        if (mat.HasProperty("_Mode"))
        {
            mat.SetFloat("_Mode", 3);          // Transparent
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite",   0);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = 3000;
        }

        if (mat.HasProperty("_Cull"))
            mat.SetFloat("_Cull", 0); // Off — belt-and-suspenders for double-sided

        return mat;
    }
    
    /// <summary>
    /// Returns the near plane as a CalibratedWall so HoldPlacementManager
    /// can project and place holds onto it.
    /// The normal faces back toward the climber (opposite to frustum opening).
    /// </summary>
    public CalibratedWall GetNearPlaneAsWall()
    {
        Vector3 normal = flipDirection ? -_wallNormal : _wallNormal;

        return new CalibratedWall
        {
            wallIndex    = 99,
            center       = _wallCenter,
            normal       = -normal,          // faces toward climber for correct offsetting
            localRight   = _wallRight,
            localUp      = _wallUp,
            width        = nearWidth,
            height       = nearHeight,
            samplePoints  = new List<Vector3>(),
            sampleNormals = new List<Vector3>()
        };
    }
}