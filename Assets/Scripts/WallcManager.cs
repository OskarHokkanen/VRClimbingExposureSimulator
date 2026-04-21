using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// Calibrates a virtual climbing wall to its physical counterpart using 3 grip presses.
///
/// SETUP:
///   1. Attach this script to any persistent GameObject in the scene.
///   2. Set wallRoot  →  the root Transform of your imported wall mesh.
///   3. Create 3 empty GameObjects as DIRECT children of wallRoot,
///      add WallReferencePoint to each, and position them on 3 easily-
///      identifiable holds/features you can touch in the real world.
///   4. Drag those 3 Transforms into virtualReferencePoints[0..2] (order matters).
///   5. Add an AudioSource to the same GameObject and assign audio clips.
///
/// CALIBRATION FLOW AT RUNTIME:
///   - If no save file exists: grip press 1 starts the session,
///     grips 2 and 3 collect points 2 and 3, after which the wall snaps into place.
///   - If a save file exists: the wall is positioned immediately on Start.
///   - To recalibrate call StartCalibration() (e.g. from a UI button).
///
/// REFERENCE POINT ORDER:
///   Touch physical spots in the same order (0 → 1 → 2) as the virtual markers.
///   Choose 3 points that form a clear triangle – NOT collinear.
///   Spread them wide for better rotational accuracy.
/// </summary>
public class WallCManager : MonoBehaviour
{
    // ── Inspector ────────────────────────────────────────────────────────────

    [Header("Wall")]
    [Tooltip("Root Transform of the 3D wall scan. Scale, rotation and position will be driven by calibration.")]
    public Transform wallRoot;

    [Tooltip("Exactly 3 child Transforms on the wall marking known reference spots (must be DIRECT children of wallRoot).")]
    public Transform[] virtualReferencePoints = new Transform[3];

    [Header("Audio")]
    public AudioSource audioSource;
    [Tooltip("Played each time a point is successfully placed.")]
    public AudioClip pointPlacedClip;
    [Tooltip("Played when all 3 points are placed and the wall is aligned.")]
    public AudioClip calibrationCompleteClip;
    [Tooltip("Played when a calibration session begins.")]
    public AudioClip calibrationStartedClip;

    // ── Private state ─────────────────────────────────────────────────────────

    private InputDevice leftController;
    private InputDevice rightController;
    private bool leftGripPrev;
    private bool rightGripPrev;

    private enum State { Idle, Collecting }
    private State currentState = State.Idle;
    private readonly List<Vector3> physicalPoints = new List<Vector3>();

    private const string FileName = "wall_calibration.json";
    private string SavePath => Path.Combine(Application.persistentDataPath, FileName);

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    void Start()
    {
        if (!ValidateSetup()) return;

        if (TryLoadCalibration())
            Debug.Log("[WallCalib] Saved calibration applied – wall is positioned.");
        else
            Debug.Log("[WallCalib] No saved calibration. Grip to start calibrating (point 1 of 3).");
    }

    void Update()
    {
        RefreshDevices();
        HandleGripInput();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Call this to begin a fresh calibration session (e.g. from a UI button).
    /// Clears any previously collected points and resets state.
    /// </summary>
    public void StartCalibration()
    {
        physicalPoints.Clear();
        currentState = State.Idle;
        Debug.Log("[WallCalib] Ready to recalibrate – press Grip at reference point 0.");
    }

    /// <summary>
    /// Delete the save file and reset the wall to default transform.
    /// </summary>
    public void ClearCalibration()
    {
        if (File.Exists(SavePath)) File.Delete(SavePath);
        wallRoot.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        wallRoot.localScale = Vector3.one;
        physicalPoints.Clear();
        currentState = State.Idle;
        Debug.Log("[WallCalib] Calibration cleared.");
    }

    // ── Input ─────────────────────────────────────────────────────────────────

    void RefreshDevices()
    {
        TryRefreshDevice(ref leftController,  InputDeviceCharacteristics.Left);
        TryRefreshDevice(ref rightController, InputDeviceCharacteristics.Right);
    }

    static void TryRefreshDevice(ref InputDevice device, InputDeviceCharacteristics side)
    {
        if (device.isValid) return;
        var found = new List<InputDevice>();
        InputDevices.GetDevicesWithCharacteristics(
            side | InputDeviceCharacteristics.Controller, found);
        if (found.Count > 0) device = found[0];
    }

    void HandleGripInput()
    {
        Vector3? triggerPos = null;

        // Rising-edge detection on either controller – first one wins per frame
        if (TryGetGripRisingEdge(leftController,  ref leftGripPrev,  out Vector3 lPos))
            triggerPos = lPos;
        else if (TryGetGripRisingEdge(rightController, ref rightGripPrev, out Vector3 rPos))
            triggerPos = rPos;

        if (triggerPos.HasValue)
            OnGripRisingEdge(triggerPos.Value);
    }

    static bool TryGetGripRisingEdge(InputDevice device, ref bool prevState, out Vector3 position)
    {
        position = default;
        if (!device.isValid) { prevState = false; return false; }

        device.TryGetFeatureValue(CommonUsages.gripButton, out bool pressed);
        bool rising = pressed && !prevState;
        prevState = pressed;

        if (!rising) return false;
        device.TryGetFeatureValue(CommonUsages.devicePosition, out position);
        return true;
    }

    // ── Calibration flow ──────────────────────────────────────────────────────

    void OnGripRisingEdge(Vector3 worldPosition)
    {
        // First press in Idle state kicks off the session
        if (currentState == State.Idle)
        {
            currentState = State.Collecting;
            physicalPoints.Clear();
            PlayClip(calibrationStartedClip);
            Debug.Log("[WallCalib] Calibration started – touch reference point 0 on the physical wall.");
        }

        physicalPoints.Add(worldPosition);
        PlayClip(pointPlacedClip);
        Debug.Log($"[WallCalib] Point {physicalPoints.Count - 1} placed at {worldPosition:F3}");

        if (physicalPoints.Count < 3)
        {
            Debug.Log($"[WallCalib] Touch reference point {physicalPoints.Count} on the physical wall.");
            return;
        }

        // All 3 points collected
        if (ComputeAndApply())
        {
            PlayClip(calibrationCompleteClip);
            Debug.Log("[WallCalib] ✓ Calibration complete!");
        }
        else
        {
            // Error logged inside ComputeAndApply – reset so user can retry
            physicalPoints.Clear();
        }

        currentState = State.Idle;
    }

    // ── Transform math ────────────────────────────────────────────────────────

    /// <summary>
    /// Given 3 physical grip positions and 3 virtual reference positions,
    /// computes the wall root's world transform (translation + rotation + uniform scale)
    /// using orthonormal frame alignment (Kabsch-style, 3-point variant).
    ///
    /// Returns false if the points are degenerate (collinear or too close).
    /// </summary>
    bool ComputeAndApply()
    {
        Vector3 p0 = physicalPoints[0], p1 = physicalPoints[1], p2 = physicalPoints[2];

        // Reference points in wall-local space (direct children → localPosition is what we want)
        Vector3 lq0 = virtualReferencePoints[0].localPosition;
        Vector3 lq1 = virtualReferencePoints[1].localPosition;
        Vector3 lq2 = virtualReferencePoints[2].localPosition;

        // ── Scale ──────────────────────────────────────────────────────────────
        float physDist = Vector3.Distance(p0, p1);
        float virtDist = Vector3.Distance(lq0, lq1);

        if (virtDist < 1e-4f)
        {
            Debug.LogError("[WallCalib] Reference points 0 and 1 are too close in local space. Spread them further apart.");
            return false;
        }

        float scale = physDist / virtDist;

        // ── Physical coordinate frame ──────────────────────────────────────────
        Vector3 px = (p1 - p0).normalized;
        Vector3 pz = Vector3.Cross(px, p2 - p0).normalized;

        if (pz.sqrMagnitude < 0.01f)
        {
            Debug.LogError("[WallCalib] Physical points are nearly collinear. Place them in a wider triangle.");
            return false;
        }

        Vector3 py = Vector3.Cross(pz, px);

        // ── Virtual coordinate frame ───────────────────────────────────────────
        Vector3 vx = (lq1 - lq0).normalized;
        Vector3 vz = Vector3.Cross(vx, lq2 - lq0).normalized;

        if (vz.sqrMagnitude < 0.01f)
        {
            Debug.LogError("[WallCalib] Virtual reference points are nearly collinear. Reposition them.");
            return false;
        }

        Vector3 vy = Vector3.Cross(vz, vx);

        // ── Rotation: R maps virtual frame → physical frame ────────────────────
        //   R = M_physical * M_virtual^T
        //   (since M_virtual is orthonormal, its transpose is its inverse)
        Matrix4x4 Mphys = BuildOrthoMatrix(px, py, pz);
        Matrix4x4 Mvirt = BuildOrthoMatrix(vx, vy, vz);
        Matrix4x4 R     = Mphys * Mvirt.transpose;
        Quaternion rot  = R.rotation;

        // ── Translation: ensure lq0 maps exactly to p0 ───────────────────────
        //   worldPos = rot * (localPos * scale) + translation
        //   → translation = p0 - rot * (lq0 * scale)
        Vector3 pos = p0 - rot * (lq0 * scale);

        // ── Apply ──────────────────────────────────────────────────────────────
        wallRoot.localScale = Vector3.one * scale;
        wallRoot.SetPositionAndRotation(pos, rot);

        Debug.Log($"[WallCalib] Scale={scale:F4}  Pos={pos:F3}  Rot={rot.eulerAngles:F1}");

        SaveCalibration(scale, pos, rot);
        return true;
    }

    /// <summary>Builds a 4×4 rotation matrix with x/y/z as column vectors.</summary>
    static Matrix4x4 BuildOrthoMatrix(Vector3 x, Vector3 y, Vector3 z)
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
            Debug.Log($"[WallCalib] Saved to: {SavePath}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[WallCalib] Could not save calibration: {e.Message}");
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

            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[WallCalib] Failed to load calibration file: {e.Message}");
            return false;
        }
    }

    // ── Validation ────────────────────────────────────────────────────────────

    bool ValidateSetup()
    {
        if (wallRoot == null)
        {
            Debug.LogError("[WallCalib] wallRoot is not assigned!");
            return false;
        }

        if (virtualReferencePoints == null || virtualReferencePoints.Length != 3)
        {
            Debug.LogError("[WallCalib] Assign exactly 3 virtualReferencePoints.");
            return false;
        }

        for (int i = 0; i < 3; i++)
        {
            if (virtualReferencePoints[i] == null)
            {
                Debug.LogError($"[WallCalib] virtualReferencePoints[{i}] is null.");
                return false;
            }

            if (virtualReferencePoints[i].parent != wallRoot)
                Debug.LogWarning($"[WallCalib] Reference point {i} is not a direct child of wallRoot – localPosition may be incorrect.");
        }

        return true;
    }

    // ── Scene debug ───────────────────────────────────────────────────────────

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        if (physicalPoints == null || physicalPoints.Count == 0) return;

        Gizmos.color = Color.yellow;
        foreach (var pt in physicalPoints)
            Gizmos.DrawSphere(pt, 0.04f);

        if (physicalPoints.Count >= 2)
            for (int i = 0; i < physicalPoints.Count - 1; i++)
                Gizmos.DrawLine(physicalPoints[i], physicalPoints[i + 1]);
    }
#endif

    void PlayClip(AudioClip clip)
    {
        if (audioSource != null && clip != null)
            audioSource.PlayOneShot(clip);
    }
}