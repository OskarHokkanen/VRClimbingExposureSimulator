using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;

/// <summary>
/// Calibrates a virtual climbing wall to its physical counterpart using 3 grip presses.
///
/// SETUP:
///   1. Attach this script to any persistent GameObject.
///   2. Assign wallRoot → the root Transform of your wall mesh.
///   3. Create 3 empty GameObjects as DIRECT children of wallRoot,
///      add WallReferencePoint to each (index 0,1,2), position on known holds.
///   4. Drag those 3 Transforms into virtualReferencePoints[0..2]. Order matters.
///   5. Drag your Left/Right controller GameObjects into the XR fields.
///
/// CALIBRATION FLOW:
///   - Press Grip at reference spot 0, then 1, then 2 on the real wall.
///   - The wall snaps into place and calibration is saved to JSON.
///   - On next launch the wall is positioned automatically from the save file.
///   - To redo: press Grip again (state resets after each completed calibration).
/// </summary>
public class WallCManager : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────────────────

    [Header("XR Controllers")]
    [Tooltip("The Left Controller GameObject from your XR Rig (e.g. Camera Offset > Left Controller).")]
    public Transform leftControllerTransform;
    [Tooltip("The Right Controller GameObject from your XR Rig (e.g. Camera Offset > Right Controller).")]
    public Transform rightControllerTransform;

    [Header("Wall")]
    [Tooltip("Root Transform of the 3D wall scan. This is what gets moved by calibration.")]
    public Transform wallRoot;
    [Tooltip("Exactly 3 Transforms placed on known spots of the wall mesh (direct children of wallRoot).")]
    public Transform[] virtualReferencePoints = new Transform[3];

    [Header("Audio (optional)")]
    public AudioSource audioSource;
    public AudioClip pointPlacedClip;
    public AudioClip calibrationCompleteClip;
    public AudioClip calibrationStartedClip;

    [Header("Debug HUD")]
    [Tooltip("How far in front of your head the HUD floats (metres).")]
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

    private const string FileName = "wall_calibration.json";
    private string SavePath => Path.Combine(Application.persistentDataPath, FileName);

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    void Start()
    {
        headCamera = Camera.main;
        BuildHUD();
        CreatePointSpheres();

        Debug.Log("[WallCalib] Start() called.");

        if (!ValidateSetup())
        {
            SetHUD("<color=red>SETUP ERROR\nCheck Inspector:\n- wallRoot assigned?\n- 3 ref points assigned?</color>");
            Debug.LogError("[WallCalib] SETUP ERROR - check wallRoot and virtualReferencePoints in Inspector.");
            return;
        }

        Debug.Log("[WallCalib] Setup valid. Checking for saved calibration...");

        if (TryLoadCalibration())
        {
            SetHUD("<color=lime>Calibration loaded!\nWall is positioned.\n\nPress GRIP to recalibrate.</color>");
            Debug.Log("[WallCalib] Saved calibration applied.");
        }
        else
        {
            SetHUD("<color=yellow>No calibration saved.\n\nPress GRIP at\nReference Point 0</color>");
            Debug.Log("[WallCalib] No save file. Press Grip at Reference Point 0 to begin.");
        }
    }

    void Update()
    {
        RefreshDevices();
        HandleGripInput();
        UpdateHUDPosition();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Call from a UI button to restart the calibration process.</summary>
    public void StartCalibration()
    {
        physicalPoints.Clear();
        calibState = CalibState.Idle;
        ResetPointSpheres();
        SetHUD("<color=yellow>Ready to recalibrate.\n\nPress GRIP at\nReference Point 0</color>");
        Debug.Log("[WallCalib] StartCalibration() called - ready for grip at point 0.");
    }

    /// <summary>Deletes the save file and resets the wall transform.</summary>
    public void ClearCalibration()
    {
        if (File.Exists(SavePath)) File.Delete(SavePath);
        wallRoot.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        wallRoot.localScale = Vector3.one;
        physicalPoints.Clear();
        calibState = CalibState.Idle;
        ResetPointSpheres();
        SetHUD("<color=orange>Calibration cleared.\n\nPress GRIP at\nReference Point 0</color>");
        Debug.Log("[WallCalib] Calibration cleared.");
    }

    // ── Input ─────────────────────────────────────────────────────────────────

    void RefreshDevices()
    {
        TryRefreshDevice(ref leftDevice,  InputDeviceCharacteristics.Left);
        TryRefreshDevice(ref rightDevice, InputDeviceCharacteristics.Right);
    }

    static void TryRefreshDevice(ref InputDevice device, InputDeviceCharacteristics side)
    {
        if (device.isValid) return;
        var found = new List<InputDevice>();
        InputDevices.GetDevicesWithCharacteristics(side | InputDeviceCharacteristics.Controller, found);
        if (found.Count > 0)
        {
            device = found[0];
            Debug.Log($"[WallCalib] Controller found: {device.name} ({side})");
        }
    }

    void HandleGripInput()
    {
        if (GripRisingEdge(leftDevice, ref leftGripPrev))
        {
            OnGripPressed(GetControllerWorldPos(leftControllerTransform, leftDevice, "Left"));
            return;
        }
        if (GripRisingEdge(rightDevice, ref rightGripPrev))
        {
            OnGripPressed(GetControllerWorldPos(rightControllerTransform, rightDevice, "Right"));
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

    static Vector3 GetControllerWorldPos(Transform controllerTransform, InputDevice device, string side)
    {
        // Prefer the GameObject Transform - it is already in world space no matter
        // where XR Origin is placed in the scene.
        if (controllerTransform != null)
            return controllerTransform.position;

        // Fallback: raw tracking-space position (only correct if XR Origin is at world origin).
        device.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 pos);
        Debug.LogWarning($"[WallCalib] {side} controllerTransform not assigned! Raw tracking pos used - assign it in Inspector.");
        return pos;
    }

    // ── Calibration flow ──────────────────────────────────────────────────────

    void OnGripPressed(Vector3 worldPos)
    {
        Debug.Log($"[WallCalib] Grip at world pos {worldPos:F3}");

        // First grip in Idle starts a new session
        if (calibState == CalibState.Idle)
        {
            calibState = CalibState.Collecting;
            physicalPoints.Clear();
            ResetPointSpheres();
            PlayClip(calibrationStartedClip);
            Debug.Log("[WallCalib] New calibration session started.");
        }

        int idx = physicalPoints.Count;
        physicalPoints.Add(worldPos);
        ShowPointSphere(idx, worldPos);
        PlayClip(pointPlacedClip);
        Debug.Log($"[WallCalib] Point {idx} recorded at {worldPos:F3} ({physicalPoints.Count}/3)");

        if (physicalPoints.Count < 3)
        {
            int next = physicalPoints.Count;
            SetHUD($"<color=lime>Point {idx} placed!</color>\n\n<color=yellow>Press GRIP at\nReference Point {next}</color>");
            return;
        }

        // All 3 points collected - compute and apply
        SetHUD("<color=cyan>Computing...</color>");
        Debug.Log("[WallCalib] All 3 points collected. Computing wall transform...");

        bool success = ComputeAndApply();
        calibState = CalibState.Idle; // always reset so next grip starts fresh

        if (success)
        {
            PlayClip(calibrationCompleteClip);
            SetHUD($"<color=lime>CALIBRATED!\n\nScale: {wallRoot.localScale.x:F4}\nPos: {wallRoot.position:F2}\n\nSaved!\nPress GRIP to redo.</color>");
            Debug.Log($"[WallCalib] SUCCESS - Scale={wallRoot.localScale.x:F4} Pos={wallRoot.position:F3} Rot={wallRoot.rotation.eulerAngles:F1}");
        }
        else
        {
            physicalPoints.Clear();
            ResetPointSpheres();
            SetHUD("<color=red>FAILED!\nPoints may be collinear.\nSee error above.\n\nPress GRIP to try again.</color>");
            Debug.LogError("[WallCalib] Calibration failed. Points cleared. Try again with a wider triangle.");
        }
    }

    // ── Transform math ────────────────────────────────────────────────────────

    bool ComputeAndApply()
    {
        Vector3 p0 = physicalPoints[0];
        Vector3 p1 = physicalPoints[1];
        Vector3 p2 = physicalPoints[2];

        // Virtual reference points are in wallRoot local space
        Vector3 lq0 = virtualReferencePoints[0].localPosition;
        Vector3 lq1 = virtualReferencePoints[1].localPosition;
        Vector3 lq2 = virtualReferencePoints[2].localPosition;

        // Scale fixed to 1 - assumes the FBX is imported at correct real-world size.
        // To re-enable auto-scaling, replace this with the distance ratio calculation.
        float scale = 1f;

        // Build physical coordinate frame from the 3 grip positions
        Vector3 px     = (p1 - p0).normalized;
        Vector3 pCross = Vector3.Cross(px, p2 - p0);
        if (pCross.sqrMagnitude < 0.001f)
        {
            Debug.LogError("[WallCalib] Physical grip points are nearly collinear. Use a wider triangle.");
            return false;
        }
        Vector3 pz = pCross.normalized;
        Vector3 py = Vector3.Cross(pz, px);

        // Build virtual coordinate frame from the 3 reference point local positions
        Vector3 vx     = (lq1 - lq0).normalized;
        Vector3 vCross = Vector3.Cross(vx, lq2 - lq0);
        if (vCross.sqrMagnitude < 0.001f)
        {
            Debug.LogError("[WallCalib] Virtual reference points are nearly collinear. Reposition the markers.");
            return false;
        }
        Vector3 vz = vCross.normalized;
        Vector3 vy = Vector3.Cross(vz, vx);

        // Rotation: R maps virtual frame onto physical frame
        // R = M_physical * M_virtual^T  (M_virtual is orthonormal so its transpose = inverse)
        Matrix4x4 Mphys = FrameMatrix(px, py, pz);
        Matrix4x4 Mvirt = FrameMatrix(vx, vy, vz);
        Quaternion rot  = (Mphys * Mvirt.transpose).rotation;

        // Translation: ensure virtual ref point 0 lands exactly at physical grip point 0
        // worldPos = rot * (localPos * scale) + translation  =>  translation = p0 - rot * (lq0 * scale)
        Vector3 pos = p0 - rot * (lq0 * scale);

        wallRoot.localScale = Vector3.one * scale;
        wallRoot.SetPositionAndRotation(pos, rot);

        SaveCalibration(scale, pos, rot);
        return true;
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
            File.WriteAllText(SavePath, JsonUtility.ToJson(data, prettyPrint: true));
            Debug.Log($"[WallCalib] Saved to {SavePath}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[WallCalib] Save failed: {e.Message}");
            SetHUD($"<color=orange>Calibrated but save failed:\n{e.Message}</color>");
        }
    }

    bool TryLoadCalibration()
    {
        if (!File.Exists(SavePath)) return false;
        try
        {
            var data = JsonUtility.FromJson<CalibrationData>(File.ReadAllText(SavePath));
            wallRoot.localScale = Vector3.one * data.scale;
            wallRoot.SetPositionAndRotation(data.position.ToVector3(), data.rotation.ToQuaternion());
            Debug.Log($"[WallCalib] Loaded calibration: scale={data.scale:F4}");
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
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = headCamera;

        // CanvasScaler keeps text sharp at our chosen pixel size
        var scaler = hudRoot.AddComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = 1f;

        hudRoot.AddComponent<GraphicRaycaster>();

        // Canvas is 2000x1200 px, scaled to 1m x 0.6m in world space
        var rt = hudRoot.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(2000f, 1200f);
        hudRoot.transform.localScale = Vector3.one * 0.0005f;

        // Background
        var bgGO = new GameObject("BG");
        bgGO.transform.SetParent(hudRoot.transform, false);
        var bg = bgGO.AddComponent<Image>();
        bg.color = new Color(0f, 0f, 0f, 0.85f);
        var bgRt = bgGO.GetComponent<RectTransform>();
        bgRt.anchorMin = Vector2.zero;
        bgRt.anchorMax = Vector2.one;
        bgRt.offsetMin = bgRt.offsetMax = Vector2.zero;

        // Text
        var textGO = new GameObject("StatusText");
        textGO.transform.SetParent(hudRoot.transform, false);
        hudText = textGO.AddComponent<Text>();
        hudText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        hudText.fontSize = 110;
        hudText.alignment = TextAnchor.MiddleCenter;
        hudText.color = Color.white;
        hudText.supportRichText = true;
        hudText.text = "Initialising...";

        var textRt = textGO.GetComponent<RectTransform>();
        textRt.anchorMin = Vector2.zero;
        textRt.anchorMax = Vector2.one;
        textRt.offsetMin = new Vector2(60, 60);
        textRt.offsetMax = new Vector2(-60, -60);
    }

    void SetHUD(string msg)
    {
        if (hudText != null) hudText.text = msg;
    }

    void UpdateHUDPosition()
    {
        if (hudRoot == null || headCamera == null) return;

        Vector3 fwd = headCamera.transform.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.01f) fwd = headCamera.transform.forward;
        fwd.Normalize();

        Vector3 target = headCamera.transform.position + fwd * hudDistance + Vector3.down * 0.2f;
        hudRoot.transform.position = Vector3.Lerp(hudRoot.transform.position, target, Time.deltaTime * 4f);
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
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            go.GetComponent<Renderer>().material = new Material(shader) { color = cols[i] };
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
            Debug.LogError("[WallCalib] wallRoot is not assigned.");
            return false;
        }
        if (virtualReferencePoints == null || virtualReferencePoints.Length != 3)
        {
            Debug.LogError("[WallCalib] virtualReferencePoints must have exactly 3 entries.");
            return false;
        }
        for (int i = 0; i < 3; i++)
        {
            if (virtualReferencePoints[i] == null)
            {
                Debug.LogError($"[WallCalib] virtualReferencePoints[{i}] is not assigned.");
                return false;
            }
        }
        if (leftControllerTransform == null || rightControllerTransform == null)
            Debug.LogWarning("[WallCalib] One or both controller Transforms not assigned. Falling back to raw tracking position.");
        return true;
    }

    // ── Audio ─────────────────────────────────────────────────────────────────

    void PlayClip(AudioClip clip)
    {
        if (audioSource != null && clip != null)
            audioSource.PlayOneShot(clip);
    }
}