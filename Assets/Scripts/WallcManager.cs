using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;

public class WallCManager : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────────────────

    [Header("XR Controllers")]
    public Transform leftControllerTransform;
    public Transform rightControllerTransform;

    [Header("Wall")]
    public Transform wallRoot;
    public Transform[] virtualReferencePoints = new Transform[3];

    [Header("Frustum Integration")]
    [Tooltip("After scan calibration the frustum will automatically snap " +
             "to the physical wall position and normal.")]
    public WallFrustumCalibrator frustumCalibrator;
    [Tooltip("Main Camera — used to orient the wall normal toward the player.")]
    public Transform headTransform;

    [Header("Audio (optional)")]
    public AudioSource audioSource;
    public AudioClip pointPlacedClip;
    public AudioClip calibrationCompleteClip;
    public AudioClip calibrationStartedClip;

    [Header("Debug HUD")]
    public float hudDistance = 1.2f;

    // ── Private state ─────────────────────────────────────────────────────────

    private InputDevice leftDevice;
    private InputDevice rightDevice;
    private bool leftGripPrev;
    private bool rightGripPrev;

    private enum CalibState { Idle, Collecting }
    private CalibState calibState = CalibState.Idle;
    private readonly List<Vector3> physicalPoints = new List<Vector3>();

    private GameObject hudRoot;
    private Text hudText;
    private Camera headCamera;
    private readonly GameObject[] pointSpheres = new GameObject[3];

    // Exposed for other scripts
    public Vector3 CalibratedWallNormal  { get; private set; }
    public Vector3 CalibratedWallCenter  { get; private set; }
    public bool    IsScanCalibrated      { get; private set; }

    private const string FileName = "wall_calibration.json";
    private string SavePath => Path.Combine(Application.persistentDataPath, FileName);

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    void Start()
    {
        headCamera = Camera.main;
        //BuildHUD();
        CreatePointSpheres();

        // if (!ValidateSetup())
        // {
        //     SetHUD("<color=red>SETUP ERROR\nCheck Inspector</color>");
        //     return;
        // }

        // if (TryLoadCalibration())
        // {
        //     //SetHUD("<color=lime>Calibration loaded!\n\nPress GRIP to recalibrate.</color>");
        //     // Re-derive wall normal from loaded transform so frustum can use it
        //     NotifyFrustumCalibrator();
        // }
        
    }

    void Update()
    {
        RefreshDevices();
        HandleGripInput();
        //UpdateHUDPosition();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void StartCalibration()
    {
        physicalPoints.Clear();
        calibState = CalibState.Idle;
        IsScanCalibrated = false;
        ResetPointSpheres();
        SetHUD("<color=yellow>Ready to recalibrate.\n\nPress GRIP at\nReference Point 0</color>");
    }

    public void ClearCalibration()
    {
        if (File.Exists(SavePath)) File.Delete(SavePath);
        wallRoot.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        wallRoot.localScale = Vector3.one;
        physicalPoints.Clear();
        calibState = CalibState.Idle;
        IsScanCalibrated = false;
        ResetPointSpheres();
        SetHUD("<color=orange>Calibration cleared.\n\nPress GRIP at\nReference Point 0</color>");
    }

    // ── Input ─────────────────────────────────────────────────────────────────

    void RefreshDevices()
    {
        TryRefreshDevice(ref leftDevice,  InputDeviceCharacteristics.Left);
        TryRefreshDevice(ref rightDevice, InputDeviceCharacteristics.Right);
    }

    static void TryRefreshDevice(ref InputDevice device,
        InputDeviceCharacteristics side)
    {
        if (device.isValid) return;
        var found = new List<InputDevice>();
        InputDevices.GetDevicesWithCharacteristics(
            side | InputDeviceCharacteristics.Controller, found);
        if (found.Count > 0) device = found[0];
    }

    void HandleGripInput()
    {
        if (GripRisingEdge(leftDevice, ref leftGripPrev))
        {
            OnGripPressed(GetControllerWorldPos(
                leftControllerTransform, leftDevice, "Left"));
            return;
        }
        if (GripRisingEdge(rightDevice, ref rightGripPrev))
        {
            OnGripPressed(GetControllerWorldPos(
                rightControllerTransform, rightDevice, "Right"));
        }
    }

    static bool GripRisingEdge(InputDevice device, ref bool prevState)
    {
        if (!device.isValid) { prevState = false; return false; }
        device.TryGetFeatureValue(CommonUsages.gripButton, out bool pressed);
        bool rising = pressed && !prevState;
        prevState = pressed;
        return rising;
    }

    static Vector3 GetControllerWorldPos(Transform t, InputDevice device,
        string side)
    {
        if (t != null) return t.position;
        device.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 pos);
        Debug.LogWarning($"[WallCalib] {side} Transform not assigned, " +
                          "using raw tracking position.");
        return pos;
    }

    // ── Calibration flow ──────────────────────────────────────────────────────

    void OnGripPressed(Vector3 worldPos)
    {
        if (calibState == CalibState.Idle)
        {
            calibState = CalibState.Collecting;
            physicalPoints.Clear();
            ResetPointSpheres();
            PlayClip(calibrationStartedClip);
        }

        int idx = physicalPoints.Count;
        physicalPoints.Add(worldPos);
        ShowPointSphere(idx, worldPos);
        PlayClip(pointPlacedClip);

        if (physicalPoints.Count < 3)
        {
            int next = physicalPoints.Count;
            SetHUD($"<color=lime>Point {idx} placed!</color>\n\n" +
                   $"<color=yellow>Press GRIP at\nReference Point {next}</color>");
            return;
        }

        SetHUD("<color=cyan>Computing...</color>");
        bool success = ComputeAndApply();
        calibState = CalibState.Idle;

        if (success)
        {
            PlayClip(calibrationCompleteClip);
            SetHUD($"<color=lime>CALIBRATED!\n\nSaved!\nPress GRIP to redo.</color>");
            NotifyFrustumCalibrator();
        }
        else
        {
            physicalPoints.Clear();
            ResetPointSpheres();
            SetHUD("<color=red>FAILED!\nPoints may be collinear.\n\n" +
                   "Press GRIP to try again.</color>");
        }
    }

    // ── Transform math ────────────────────────────────────────────────────────

    bool ComputeAndApply()
    {
        Vector3 p0 = physicalPoints[0];
        Vector3 p1 = physicalPoints[1];
        Vector3 p2 = physicalPoints[2];

        Vector3 lq0 = virtualReferencePoints[0].localPosition;
        Vector3 lq1 = virtualReferencePoints[1].localPosition;
        Vector3 lq2 = virtualReferencePoints[2].localPosition;

        float scale = 1f;

        Vector3 px     = (p1 - p0).normalized;
        Vector3 pCross = Vector3.Cross(px, p2 - p0);
        if (pCross.sqrMagnitude < 0.001f)
        {
            Debug.LogError("[WallCalib] Physical points nearly collinear.");
            return false;
        }
        Vector3 pz = pCross.normalized;
        Vector3 py = Vector3.Cross(pz, px);

        Vector3 vx     = (lq1 - lq0).normalized;
        Vector3 vCross = Vector3.Cross(vx, lq2 - lq0);
        if (vCross.sqrMagnitude < 0.001f)
        {
            Debug.LogError("[WallCalib] Virtual points nearly collinear.");
            return false;
        }
        Vector3 vz = vCross.normalized;
        Vector3 vy = Vector3.Cross(vz, vx);

        Matrix4x4 Mphys = FrameMatrix(px, py, pz);
        Matrix4x4 Mvirt = FrameMatrix(vx, vy, vz);
        Quaternion rot  = (Mphys * Mvirt.transpose).rotation;

        Vector3 pos = p0 - rot * (lq0 * scale);

        wallRoot.localScale = Vector3.one * scale;
        wallRoot.SetPositionAndRotation(pos, rot);

        // Store wall normal (pz) and center (centroid of grip points)
        // Orient normal toward the player if headTransform is assigned
        Vector3 centroid = (p0 + p1 + p2) / 3f;
        Vector3 normal   = pz;

        if (headTransform != null)
        {
            Vector3 toHead = headTransform.position - centroid;
            if (Vector3.Dot(normal, toHead) < 0f)
                normal = -normal;
        }

        CalibratedWallNormal = normal;
        CalibratedWallCenter = centroid;
        IsScanCalibrated     = true;

        SaveCalibration(scale, pos, rot);
        return true;
    }

    /// <summary>
    /// Tells the frustum calibrator to snap to the physical wall surface
    /// using the normal and center derived from the 3D scan calibration.
    /// </summary>
    void NotifyFrustumCalibrator()
    {
        if (frustumCalibrator == null) return;
        if (!IsScanCalibrated) return;

        frustumCalibrator.CalibrateFromScan(
            CalibratedWallCenter,
            CalibratedWallNormal);
    }

    static Matrix4x4 FrameMatrix(Vector3 x, Vector3 y, Vector3 z)
    {
        var m = Matrix4x4.identity;
        m.SetColumn(0, new Vector4(x.x, x.y, x.z, 0));
        m.SetColumn(1, new Vector4(y.x, y.y, y.z, 0));
        m.SetColumn(2, new Vector4(z.x, z.y, z.z, 0));
        return m;
    }

    // ── Save / Load ───────────────────────────────────────────────────────────

    void SaveCalibration(float scale, Vector3 position, Quaternion rotation)
    {
        var data = new CalibrationData
        {
            scale    = scale,
            position = new SerializableVector3(position),
            rotation = new SerializableQuaternion(rotation)
        };
        try
        {
            File.WriteAllText(SavePath,
                JsonUtility.ToJson(data, prettyPrint: true));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[WallCalib] Save failed: {e.Message}");
        }
    }

    bool TryLoadCalibration()
    {
        if (!File.Exists(SavePath)) return false;
        try
        {
            var data = JsonUtility.FromJson<CalibrationData>(
                File.ReadAllText(SavePath));
            wallRoot.localScale = Vector3.one * data.scale;
            wallRoot.SetPositionAndRotation(
                data.position.ToVector3(),
                data.rotation.ToQuaternion());

            // Derive wall normal from loaded rotation:
            // use the forward axis of wallRoot as the outward normal
            CalibratedWallNormal = wallRoot.forward;
            CalibratedWallCenter = wallRoot.position;

            // Orient toward player if possible
            if (headTransform != null)
            {
                Vector3 toHead = headTransform.position - CalibratedWallCenter;
                if (Vector3.Dot(CalibratedWallNormal, toHead) < 0f)
                    CalibratedWallNormal = -CalibratedWallNormal;
            }

            IsScanCalibrated = true;
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[WallCalib] Load failed: {e.Message}");
            return false;
        }
    }

    // ── HUD ───────────────────────────────────────────────────────────────────

    void BuildHUD()
    {
        hudRoot = new GameObject("CalibrationHUD");
        DontDestroyOnLoad(hudRoot);

        var canvas = hudRoot.AddComponent<Canvas>();
        canvas.renderMode  = RenderMode.WorldSpace;
        canvas.worldCamera = headCamera;

        var scaler = hudRoot.AddComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = 1f;

        hudRoot.AddComponent<GraphicRaycaster>();

        var rt = hudRoot.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(2000f, 1200f);
        hudRoot.transform.localScale = Vector3.one * 0.0005f;

        var bgGO = new GameObject("BG");
        bgGO.transform.SetParent(hudRoot.transform, false);
        var bg = bgGO.AddComponent<Image>();
        bg.color = new Color(0f, 0f, 0f, 0.85f);
        var bgRt = bgGO.GetComponent<RectTransform>();
        bgRt.anchorMin = Vector2.zero;
        bgRt.anchorMax = Vector2.one;
        bgRt.offsetMin = bgRt.offsetMax = Vector2.zero;

        var textGO = new GameObject("StatusText");
        textGO.transform.SetParent(hudRoot.transform, false);
        hudText = textGO.AddComponent<Text>();
        hudText.font            = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        hudText.fontSize        = 110;
        hudText.alignment       = TextAnchor.MiddleCenter;
        hudText.color           = Color.white;
        hudText.supportRichText = true;
        hudText.text            = "Initialising...";

        var textRt = textGO.GetComponent<RectTransform>();
        textRt.anchorMin = Vector2.zero;
        textRt.anchorMax = Vector2.one;
        textRt.offsetMin = new Vector2(60, 60);
        textRt.offsetMax = new Vector2(-60, -60);
    }

    void SetHUD(string msg) { if (hudText != null) hudText.text = msg; }

    void UpdateHUDPosition()
    {
        if (hudRoot == null || headCamera == null) return;
        Vector3 fwd = headCamera.transform.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.01f) fwd = headCamera.transform.forward;
        fwd.Normalize();
        Vector3 target = headCamera.transform.position
                        + fwd * hudDistance + Vector3.down * 0.2f;
        hudRoot.transform.position = Vector3.Lerp(
            hudRoot.transform.position, target, Time.deltaTime * 4f);
        hudRoot.transform.rotation = Quaternion.LookRotation(fwd);
    }

    // ── Point spheres ─────────────────────────────────────────────────────────

    void CreatePointSpheres()
    {
        Color[] cols = { Color.cyan, new Color(1f, 0.5f, 0f), Color.green };
        for (int i = 0; i < 3; i++)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = $"GripPoint_{i}";
            go.transform.localScale = Vector3.one * 0.06f;
            Destroy(go.GetComponent<Collider>());
            var shader = Shader.Find("Universal Render Pipeline/Lit")
                      ?? Shader.Find("Standard");
            go.GetComponent<Renderer>().material =
                new Material(shader) { color = cols[i] };
            go.SetActive(false);
            pointSpheres[i] = go;
        }
    }

    void ShowPointSphere(int idx, Vector3 pos)
    {
        if (idx < 0 || idx >= 3 || pointSpheres[idx] == null) return;
        pointSpheres[idx].transform.position = pos;
        pointSpheres[idx].SetActive(true);
    }

    void ResetPointSpheres()
    {
        foreach (var s in pointSpheres)
            if (s != null) s.SetActive(false);
    }

    // ── Validation ────────────────────────────────────────────────────────────

    bool ValidateSetup()
    {
        if (wallRoot == null)
        {
            Debug.LogError("[WallCalib] wallRoot not assigned."); return false;
        }
        if (virtualReferencePoints == null || virtualReferencePoints.Length != 3)
        {
            Debug.LogError("[WallCalib] Need exactly 3 reference points."); return false;
        }
        for (int i = 0; i < 3; i++)
        {
            if (virtualReferencePoints[i] == null)
            {
                Debug.LogError($"[WallCalib] virtualReferencePoints[{i}] is null.");
                return false;
            }
        }
        if (headTransform == null)
            Debug.LogWarning("[WallCalib] headTransform not assigned — " +
                             "wall normal may point the wrong way.");
        return true;
    }

    void PlayClip(AudioClip clip)
    {
        if (audioSource != null && clip != null)
            audioSource.PlayOneShot(clip);
    }
}