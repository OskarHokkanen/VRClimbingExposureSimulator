using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using TMPro;

/*
 * -----------------------------
 * -----------------------------
 * NOT IN USED ANYMORE, OLD TEST
 * -----------------------------
 * -----------------------------
 */
public class WallCalibrationManager : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The OVRCameraRig or XR Origin in your scene")]
    public Transform xrOrigin;

    [Tooltip("Right controller transform (e.g., RightHandAnchor)")]
    public Transform rightController;

    [Tooltip("Left controller transform (e.g., LeftHandAnchor)")]
    public Transform leftController;

    [Header("Calibration Settings")]
    [Tooltip("Minimum points required to finalize a wall")]
    public int minPointsPerWall = 3;

    [Tooltip("Maximum number of walls that can be calibrated")]
    public int maxWalls = 6;

    [Tooltip("Which hand to use for calibration")]
    public ControllerHand activeHand = ControllerHand.Right;

    [Header("Visual Feedback")]
    [Tooltip("Prefab for the small sphere shown at each sampled point")]
    public GameObject samplePointMarkerPrefab;

    [Tooltip("Material for the wall preview mesh")]
    public Material wallPreviewMaterial;

    [Tooltip("Material for the finalized wall mesh")]
    public Material wallFinalizedMaterial;

    [Tooltip("Optional TextMeshPro for status display (world-space canvas)")]
    public TextMeshProUGUI statusText;

    [Header("Audio Feedback")]
    public AudioClip sampleSound;
    public AudioClip finalizeSound;
    public AudioClip errorSound;

    // ── State ──
    public enum CalibrationState { Idle, Sampling, Reviewing }
    public CalibrationState State { get; private set; } = CalibrationState.Idle;

    private List<CalibratedWall> _walls = new List<CalibratedWall>();
    private List<Vector3> _currentSamples = new List<Vector3>();
    private List<Vector3> _currentNormals = new List<Vector3>();
    private List<GameObject> _currentMarkers = new List<GameObject>();
    private GameObject _previewWallObj;
    private AudioSource _audioSource;

    // Input tracking (to detect press vs hold)
    private bool _triggerWasPressed;
    private bool _primaryWasPressed; // A or X button
    private bool _secondaryWasPressed; // B or Y button

    public IReadOnlyList<CalibratedWall> Walls => _walls;

    public enum ControllerHand { Right, Left }

    // ────────────────────────────────────────────────────────────
    // Unity Lifecycle
    // ────────────────────────────────────────────────────────────

    void Start()
    {
        _audioSource = gameObject.AddComponent<AudioSource>();
        _audioSource.playOnAwake = false;
        _audioSource.spatialBlend = 0f;

        if (xrOrigin == null)
            xrOrigin = Camera.main?.transform.parent;

        BeginNewWall();
    }

    void Update()
    {
        ReadInputAndProcess();
        UpdatePreview();
        UpdateStatusText();
    }

    // ────────────────────────────────────────────────────────────
    // Input Handling
    // ────────────────────────────────────────────────────────────

    void ReadInputAndProcess()
    {
        InputDevice device = GetActiveDevice();
        if (!device.isValid) return;

        // Trigger → sample a point
        device.TryGetFeatureValue(CommonUsages.triggerButton, out bool triggerPressed);
        if (triggerPressed && !_triggerWasPressed && State == CalibrationState.Sampling)
        {
            SamplePoint();
        }
        _triggerWasPressed = triggerPressed;

        // Primary button (A / X) → finalize current wall
        device.TryGetFeatureValue(CommonUsages.primaryButton, out bool primaryPressed);
        if (primaryPressed && !_primaryWasPressed)
        {
            if (State == CalibrationState.Sampling && _currentSamples.Count >= minPointsPerWall)
                FinalizeCurrentWall();
            else if (State == CalibrationState.Sampling)
                PlaySound(errorSound); // not enough points
        }
        _primaryWasPressed = primaryPressed;

        // Secondary button (B / Y) → undo last sample or delete last wall
        device.TryGetFeatureValue(CommonUsages.secondaryButton, out bool secondaryPressed);
        if (secondaryPressed && !_secondaryWasPressed)
        {
            if (_currentSamples.Count > 0)
                UndoLastSample();
            else if (_walls.Count > 0)
                UndoLastWall();
        }
        _secondaryWasPressed = secondaryPressed;
    }

    InputDevice GetActiveDevice()
    {
        var characteristics = (activeHand == ControllerHand.Right)
            ? InputDeviceCharacteristics.Right | InputDeviceCharacteristics.Controller
            : InputDeviceCharacteristics.Left | InputDeviceCharacteristics.Controller;

        var devices = new List<InputDevice>();
        InputDevices.GetDevicesWithCharacteristics(characteristics, devices);
        return devices.Count > 0 ? devices[0] : default;
    }

    Transform GetActiveControllerTransform()
    {
        return (activeHand == ControllerHand.Right) ? rightController : leftController;
    }

    // ────────────────────────────────────────────────────────────
    // Sampling
    // ────────────────────────────────────────────────────────────

    public void BeginNewWall()
    {
        if (_walls.Count >= maxWalls)
        {
            Debug.LogWarning("Maximum wall count reached.");
            return;
        }

        _currentSamples.Clear();
        _currentNormals.Clear();
        ClearCurrentMarkers();
        State = CalibrationState.Sampling;
    }

    void SamplePoint()
    {
        Transform ctrl = GetActiveControllerTransform();
        if (ctrl == null) return;

        Vector3 position = ctrl.position;

        // When you press the controller flat against a wall,
        // the controller's -forward axis (back of the controller / palm side)
        // approximates the wall's outward normal.
        Vector3 normal = -ctrl.forward;

        _currentSamples.Add(position);
        _currentNormals.Add(normal);

        // Visual marker
        if (samplePointMarkerPrefab != null)
        {
            var marker = Instantiate(samplePointMarkerPrefab, position, Quaternion.identity);
            marker.transform.localScale = Vector3.one * 0.02f;
            _currentMarkers.Add(marker);
        }
        else
        {
            // Fallback: create a small sphere
            var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.transform.position = position;
            marker.transform.localScale = Vector3.one * 0.02f;
            marker.GetComponent<Renderer>().material.color = Color.cyan;
            Destroy(marker.GetComponent<Collider>());
            _currentMarkers.Add(marker);
        }

        PlaySound(sampleSound);
        Debug.Log($"Sampled point #{_currentSamples.Count} at {position}, normal ≈ {normal}");
    }

    void UndoLastSample()
    {
        if (_currentSamples.Count == 0) return;

        _currentSamples.RemoveAt(_currentSamples.Count - 1);
        _currentNormals.RemoveAt(_currentNormals.Count - 1);

        if (_currentMarkers.Count > 0)
        {
            Destroy(_currentMarkers[_currentMarkers.Count - 1]);
            _currentMarkers.RemoveAt(_currentMarkers.Count - 1);
        }
    }

    // ────────────────────────────────────────────────────────────
    // Finalization – Plane Fitting
    // ────────────────────────────────────────────────────────────

    void FinalizeCurrentWall()
    {
        if (_currentSamples.Count < minPointsPerWall)
        {
            Debug.LogWarning($"Need at least {minPointsPerWall} points.");
            return;
        }

        // 1. Fit a plane through the sampled positions
        PlaneFit.FitPlane(_currentSamples, out Vector3 centroid, out Vector3 fittedNormal);

        // 2. Orient the normal to agree with the average sampled normals
        //    (so it points outward, away from the wall surface toward the climber)
        Vector3 avgNormal = Vector3.zero;
        foreach (var n in _currentNormals) avgNormal += n;
        avgNormal.Normalize();

        if (Vector3.Dot(fittedNormal, avgNormal) < 0)
            fittedNormal = -fittedNormal;

        // 3. Compute wall extents from projected sample points
        ComputeWallFrame(fittedNormal, centroid, out Vector3 wallRight, out Vector3 wallUp);
        ComputeWallExtents(_currentSamples, centroid, wallRight, wallUp,
            out float width, out float height, out Vector3 adjustedCenter);

        // 4. Build the CalibratedWall
        var wall = new CalibratedWall
        {
            wallIndex = _walls.Count,
            center = adjustedCenter,
            normal = fittedNormal,
            localRight = wallRight,
            localUp = wallUp,
            width = Mathf.Max(width, 0.5f),   // minimum 0.5 m
            height = Mathf.Max(height, 0.5f),
            samplePoints = new List<Vector3>(_currentSamples),
            sampleNormals = new List<Vector3>(_currentNormals),
            calibrationTimestamp = Time.time
        };

        _walls.Add(wall);

        // 5. Create finalized wall visual
        CreateWallVisual(wall, true);

        // 6. Clean up sampling state
        ClearCurrentMarkers();
        if (_previewWallObj != null) Destroy(_previewWallObj);

        PlaySound(finalizeSound);
        Debug.Log($"Wall #{wall.wallIndex} finalized: center={wall.center}, " +
                  $"normal={wall.normal}, size={wall.width:F2}×{wall.height:F2}m");

        // Auto-start next wall
        BeginNewWall();
    }

    /// <summary>
    /// Derive a right and up vector for the wall from its normal.
    /// "Up" is biased toward world-up so walls look natural.
    /// </summary>
    void ComputeWallFrame(Vector3 normal, Vector3 centroid,
        out Vector3 wallRight, out Vector3 wallUp)
    {
        // If the wall is nearly horizontal (floor/ceiling), use world-forward as fallback
        Vector3 refUp = (Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > 0.95f)
            ? Vector3.forward
            : Vector3.up;

        wallRight = Vector3.Cross(refUp, normal).normalized;
        wallUp = Vector3.Cross(normal, wallRight).normalized;
    }

    /// <summary>
    /// Project sample points onto the wall plane to compute width, height,
    /// and a centered position.
    /// </summary>
    void ComputeWallExtents(List<Vector3> samples, Vector3 centroid,
        Vector3 right, Vector3 up,
        out float width, out float height, out Vector3 adjustedCenter)
    {
        float minR = float.MaxValue, maxR = float.MinValue;
        float minU = float.MaxValue, maxU = float.MinValue;

        foreach (var p in samples)
        {
            Vector3 offset = p - centroid;
            float r = Vector3.Dot(offset, right);
            float u = Vector3.Dot(offset, up);
            minR = Mathf.Min(minR, r);
            maxR = Mathf.Max(maxR, r);
            minU = Mathf.Min(minU, u);
            maxU = Mathf.Max(maxU, u);
        }

        width = maxR - minR;
        height = maxU - minU;

        // Shift center to the midpoint of the bounding rectangle
        float midR = (minR + maxR) * 0.5f;
        float midU = (minU + maxU) * 0.5f;
        adjustedCenter = centroid + right * midR + up * midU;
    }

    // ────────────────────────────────────────────────────────────
    // Preview & Visualization
    // ────────────────────────────────────────────────────────────

    void UpdatePreview()
    {
        if (State != CalibrationState.Sampling || _currentSamples.Count < 3)
        {
            if (_previewWallObj != null) _previewWallObj.SetActive(false);
            return;
        }

        // Live preview of the wall being calibrated
        PlaneFit.FitPlane(_currentSamples, out Vector3 centroid, out Vector3 normal);

        Vector3 avgN = Vector3.zero;
        foreach (var n in _currentNormals) avgN += n;
        if (Vector3.Dot(normal, avgN) < 0) normal = -normal;

        ComputeWallFrame(normal, centroid, out Vector3 right, out Vector3 up);
        ComputeWallExtents(_currentSamples, centroid, right, up,
            out float w, out float h, out Vector3 center);

        w = Mathf.Max(w, 0.3f);
        h = Mathf.Max(h, 0.3f);

        if (_previewWallObj == null)
        {
            _previewWallObj = CreateWallQuad("WallPreview", wallPreviewMaterial);
        }

        _previewWallObj.SetActive(true);
        _previewWallObj.transform.position = center;
        _previewWallObj.transform.rotation = Quaternion.LookRotation(-normal, up);
        _previewWallObj.transform.localScale = new Vector3(w, h, 1f);
    }

    GameObject CreateWallQuad(string name, Material mat)
    {
        var obj = GameObject.CreatePrimitive(PrimitiveType.Quad);
        obj.name = name;
        Destroy(obj.GetComponent<Collider>());

        if (mat != null)
            obj.GetComponent<Renderer>().material = mat;
        else
        {
            var m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            m.color = new Color(0.2f, 0.6f, 1f, 0.35f);
            SetMaterialTransparent(m);
            obj.GetComponent<Renderer>().material = m;
        }

        return obj;
    }

    void CreateWallVisual(CalibratedWall wall, bool finalized)
    {
        Material mat = finalized ? wallFinalizedMaterial : wallPreviewMaterial;
        var obj = CreateWallQuad($"Wall_{wall.wallIndex}", mat);

        obj.transform.position = wall.center;
        obj.transform.rotation = Quaternion.LookRotation(-wall.normal, wall.localUp);
        obj.transform.localScale = new Vector3(wall.width, wall.height, 1f);

        wall.visualObject = obj;
    }

    void SetMaterialTransparent(Material mat)
    {
        mat.SetFloat("_Surface", 1); // Transparent
        mat.SetFloat("_Blend", 0);
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);
        mat.DisableKeyword("_ALPHATEST_ON");
        mat.EnableKeyword("_ALPHABLEND_ON");
        mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        mat.renderQueue = 3000;
    }

    // ────────────────────────────────────────────────────────────
    // Undo / Reset
    // ────────────────────────────────────────────────────────────

    void UndoLastWall()
    {
        if (_walls.Count == 0) return;
        var last = _walls[_walls.Count - 1];
        if (last.visualObject != null) Destroy(last.visualObject);
        _walls.RemoveAt(_walls.Count - 1);
    }

    public void ResetAll()
    {
        foreach (var w in _walls)
            if (w.visualObject != null) Destroy(w.visualObject);
        _walls.Clear();
        ClearCurrentMarkers();
        if (_previewWallObj != null) Destroy(_previewWallObj);
        State = CalibrationState.Idle;
    }

    void ClearCurrentMarkers()
    {
        foreach (var m in _currentMarkers)
            if (m != null) Destroy(m);
        _currentMarkers.Clear();
    }

    // ────────────────────────────────────────────────────────────
    // Status Display
    // ────────────────────────────────────────────────────────────

    void UpdateStatusText()
    {
        if (statusText == null) return;

        string hand = activeHand == ControllerHand.Right ? "RIGHT" : "LEFT";
        string status = State switch
        {
            CalibrationState.Idle => "Press trigger to begin",
            CalibrationState.Sampling =>
                $"Wall #{_walls.Count}  |  Points: {_currentSamples.Count}/{minPointsPerWall}+\n" +
                $"[Trigger] Sample  [{(activeHand == ControllerHand.Right ? "A" : "X")}] Finalize  " +
                $"[{(activeHand == ControllerHand.Right ? "B" : "Y")}] Undo",
            _ => ""
        };

        string wallInfo = "";
        for (int i = 0; i < _walls.Count; i++)
        {
            var w = _walls[i];
            float tiltAngle = Vector3.Angle(w.normal, Vector3.back); // angle from vertical
            float verticalAngle = 90f - Vector3.Angle(w.normal, Vector3.up);
            wallInfo += $"\nWall {i}: tilt={verticalAngle:F1}° size={w.width:F2}×{w.height:F2}m";
        }

        statusText.text = $"WALL CALIBRATION ({hand} hand)\n{status}{wallInfo}";
    }

    // ────────────────────────────────────────────────────────────
    // Utility
    // ────────────────────────────────────────────────────────────

    void PlaySound(AudioClip clip)
    {
        if (clip != null && _audioSource != null)
            _audioSource.PlayOneShot(clip);
    }

    /// <summary>
    /// Get the angle between two calibrated walls (angle between their normals).
    /// </summary>
    public float GetAngleBetweenWalls(int indexA, int indexB)
    {
        if (indexA < 0 || indexA >= _walls.Count || indexB < 0 || indexB >= _walls.Count)
            return -1f;

        return Vector3.Angle(_walls[indexA].normal, _walls[indexB].normal);
    }

    /// <summary>
    /// Get the dihedral angle at the junction of two walls
    /// (the inside angle a climber would experience in a corner).
    /// </summary>
    public float GetDihedralAngle(int indexA, int indexB)
    {
        return 180f - GetAngleBetweenWalls(indexA, indexB);
    }

    // ────────────────────────────────────────────────────────────
    // Serialization helpers (for save/load later)
    // ────────────────────────────────────────────────────────────

    public string SerializeWalls()
    {
        var data = new WallCalibrationData { walls = _walls };
        return JsonUtility.ToJson(data, true);
    }

    public void DeserializeWalls(string json)
    {
        ResetAll();
        var data = JsonUtility.FromJson<WallCalibrationData>(json);
        if (data?.walls == null) return;

        foreach (var w in data.walls)
        {
            _walls.Add(w);
            CreateWallVisual(w, true);
        }
    }

    [System.Serializable]
    private class WallCalibrationData
    {
        public List<CalibratedWall> walls;
    }
}
