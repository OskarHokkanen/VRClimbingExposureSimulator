using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using TMPro;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
[ExecuteAlways]
public class WallFrustumCalibrator : MonoBehaviour
{
    public bool gymHoldsOnNearFace = false;
    public enum ControllerHand { Right, Left }
    public enum FrustumMode { Frustum, FlatWall }

    // ── References ──────────────────────────────────────────────────────
    [Header("References")]
    public Transform rightController;
    public Transform leftController;
    public TextMeshProUGUI statusText;

    // ── Controller ──────────────────────────────────────────────────────
    [Header("Controls")]
    public ControllerHand activeHand = ControllerHand.Left;

    // ── Frustum Shape ───────────────────────────────────────────────────
    [Header("Frustum Shape")]
    public float farDistance = 3f;
    public float nearWidth   = 0.4f;
    public float nearHeight  = 0.3f;
    public float farWidth    = 2f;
    public float farHeight   = 1.5f;

    // ── Orientation ──────────────────────────────────────────────────────
    [Header("Orientation")]
    public FrustumMode mode = FrustumMode.Frustum;
    public bool flipDirection = false;

    // ── Appearance ──────────────────────────────────────────────────────
    [Header("Appearance")]
    public Material frustumMaterial;
    public Color frustumColor = new Color(0.2f, 0.6f, 1f, 0.4f);

    // ── Preview Holds ────────────────────────────────────────────────────
    [Header("Preview Holds")]
    public int previewHoldCount = 16;
    public GameObject holdPrefab;
    public float holdScale      = 1f;
    public float previewHoldScale = 0.06f;
    public Color previewHoldColor = new Color(1f, 0.4f, 0.1f);
    public int randomSeed = 42;

    private List<GameObject> _previewHolds = new List<GameObject>();

    // ── Gym Holds (GPU Instanced) ─────────────────────────────────────────
    [Header("Gym Holds")]
    [Tooltip("Same mesh used by GymWall")]
    public Mesh gymHoldMesh;
    [Tooltip("Must have Enable GPU Instancing ticked")]
    public Material gymHoldMaterial;
    public int gymHoldCount = 20;
    public float gymHoldMinScale    = 0.04f;
    public float gymHoldMaxScale    = 0.09f;
    public float gymHoldProtrusion  = 0.03f;
    public Vector3 gymHoldRotationOffset = new Vector3(90f, 0f, 0f);
    public Color[] gymHoldColors = new Color[]
    {
        Color.white,
        Color.red,
        new Color(0f, 0.5f, 1f),
        new Color(1f, 0.5f, 0f),
        Color.yellow
    };
    public int gymHoldSeed = 42;

    private Matrix4x4[]         _gymHoldMatrices;
    private Vector4[]           _gymHoldColors;
    private MaterialPropertyBlock _gymHoldBlock;
    private bool                _gymHoldsReady;

    // ── State ────────────────────────────────────────────────────────────
    public enum Phase { Sampling, Done }
    public Phase CurrentPhase { get; private set; } = Phase.Sampling;

    private List<Vector3>     _samplePoints  = new List<Vector3>();
    private List<Vector3>     _sampleNormals = new List<Vector3>();
    private List<GameObject>  _sampleMarkers = new List<GameObject>();

    private Vector3 _wallCenter;
    private Vector3 _wallNormal;
    private Vector3 _wallUp;
    private Vector3 _wallRight;
    private bool    _calibrated;

    private Mesh _mesh;

    private bool _triggerPrev, _primaryPrev, _secondaryPrev, _gripPrev, _thumbPrev;

    // ────────────────────────────────────────────────────────────────────
    // Lifecycle
    // ────────────────────────────────────────────────────────────────────

    private void Awake()
    {
        _mesh = new Mesh { name = "WallFrustum" };
        GetComponent<MeshFilter>().sharedMesh = _mesh;

        MeshRenderer mr = GetComponent<MeshRenderer>();
        if (mr.sharedMaterial == null)
            mr.sharedMaterial = BuildDefaultMaterial();
    }

    private void Update()
    {
        // ── Draw GPU instanced gym holds every frame ──────────────────
        if (_gymHoldsReady && gymHoldMesh != null && gymHoldMaterial != null)
        {
            int batchSize = 1023;
            for (int i = 0; i < _gymHoldMatrices.Length; i += batchSize)
            {
                int count = Mathf.Min(batchSize, _gymHoldMatrices.Length - i);

                var batchMatrices = new Matrix4x4[count];
                var batchColors   = new Vector4[count];

                System.Array.Copy(_gymHoldMatrices, i, batchMatrices, 0, count);
                System.Array.Copy(_gymHoldColors,   i, batchColors,   0, count);

                _gymHoldBlock.SetVectorArray("_BaseColor", batchColors);

                Graphics.DrawMeshInstanced(gymHoldMesh, 0, gymHoldMaterial,
                    batchMatrices, count, _gymHoldBlock);
            }
        }

        // ── Controller input ──────────────────────────────────────────
        InputDevice dev = GetDevice();
        if (!dev.isValid) { UpdateStatusText(); return; }

        dev.TryGetFeatureValue(CommonUsages.triggerButton,   out bool trigger);
        dev.TryGetFeatureValue(CommonUsages.primaryButton,   out bool primary);
        dev.TryGetFeatureValue(CommonUsages.secondaryButton, out bool secondary);
        dev.TryGetFeatureValue(CommonUsages.gripButton,      out bool grip);

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
                if (primary && !_primaryPrev)
                {
                    flipDirection = !flipDirection;
                    BuildFrustumMesh();
                }

                dev.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out bool thumb);
                if (thumb && !_thumbPrev)
                {
                    mode = mode == FrustumMode.Frustum
                        ? FrustumMode.FlatWall : FrustumMode.Frustum;
                    BuildFrustumMesh();
                }
                _thumbPrev = thumb;

                if (secondary && !_secondaryPrev)
                    ResetCalibration();
                break;
        }

        _triggerPrev   = trigger;
        _primaryPrev   = primary;
        _secondaryPrev = secondary;
        _gripPrev      = grip;

        UpdateStatusText();
    }

    // ────────────────────────────────────────────────────────────────────
    // Sampling
    // ────────────────────────────────────────────────────────────────────

    private void SamplePoint()
    {
        Transform ctrl = GetController();
        if (ctrl == null) return;

        Vector3 pos    = ctrl.position;
        Vector3 normal = -ctrl.forward;

        _samplePoints.Add(pos);
        _sampleNormals.Add(normal);

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
        PlaneFit.FitPlane(_samplePoints, out _, out _wallNormal);

        _wallCenter = Vector3.zero;
        foreach (var p in _samplePoints) _wallCenter += p;
        _wallCenter /= _samplePoints.Count;

        transform.position = _wallCenter;

        Vector3 avgN = Vector3.zero;
        foreach (var n in _sampleNormals) avgN += n;
        if (Vector3.Dot(_wallNormal, avgN) < 0f) _wallNormal = -_wallNormal;

        ComputeWallFrame(_wallNormal, out _wallRight, out _wallUp);

        _calibrated  = true;
        CurrentPhase = Phase.Done;

        ClearSamples();
        BuildFrustumMesh();
        SendHaptic(0.8f, 0.4f);
    }

    private static void ComputeWallFrame(Vector3 normal,
        out Vector3 right, out Vector3 up)
    {
        Vector3 refUp = (Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > 0.95f)
            ? Vector3.forward : Vector3.up;
        right = Vector3.Cross(refUp, normal).normalized;
        up    = Vector3.Cross(normal, right).normalized;
    }

    // ────────────────────────────────────────────────────────────────────
    // Frustum Mesh
    // ────────────────────────────────────────────────────────────────────

    public void BuildFrustumMesh()
{
    if (!_calibrated) return;

    Vector3 normal = flipDirection ? -_wallNormal : _wallNormal;

    float nHW = nearWidth  * 0.5f;
    float nHH = nearHeight * 0.5f;
    float fHW = farWidth   * 0.5f;
    float fHH = farHeight  * 0.5f;

    float nearHW = mode == FrustumMode.FlatWall ? fHW : nHW;
    float nearHH = mode == FrustumMode.FlatWall ? fHH : nHH;

    Vector3 P(float x, float y, float z) =>
        _wallRight * x + _wallUp * y + normal * z;

    Vector3 n0 = P(-nearHW, -nearHH, 0f);
    Vector3 n1 = P( nearHW, -nearHH, 0f);
    Vector3 n2 = P( nearHW,  nearHH, 0f);
    Vector3 n3 = P(-nearHW,  nearHH, 0f);

    Vector3 f0 = P(-fHW, -fHH, farDistance);
    Vector3 f1 = P( fHW, -fHH, farDistance);
    Vector3 f2 = P( fHW,  fHH, farDistance);
    Vector3 f3 = P(-fHW,  fHH, farDistance);

    Vector3[] vertices =
    {
        n0, n1, n2, n3,
        n0, n1, f1, f0,
        n3, n2, f2, f3,
        f0, n0, n3, f3,
        n1, f1, f2, n2,

        n0, n1, n2, n3,
        n0, n1, f1, f0,
        n3, n2, f2, f3,
        f0, n0, n3, f3,
        n1, f1, f2, n2,
    };

    int[] triangles =
    {
         2,  1,  0,   3,  2,  0,
         4,  5,  6,   4,  6,  7,
         8,  9, 10,   8, 10, 11,
        12, 13, 14,  12, 14, 15,
        16, 17, 18,  16, 18, 19,

        20, 21, 22,  20, 22, 23,
        25, 24, 26,  26, 24, 27,
        29, 28, 30,  30, 28, 31,
        33, 32, 34,  34, 32, 35,
        37, 36, 38,  38, 36, 39,
    };

    // ── World-space UVs — 1 UV unit = 1 metre, no stretching ──────────
    Vector2[] uvs = new Vector2[40];

    void SetFaceUVs(int startIdx, Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3)
    {
        Vector3 uAxis = (v1 - v0).normalized;
        Vector3 vAxis = (v3 - v0).normalized;
        uvs[startIdx + 0] = new Vector2(0, 0);
        uvs[startIdx + 1] = new Vector2(Vector3.Dot(v1 - v0, uAxis), 0);
        uvs[startIdx + 2] = new Vector2(Vector3.Dot(v2 - v0, uAxis),
                                         Vector3.Dot(v2 - v0, vAxis));
        uvs[startIdx + 3] = new Vector2(0, Vector3.Dot(v3 - v0, vAxis));
    }

    // Front faces
    SetFaceUVs( 0, n0, n1, n2, n3);  // Near cap
    SetFaceUVs( 4, n0, n1, f1, f0);  // Bottom
    SetFaceUVs( 8, n3, n2, f2, f3);  // Top
    SetFaceUVs(12, f0, n0, n3, f3);  // Left
    SetFaceUVs(16, n1, f1, f2, n2);  // Right

    // Back faces — same UVs as front
    SetFaceUVs(20, n0, n1, n2, n3);
    SetFaceUVs(24, n0, n1, f1, f0);
    SetFaceUVs(28, n3, n2, f2, f3);
    SetFaceUVs(32, f0, n0, n3, f3);
    SetFaceUVs(36, n1, f1, f2, n2);

    _mesh.Clear();
    _mesh.vertices  = vertices;
    _mesh.triangles = triangles;
    _mesh.uv        = uvs;
    _mesh.RecalculateNormals();
    _mesh.RecalculateBounds();

    RebuildPreviewHolds(normal, nearHW, nearHH, fHW, fHH);
    SpawnGymHolds(normal, nearHW, nearHH, fHW, fHH);
}

    private void OnValidate() => BuildFrustumMesh();

    // ────────────────────────────────────────────────────────────────────
    // Preview Holds
    // ────────────────────────────────────────────────────────────────────

    private void RebuildPreviewHolds(Vector3 normal,
        float nearHW, float nearHH, float fHW, float fHH)
    {
        foreach (var h in _previewHolds) if (h != null) Destroy(h);
        _previewHolds.Clear();

        if (!_calibrated || previewHoldCount <= 0) return;

        Vector3 origin = transform.position;

        Vector3 W(float x, float y, float z) =>
            origin + _wallRight * x + _wallUp * y + normal * z;

        int perFace = Mathf.Max(1, previewHoldCount / 4);
        int cols    = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(perFace)));
        int rows    = Mathf.Max(1, Mathf.CeilToInt((float)perFace / cols));

        var faces = new (Vector3 c00, Vector3 c10, Vector3 c01,
                         Vector3 c11, Vector3 faceNormal)[]
        {
            ( W(-nearHW, -nearHH, 0f), W( nearHW, -nearHH, 0f),
              W(-fHW,    -fHH, farDistance), W( fHW, -fHH, farDistance),
              _wallUp ),
            ( W(-nearHW,  nearHH, 0f), W( nearHW,  nearHH, 0f),
              W(-fHW,     fHH, farDistance), W( fHW,  fHH, farDistance),
              -_wallUp ),
            ( W(-nearHW, -nearHH, 0f), W(-nearHW,  nearHH, 0f),
              W(-fHW,    -fHH, farDistance), W(-fHW,  fHH, farDistance),
              _wallRight ),
            ( W( nearHW, -nearHH, 0f), W( nearHW,  nearHH, 0f),
              W( fHW,    -fHH, farDistance), W( fHW,  fHH, farDistance),
              -_wallRight ),
        };

        foreach (var face in faces)
        {
            for (int row = 0; row < rows; row++)
            for (int col = 0; col < cols; col++)
            {
                float tCol = cols > 1 ? (col + 0.5f) / cols : 0.5f;
                float tRow = rows > 1 ? (row + 0.5f) / rows : 0.5f;

                Vector3 pos = Vector3.Lerp(
                    Vector3.Lerp(face.c00, face.c10, tCol),
                    Vector3.Lerp(face.c01, face.c11, tCol),
                    tRow);

                pos += face.faceNormal * 0.02f;

                GameObject obj;
                if (holdPrefab != null)
                {
                    obj = Instantiate(holdPrefab, pos,
                        Quaternion.LookRotation(face.faceNormal, _wallUp));
                    obj.transform.localScale *= holdScale;
                }
                else
                {
                    obj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    obj.transform.position   = pos;
                    obj.transform.localScale = Vector3.one * previewHoldScale;
                    var mat = new Material(
                        Shader.Find("Universal Render Pipeline/Lit") ??
                        Shader.Find("Standard"));
                    mat.color = previewHoldColor;
                    obj.GetComponent<Renderer>().material = mat;
                    Destroy(obj.GetComponent<Collider>());
                }

                obj.name = "PreviewHold";
                _previewHolds.Add(obj);
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // Gym Holds (GPU Instanced)
    // ────────────────────────────────────────────────────────────────────

    private void SpawnGymHolds(Vector3 normal,
    float nearHW, float nearHH, float fHW, float fHH)
{
    _gymHoldsReady = false;

    if (gymHoldMesh == null || gymHoldMaterial == null) return;
    gymHoldMaterial.enableInstancing = true;

    _gymHoldBlock = new MaterialPropertyBlock();

    Vector3 origin = transform.position;

    Vector3 W(float x, float y, float z) =>
        origin + _wallRight * x + _wallUp * y + normal * z;

    // 4 side faces — near face excluded by default
    var faces = new List<(Vector3 c00, Vector3 c10, Vector3 c01,
                           Vector3 c11, Vector3 faceNormal)>
    {
        // Bottom
        ( W(-nearHW, -nearHH, 0f), W( nearHW, -nearHH, 0f),
          W(-fHW,    -fHH, farDistance), W( fHW, -fHH, farDistance),
          _wallUp ),
        // Top
        ( W(-nearHW,  nearHH, 0f), W( nearHW,  nearHH, 0f),
          W(-fHW,     fHH, farDistance), W( fHW,  fHH, farDistance),
          -_wallUp ),
        // Left
        ( W(-nearHW, -nearHH, 0f), W(-nearHW,  nearHH, 0f),
          W(-fHW,    -fHH, farDistance), W(-fHW,  fHH, farDistance),
          _wallRight ),
        // Right
        ( W( nearHW, -nearHH, 0f), W( nearHW,  nearHH, 0f),
          W( fHW,    -fHH, farDistance), W( fHW,  fHH, farDistance),
          -_wallRight ),
    };

    // Optionally add near face
    if (gymHoldsOnNearFace)
    {
        faces.Add((
            W(-nearHW, -nearHH, 0f), W( nearHW, -nearHH, 0f),
            W(-nearHW,  nearHH, 0f), W( nearHW,  nearHH, 0f),
            -normal ));
    }

    int perFace = Mathf.Max(1, gymHoldCount / faces.Count);
    int cols    = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(perFace)));
    int rows    = Mathf.Max(1, Mathf.CeilToInt((float)perFace / cols));
    int total   = faces.Count * rows * cols;

    _gymHoldMatrices = new Matrix4x4[total];
    _gymHoldColors   = new Vector4[total];

    var rng = new System.Random(gymHoldSeed);
    int idx = 0;

    foreach (var face in faces)
    {
        for (int row = 0; row < rows; row++)
        for (int col = 0; col < cols; col++)
        {
            float tCol = cols > 1 ? (col + 0.5f) / cols : 0.5f;
            float tRow = rows > 1 ? (row + 0.5f) / rows : 0.5f;

            Vector3 pos = Vector3.Lerp(
                Vector3.Lerp(face.c00, face.c10, tCol),
                Vector3.Lerp(face.c01, face.c11, tCol),
                tRow);

            pos += face.faceNormal * gymHoldProtrusion;

            float scale = Mathf.Lerp(gymHoldMinScale, gymHoldMaxScale,
                                      (float)rng.NextDouble());
            float yRot  = (float)rng.NextDouble() * 360f;

            Quaternion baseRot    = Quaternion.LookRotation(
                                        -face.faceNormal, _wallUp) *
                                    Quaternion.Euler(gymHoldRotationOffset);
            Quaternion randomSpin = Quaternion.AngleAxis(
                                        yRot, -face.faceNormal);
            Quaternion worldRot   = randomSpin * baseRot;

            _gymHoldMatrices[idx] = Matrix4x4.TRS(pos, worldRot,
                                                   Vector3.one * scale);

            Color c = gymHoldColors.Length > 0
                ? gymHoldColors[rng.Next(gymHoldColors.Length)]
                : Color.white;
            _gymHoldColors[idx] = new Vector4(c.r, c.g, c.b, c.a);

            idx++;
        }
    }

    _gymHoldsReady = true;
    Debug.Log($"[WallFrustum] {total} gym holds spawned across " +
              $"{faces.Count} faces (GPU instanced).");
}

    // ────────────────────────────────────────────────────────────────────
    // Public API
    // ────────────────────────────────────────────────────────────────────

    public void CalibrateFromScan(Vector3 wallCenter, Vector3 wallNormal)
    {
        _wallCenter = wallCenter;

        Vector3 flatNormal = new Vector3(wallNormal.x, 0f, wallNormal.z).normalized;
        if (flatNormal.sqrMagnitude < 0.001f)
            flatNormal = wallNormal;

        _wallNormal = flatNormal;

        ComputeWallFrame(_wallNormal, out _wallRight, out _wallUp);

        transform.position = _wallCenter - flatNormal * 0.05f; //0.05 Gasverket 4.5 Telefon

        _calibrated  = true;
        CurrentPhase = Phase.Done;

        BuildFrustumMesh();
        SendHaptic(0.5f, 0.2f);

        Debug.Log($"[WallFrustum] Calibrated from scan — " +
                  $"center={_wallCenter:F3} flatNormal={_wallNormal:F3}");
    }

    public void ResetCalibration()
    {
        _calibrated    = false;
        _gymHoldsReady = false;
        CurrentPhase   = Phase.Sampling;
        ClearSamples();

        foreach (var h in _previewHolds) if (h != null) Destroy(h);
        _previewHolds.Clear();

        _gymHoldMatrices = null;
        _gymHoldColors   = null;

        if (_mesh != null) _mesh.Clear();
    }

    public Vector3 WallNormal   => _wallNormal;
    public Vector3 WallCenter   => _wallCenter;
    public bool    IsCalibrated => _calibrated;

    public CalibratedWall GetNearPlaneAsWall()
    {
        Vector3 normal = flipDirection ? -_wallNormal : _wallNormal;

        float w = mode == FrustumMode.FlatWall ? farWidth  : nearWidth;
        float h = mode == FrustumMode.FlatWall ? farHeight : nearHeight;

        return new CalibratedWall
        {
            wallIndex     = 99,
            center        = _wallCenter,
            normal        = -normal,
            localRight    = _wallRight,
            localUp       = _wallUp,
            width         = w,
            height        = h,
            samplePoints  = new List<Vector3>(),
            sampleNormals = new List<Vector3>()
        };
    }

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
            Phase.Done     => $"FRUSTUM READY  |  {mode}" +
                              $"{(flipDirection ? " Flipped" : "")}\n" +
                              "[A] Flip  [Stick] Flat/Frustum  " +
                              "[B] Re-calibrate  [Grip] Reset",
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

        if (mat.HasProperty("_Mode"))
        {
            mat.SetFloat("_Mode", 3);
            mat.SetInt("_SrcBlend",
                (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend",
                (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = 3000;
        }

        if (mat.HasProperty("_Cull"))
            mat.SetFloat("_Cull", 0);

        return mat;
    }
}