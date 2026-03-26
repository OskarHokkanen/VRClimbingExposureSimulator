using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using TMPro;

/// <summary>
/// Simplified wall calibration for a two-wall climbing corner with side wings.
///
/// LAYOUT (top-down view, climber facing the corner):
///
///   Left Wing ─── Left Wall ──┐┌── Right Wall ─── Right Wing
///                              ││
///                              ││  ← corner (intersection)
///                              ││
///                            climber
///
/// CALIBRATION:
///   1. Touch controller flat on Wall 1, press trigger (3+ points)
///   2. Press A to finalize Wall 1
///   3. Touch controller flat on Wall 2, press trigger (3+ points)
///   4. Press A to finalize Wall 2
///   5. Walls are automatically positioned at their intersection
///   6. Adjust wings with thumbstick in wing-edit mode
///
/// All walls extend down to the environment ground level.
/// Wings are hinged on the outer edge of each main wall.
/// </summary>
public class SimpleWallSystem : MonoBehaviour
{
    public enum ControllerHand { Right, Left }

    [Header("References")]
    public Transform rightController;
    public Transform leftController;
    public Transform headTransform; 
    public EnvironmentManager environmentManager;

    [Header("Wall Dimensions")]
    [Tooltip("Width of each main wall panel (meters)")]
    public float wallWidth = 3f;

    [Tooltip("How far above the player's head the wall top extends (meters)")]
    public float topAboveHead = 1.0f;

    [Tooltip("How far each wall extends past the corner to prevent gaps (meters)")]
    public float cornerOverlap = 0.005f;

    [Header("Wing Settings")]
    [Tooltip("Enable wing panels on the outer sides")]
    public bool enableWings = true;

    [Tooltip("Width of each wing panel (meters)")]
    public float wingWidth = 1.5f;

    [Tooltip("Angle of left wing relative to left wall (degrees). " +
             "0 = flush/coplanar, 90 = perpendicular inward, -90 = perpendicular outward")]
    public float leftWingAngle = 0f;

    [Tooltip("Angle of right wing relative to right wall (degrees)")]
    public float rightWingAngle = 0f;

    [Header("Appearance")]
    public Material wallMaterial;
    public Color wallColor = new Color(0.35f, 0.35f, 0.4f, 1f);
    public Color wingColor = new Color(0.30f, 0.30f, 0.38f, 1f);
    public Color bottomColor = new Color(0.03f, 0.03f, 0.05f, 1f);

    [Range(0f, 1f)]
    public float fadeStart = 0.3f;

    [Header("Controls")]
    public SimpleWallSystem.ControllerHand activeHand =
        SimpleWallSystem.ControllerHand.Right;

    [Header("UI")]
    public TextMeshProUGUI statusText;
    public HoldPlacementManager holdPlacementManager; 

    // ── State ──
    public enum Phase { CalibratingWall1, CalibratingWall2, Editing, Done }
    public Phase CurrentPhase { get; private set; } = Phase.CalibratingWall1;

    private enum EditTarget { WallWidth, LeftWing, RightWing }
    private EditTarget _editTarget = EditTarget.WallWidth;

    // Calibration data
    private List<Vector3> _samplePoints = new List<Vector3>();
    private List<Vector3> _sampleNormals = new List<Vector3>();
    private List<GameObject> _sampleMarkers = new List<GameObject>();

    // Fitted wall planes
    private Vector3 _wall1Center, _wall1Normal, _wall1Up, _wall1Right;
    private Vector3 _wall2Center, _wall2Normal, _wall2Up, _wall2Right;
    private float _wall1Height, _wall2Height;
    private bool _wall1Valid, _wall2Valid;

    // Intersection
    private Vector3 _cornerPoint;
    private Vector3 _cornerLineDir;

    // Meshes
    private List<GameObject> _meshObjects = new List<GameObject>();

    // Input
    private bool _triggerPrev, _primaryPrev, _secondaryPrev, _gripPrev;

    // Head height captured during calibration
    private float _headYAtCalibration;

    // Public access for other scripts
    public Vector3 Wall1Normal => _wall1Normal;
    public Vector3 Wall2Normal => _wall2Normal;
    public Vector3 CornerPoint => _cornerPoint;
    public bool IsCalibrated => _wall1Valid && _wall2Valid;

    // ────────────────────────────────────────────
    // Lifecycle
    // ────────────────────────────────────────────

    void Update()
    {
        InputDevice dev = GetDevice();
        if (!dev.isValid) return;

        dev.TryGetFeatureValue(CommonUsages.triggerButton, out bool trigger);
        dev.TryGetFeatureValue(CommonUsages.primaryButton, out bool primary);
        dev.TryGetFeatureValue(CommonUsages.secondaryButton, out bool secondary);
        dev.TryGetFeatureValue(CommonUsages.gripButton, out bool grip);
        dev.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 stick);

        switch (CurrentPhase)
        {
            case Phase.CalibratingWall1:
            case Phase.CalibratingWall2:
                // Trigger → sample point
                if (trigger && !_triggerPrev)
                    SamplePoint();

                // A → finalize current wall
                if (primary && !_primaryPrev && _samplePoints.Count >= 3)
                    FinalizeCurrentWall();

                // B → undo last sample
                if (secondary && !_secondaryPrev && _samplePoints.Count > 0)
                    UndoLastSample();
                break;

            case Phase.Editing:
                // B → cycle edit target: wall width → left wing → right wing
                if (secondary && !_secondaryPrev)
                {
                    _editTarget = (EditTarget)(((int)_editTarget + 1) % 3);
                }

                if (_editTarget == EditTarget.WallWidth)
                {
                    // Stick X → adjust wall width
                    if (Mathf.Abs(stick.x) > 0.3f)
                    {
                        wallWidth += stick.x * 3f * Time.deltaTime;
                        wallWidth = Mathf.Clamp(wallWidth, 1f, 15f);
                        RebuildMeshes();
                    }
                }
                else
                {
                    // Stick X → adjust active wing angle
                    if (Mathf.Abs(stick.x) > 0.3f)
                    {
                        float delta = stick.x * 60f * Time.deltaTime;
                        if (_editTarget == EditTarget.LeftWing)
                            leftWingAngle = Mathf.Clamp(leftWingAngle + delta, -135f, 135f);
                        else
                            rightWingAngle = Mathf.Clamp(rightWingAngle + delta, -135f, 135f);
                        RebuildMeshes();
                    }

                    // Stick Y → adjust wing width
                    if (Mathf.Abs(stick.y) > 0.3f)
                    {
                        wingWidth += stick.y * 2f * Time.deltaTime;
                        wingWidth = Mathf.Clamp(wingWidth, 0.2f, 5f);
                        RebuildMeshes();
                    }
                }

                // A → done editing, move to hold placement
                if (primary && !_primaryPrev)
                {
                    CurrentPhase = Phase.Done;
                }
                break;

            case Phase.Done:
                // B → go back to editing walls/wings
                if (secondary && !_secondaryPrev)
                    CurrentPhase = Phase.Editing;
                break;
        }

        _triggerPrev = trigger;
        _primaryPrev = primary;
        _secondaryPrev = secondary;
        _gripPrev = grip;

        UpdateStatusText();
    }

    // ────────────────────────────────────────────
    // Sampling
    // ────────────────────────────────────────────

    void SamplePoint()
    {
        Transform ctrl = GetController();
        if (ctrl == null) return;

        Vector3 pos = ctrl.position;
        Vector3 normal = -ctrl.forward;

        _samplePoints.Add(pos);
        _sampleNormals.Add(normal);

        // Visual marker
        var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        marker.transform.position = pos;
        marker.transform.localScale = Vector3.one * 0.02f;
        marker.GetComponent<Renderer>().material.color = Color.cyan;
        Destroy(marker.GetComponent<Collider>());
        _sampleMarkers.Add(marker);

        // Haptic buzz — short pulse
        SendHaptic(0.3f, 0.15f);
    }

    void UndoLastSample()
    {
        _samplePoints.RemoveAt(_samplePoints.Count - 1);
        _sampleNormals.RemoveAt(_sampleNormals.Count - 1);
        Destroy(_sampleMarkers[_sampleMarkers.Count - 1]);
        _sampleMarkers.RemoveAt(_sampleMarkers.Count - 1);
    }

    void ClearSamples()
    {
        _samplePoints.Clear();
        _sampleNormals.Clear();
        foreach (var m in _sampleMarkers) if (m != null) Destroy(m);
        _sampleMarkers.Clear();
    }

    // ────────────────────────────────────────────
    // Wall Finalization
    // ────────────────────────────────────────────

    void FinalizeCurrentWall()
    {
        // Fit plane
        PlaneFit.FitPlane(_samplePoints, out Vector3 centroid, out Vector3 normal);

        // Orient normal outward (toward climber)
        Vector3 avgN = Vector3.zero;
        foreach (var n in _sampleNormals) avgN += n;
        if (Vector3.Dot(normal, avgN) < 0) normal = -normal;

        // Compute wall frame
        Vector3 up, right;
        ComputeWallFrame(normal, out right, out up);

        // Compute height from sample spread along up axis
        float minV = float.MaxValue, maxV = float.MinValue;
        foreach (var p in _samplePoints)
        {
            float v = Vector3.Dot(p - centroid, up);
            minV = Mathf.Min(minV, v);
            maxV = Mathf.Max(maxV, v);
        }
        float height = Mathf.Max(maxV - minV, 1f);

        if (CurrentPhase == Phase.CalibratingWall1)
        {
            _wall1Center = centroid;
            _wall1Normal = normal;
            _wall1Up = up;
            _wall1Right = right;
            _wall1Height = height;
            _wall1Valid = true;

            // Capture head height for wall top calculation
            if (headTransform != null)
                _headYAtCalibration = headTransform.position.y;

            ClearSamples();
            CurrentPhase = Phase.CalibratingWall2;
            SendHaptic(0.6f, 0.3f); // stronger buzz for finalize
            Debug.Log($"Wall 1 calibrated: normal={normal}, center={centroid}");
        }
        else if (CurrentPhase == Phase.CalibratingWall2)
        {
            _wall2Center = centroid;
            _wall2Normal = normal;
            _wall2Up = up;
            _wall2Right = right;
            _wall2Height = height;
            _wall2Valid = true;

            ClearSamples();

            // Compute intersection and build everything
            ComputeCorner();
            RebuildMeshes();
            CurrentPhase = Phase.Done; // go straight to hold placement
            SendHaptic(0.8f, 0.5f); // strong buzz — walls are ready
            Debug.Log($"Wall 2 calibrated: normal={normal}, center={centroid}");
        }
    }

    void ComputeWallFrame(Vector3 normal, out Vector3 right, out Vector3 up)
    {
        Vector3 refUp = (Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > 0.95f)
            ? Vector3.forward : Vector3.up;
        right = Vector3.Cross(refUp, normal).normalized;
        up = Vector3.Cross(normal, right).normalized;
    }

    // ────────────────────────────────────────────
    // Corner Computation
    // ────────────────────────────────────────────

    void ComputeCorner()
    {
        // Find intersection line of the two wall planes
        _cornerLineDir = Vector3.Cross(_wall1Normal, _wall2Normal);
        float mag = _cornerLineDir.magnitude;

        if (mag < 0.001f)
        {
            // Walls are parallel — place corner between them
            _cornerPoint = (_wall1Center + _wall2Center) * 0.5f;
            _cornerLineDir = _wall1Up;
            return;
        }

        _cornerLineDir /= mag;

        // Find a point on the intersection line
        float d1 = Vector3.Dot(_wall1Normal, _wall1Center);
        float d2 = Vector3.Dot(_wall2Normal, _wall2Center);
        float n1n2 = Vector3.Dot(_wall1Normal, _wall2Normal);
        float denom = 1f - n1n2 * n1n2;

        if (Mathf.Abs(denom) < 1e-6f)
        {
            _cornerPoint = (_wall1Center + _wall2Center) * 0.5f;
            return;
        }

        float alpha = (d1 - d2 * n1n2) / denom;
        float beta = (d2 - d1 * n1n2) / denom;
        _cornerPoint = alpha * _wall1Normal + beta * _wall2Normal;
    }

    // ────────────────────────────────────────────
    // Mesh Building
    // ────────────────────────────────────────────

    public void RebuildMeshes()
    {
        ClearMeshes();
        if (!_wall1Valid || !_wall2Valid) return;

        float groundY = environmentManager != null ? environmentManager.GroundY : -20f;

        // Determine which wall is "left" and which is "right"
        // from the climber's perspective (facing the corner)
        Vector3 cornerToW1 = (_wall1Center - _cornerPoint);
        Vector3 cornerToW2 = (_wall2Center - _cornerPoint);

        // Build main walls — both start at the corner edge
        BuildWallMesh("Wall1", _wall1Normal, _wall1Up, _wall1Right,
            _cornerPoint, _wall1Height, wallWidth, wallColor, groundY, _wall1Center);
        BuildWallMesh("Wall2", _wall2Normal, _wall2Up, _wall2Right,
            _cornerPoint, _wall2Height, wallWidth, wallColor, groundY, _wall2Center);

        // Build wings
        if (enableWings)
        {
            // Left wing: hinged on the outer edge of wall 1
            Vector3 wall1OuterEdge = GetOuterEdge(
                _cornerPoint, _wall1Right, _wall1Center);

            Vector3 leftWingNormal = RotateNormalAroundEdge(
                _wall1Normal, _wall1Up, _wall1Right, wall1OuterEdge,
                _cornerPoint, leftWingAngle);

            ComputeWallFrame(leftWingNormal, out Vector3 lwRight, out Vector3 lwUp);

            Vector3 leftWingStart = _cornerPoint + wall1OuterEdge * wallWidth;
            BuildWingMesh("LeftWing", leftWingNormal, lwUp, lwRight,
                leftWingStart, _wall1Height, wingWidth, wingColor,
                groundY, wall1OuterEdge);

            // Right wing: hinged on the outer edge of wall 2
            Vector3 wall2OuterEdge = GetOuterEdge(
                _cornerPoint, _wall2Right, _wall2Center);

            Vector3 rightWingNormal = RotateNormalAroundEdge(
                _wall2Normal, _wall2Up, _wall2Right, wall2OuterEdge,
                _cornerPoint, rightWingAngle);

            ComputeWallFrame(rightWingNormal, out Vector3 rwRight, out Vector3 rwUp);

            Vector3 rightWingStart = _cornerPoint + wall2OuterEdge * wallWidth;
            BuildWingMesh("RightWing", rightWingNormal, rwUp, rwRight,
                rightWingStart, _wall2Height, wingWidth, wingColor,
                groundY, wall2OuterEdge);
        }

        // Rebuild the CalibratedWall list so HoldPlacementManager can use it
        RebuildCalibratedWalls();
    }

    /// <summary>
    /// Determine which direction along wallRight the wall extends from the corner.
    /// Picks the direction that goes toward the wall's own center (where
    /// the user actually sampled), so the wall covers the real physical surface.
    /// This works for both inside and outside corners.
    /// </summary>
    Vector3 GetOuterEdge(Vector3 corner, Vector3 wallRight, Vector3 wallCenter)
    {
        Vector3 cornerToCenter = wallCenter - corner;
        float dot = Vector3.Dot(cornerToCenter, wallRight);
        return dot >= 0 ? wallRight : -wallRight;
    }

    /// <summary>
    /// Rotate a wall's normal around the vertical edge (hinge) by the given angle.
    /// This creates the wing's orientation.
    /// </summary>
    Vector3 RotateNormalAroundEdge(Vector3 wallNormal, Vector3 wallUp,
        Vector3 wallRight, Vector3 outerDir, Vector3 corner, float angleDeg)
    {
        // The hinge axis is the wall's up direction (vertical edge)
        // Rotating the normal around this axis pivots the wing
        Quaternion rotation = Quaternion.AngleAxis(angleDeg, wallUp);
        return (rotation * wallNormal).normalized;
    }

    // ────────────────────────────────────────────
    // Mesh Construction
    // ────────────────────────────────────────────

    void BuildWallMesh(string name, Vector3 normal, Vector3 up, Vector3 right,
        Vector3 cornerPoint, float height, float width, Color color,
        float groundY, Vector3 wallCenter)
    {
        // Extend from corner toward the wall's own center
        Vector3 outerDir = GetOuterEdge(cornerPoint, right, wallCenter);

        // Top: above the player's head
        float topY = _headYAtCalibration + topAboveHead;
        float topT = Mathf.Abs(up.y) > 0.01f
            ? (topY - cornerPoint.y) / up.y
            : height * 0.5f + topAboveHead;

        // Bottom: down to ground
        float halfH = height * 0.5f;
        float bottomT = Mathf.Abs(up.y) > 0.01f
            ? (groundY - cornerPoint.y) / up.y
            : -(halfH + 50f);
        if (bottomT > topT - 1f) bottomT = topT - 5f; // ensure some height

        // Inner edge extends past the corner by overlap amount to fill gaps
        Vector3 innerEdge = cornerPoint - outerDir;
        // Vector3 innerEdge = cornerPoint - outerDir * cornerOverlap;

        Vector3 BL = innerEdge + up * bottomT;
        Vector3 BR = cornerPoint + outerDir * width + up * bottomT;
        Vector3 TR = cornerPoint + outerDir * width + up * topT;
        Vector3 TL = innerEdge + up * topT;

        var verts = new List<Vector3> { BL, BR, TR, TL };
        CreateMeshObject(name, verts, normal, up, color, groundY, cornerPoint.y, halfH);
    }

    void BuildWingMesh(string name, Vector3 normal, Vector3 up, Vector3 right,
        Vector3 hingePoint, float height, float width, Color color,
        float groundY, Vector3 outerDir)
    {
        // Top: above the player's head (same as main walls)
        float topY = _headYAtCalibration + topAboveHead;
        float topT = Mathf.Abs(up.y) > 0.01f
            ? (topY - hingePoint.y) / up.y
            : height * 0.5f + topAboveHead;

        float halfH = height * 0.5f;
        float bottomT = Mathf.Abs(up.y) > 0.01f
            ? (groundY - hingePoint.y) / up.y
            : -(halfH + 50f);
        if (bottomT > topT - 1f) bottomT = topT - 5f;
        

        // Wing extends from hinge point outward
        // The "outward" direction for the wing is its own right axis,
        // oriented to continue away from the corner
        Vector3 wingOuterDir;
        float dotRight = Vector3.Dot(outerDir, right);
        wingOuterDir = dotRight >= 0 ? right : -right;

        Vector3 BL = hingePoint + up * bottomT;
        Vector3 BR = hingePoint + wingOuterDir * width + up * bottomT;
        Vector3 TR = hingePoint + wingOuterDir * width + up * topT;
        Vector3 TL = hingePoint + up * topT;

        var verts = new List<Vector3> { BL, BR, TR, TL };
        CreateMeshObject(name, verts, normal, up, color, groundY, hingePoint.y, halfH);
    }

    void CreateMeshObject(string name, List<Vector3> verts, Vector3 normal,
        Vector3 up, Color surfaceColor, float groundY, float centerY, float halfH)
    {
        var obj = new GameObject(name);
        var mesh = new Mesh { name = name };

        int n = verts.Count;
        var vertices = new Vector3[n];
        var normals = new Vector3[n];
        var uvs = new Vector2[n];
        var colors = new Color[n];

        float calibratedBottomY = centerY - halfH;
        float topY = centerY + halfH + topAboveHead;
        float totalHeight = topY - groundY;

        for (int i = 0; i < n; i++)
        {
            vertices[i] = verts[i];
            normals[i] = normal;
            uvs[i] = new Vector2(i < 2 ? 0 : 1,
                Mathf.Clamp01((verts[i].y - groundY) / Mathf.Max(totalHeight, 0.1f)));

            float belowCalibrated = calibratedBottomY - verts[i].y;
            float extensionHeight = calibratedBottomY - groundY;
            float t = (extensionHeight <= 0.01f || belowCalibrated <= 0f)
                ? 0f : Mathf.Clamp01(belowCalibrated / extensionHeight);
            float fadeT = Mathf.Clamp01(Mathf.InverseLerp(fadeStart, 1f, t));
            fadeT *= fadeT;
            colors[i] = Color.Lerp(surfaceColor, bottomColor, fadeT);
        }

        mesh.vertices = vertices;
        mesh.normals = normals;
        mesh.uv = uvs;
        mesh.colors = colors;

        // Double-sided triangles: both windings so the wall is visible from either side
        mesh.triangles = new int[]
        {
            0, 1, 2,  0, 2, 3,   // front face
            0, 2, 1,  0, 3, 2    // back face
        };
        mesh.RecalculateBounds();
        mesh.RecalculateNormals(); // let Unity figure out normals for both sides

        obj.AddComponent<MeshFilter>().mesh = mesh;
        var mr = obj.AddComponent<MeshRenderer>();
        mr.material = wallMaterial != null
            ? new Material(wallMaterial) : CreateDefaultMaterial();

        _meshObjects.Add(obj);
    }

    void ClearMeshes()
    {
        foreach (var obj in _meshObjects) if (obj != null) Destroy(obj);
        _meshObjects.Clear();
    }

    Material CreateDefaultMaterial()
    {
        // Try multiple shaders in order of preference
        // On Quest, some shaders get stripped from the build
        string[] shaderNames = new[]
        {
            "Universal Render Pipeline/Unlit",
            "Universal Render Pipeline/Particles/Unlit",
            "Particles/Standard Unlit",
            "Unlit/Color",
            "Standard"
        };

        Shader shader = null;
        foreach (var name in shaderNames)
        {
            shader = Shader.Find(name);
            if (shader != null) break;
        }

        if (shader == null)
        {
            Debug.LogError("SimpleWallSystem: No shader found! Assign a wallMaterial in the inspector.");
            shader = Shader.Find("Hidden/InternalErrorShader");
        }

        var mat = new Material(shader);
        mat.color = wallColor;

        // Disable backface culling so both sides render
        if (mat.HasProperty("_Cull"))
            mat.SetFloat("_Cull", 0); // 0 = Off

        // Disable soft particles
        if (mat.HasProperty("_SoftParticlesEnabled"))
            mat.SetFloat("_SoftParticlesEnabled", 0);

        return mat;
    }

    // ────────────────────────────────────────────
    // CalibratedWall Exposure (for HoldPlacementManager)
    // ────────────────────────────────────────────

    private List<CalibratedWall> _calibratedWalls = new List<CalibratedWall>();

    /// <summary>
    /// All wall panels as CalibratedWall objects (main walls + wings).
    /// Used by HoldPlacementManager for hold placement.
    /// Rebuilt whenever meshes are rebuilt.
    /// </summary>
    public IReadOnlyList<CalibratedWall> Walls => _calibratedWalls;

    void RebuildCalibratedWalls()
    {
        _calibratedWalls.Clear();
        if (!_wall1Valid || !_wall2Valid) return;

        Vector3 w1Outer = GetOuterEdge(
            _cornerPoint, _wall1Right, _wall1Center);
        Vector3 w2Outer = GetOuterEdge(
            _cornerPoint, _wall2Right, _wall2Center);

        // Wall 1
        Vector3 w1Center = _cornerPoint + w1Outer * (wallWidth * 0.5f);
        _calibratedWalls.Add(BuildCalibratedWall(0, w1Center,
            _wall1Normal, _wall1Right, _wall1Up, wallWidth, _wall1Height));

        // Wall 2
        Vector3 w2Center = _cornerPoint + w2Outer * (wallWidth * 0.5f);
        _calibratedWalls.Add(BuildCalibratedWall(1, w2Center,
            _wall2Normal, _wall2Right, _wall2Up, wallWidth, _wall2Height));

        // Wings
        if (enableWings)
        {
            // Left wing
            Vector3 leftWingNormal = RotateNormalAroundEdge(
                _wall1Normal, _wall1Up, _wall1Right, w1Outer,
                _cornerPoint, leftWingAngle);
            ComputeWallFrame(leftWingNormal, out Vector3 lwRight, out Vector3 lwUp);
            Vector3 lwHinge = _cornerPoint + w1Outer * wallWidth;
            float dotLw = Vector3.Dot(w1Outer, lwRight);
            Vector3 lwOutDir = dotLw >= 0 ? lwRight : -lwRight;
            Vector3 lwCenter = lwHinge + lwOutDir * (wingWidth * 0.5f);
            _calibratedWalls.Add(BuildCalibratedWall(2, lwCenter,
                leftWingNormal, lwRight, lwUp, wingWidth, _wall1Height));

            // Right wing
            Vector3 rightWingNormal = RotateNormalAroundEdge(
                _wall2Normal, _wall2Up, _wall2Right, w2Outer,
                _cornerPoint, rightWingAngle);
            ComputeWallFrame(rightWingNormal, out Vector3 rwRight, out Vector3 rwUp);
            Vector3 rwHinge = _cornerPoint + w2Outer * wallWidth;
            float dotRw = Vector3.Dot(w2Outer, rwRight);
            Vector3 rwOutDir = dotRw >= 0 ? rwRight : -rwRight;
            Vector3 rwCenter = rwHinge + rwOutDir * (wingWidth * 0.5f);
            _calibratedWalls.Add(BuildCalibratedWall(3, rwCenter,
                rightWingNormal, rwRight, rwUp, wingWidth, _wall2Height));
        }
    }

    CalibratedWall BuildCalibratedWall(int index, Vector3 center,
        Vector3 normal, Vector3 right, Vector3 up, float width, float height)
    {
        return new CalibratedWall
        {
            wallIndex = index,
            center = center,
            normal = normal,
            localRight = right,
            localUp = up,
            width = width,
            height = height,
            samplePoints = new List<Vector3>(),
            sampleNormals = new List<Vector3>()
        };
    }

    // ────────────────────────────────────────────
    // Public API
    // ────────────────────────────────────────────

    /// <summary>Reset everything and start over.</summary>
    public void ResetCalibration()
    {
        ClearMeshes();
        ClearSamples();
        _calibratedWalls.Clear();
        _wall1Valid = false;
        _wall2Valid = false;
        CurrentPhase = Phase.CalibratingWall1;

        // Clear holds if referenced
        if (holdPlacementManager != null)
            holdPlacementManager.ClearAll();
    }

    /// <summary>
    /// Set wing angles from code and rebuild.
    /// </summary>
    public void SetWingAngles(float leftAngle, float rightAngle)
    {
        leftWingAngle = Mathf.Clamp(leftAngle, -135f, 135f);
        rightWingAngle = Mathf.Clamp(rightAngle, -135f, 135f);
        RebuildMeshes();
    }

    /// <summary>
    /// Set wing width from code and rebuild.
    /// </summary>
    public void SetWingWidth(float width)
    {
        wingWidth = Mathf.Clamp(width, 0.2f, 5f);
        RebuildMeshes();
    }

    // ────────────────────────────────────────────
    // Status
    // ────────────────────────────────────────────

    int _getHoldCount() =>
        holdPlacementManager != null ? holdPlacementManager.HoldCount : 0;

    void UpdateStatusText()
    {
        if (statusText == null) return;

        switch (CurrentPhase)
        {
            case Phase.CalibratingWall1:
                statusText.text = $"WALL 1  |  Points: {_samplePoints.Count}/3+\n" +
                    "[Trigger] Sample  [A] Finalize  [B] Undo";
                break;
            case Phase.CalibratingWall2:
                statusText.text = $"WALL 2  |  Points: {_samplePoints.Count}/3+\n" +
                    "[Trigger] Sample  [A] Finalize  [B] Undo";
                break;
            case Phase.Editing:
                string targetName = _editTarget switch
                {
                    EditTarget.WallWidth => $"WALL WIDTH ({wallWidth:F1}m)",
                    EditTarget.LeftWing => $"LEFT WING ({leftWingAngle:F0}°)",
                    EditTarget.RightWing => $"RIGHT WING ({rightWingAngle:F0}°)",
                    _ => ""
                };
                string wingInfo = _editTarget != EditTarget.WallWidth
                    ? $"  Wing width: {wingWidth:F1}m" : "";
                statusText.text = $"EDITING: {targetName}{wingInfo}\n" +
                    $"Walls: L={leftWingAngle:F0}° | W={wallWidth:F1}m | R={rightWingAngle:F0}°\n" +
                    "[Stick ←→] Adjust  [Stick ↕] Wing width  [B] Next  [A] Done";
                break;
            case Phase.Done:
                statusText.text = $"PLACE HOLDS  |  Holds: {_getHoldCount()}\n" +
                    "[Trigger] Place  [A] Type  [B] Edit walls";
                break;
        }
    }

    // ────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────

    Transform GetController() =>
        activeHand == SimpleWallSystem.ControllerHand.Right
            ? rightController : leftController;

    InputDevice GetDevice()
    {
        var flags = (activeHand == SimpleWallSystem.ControllerHand.Right)
            ? InputDeviceCharacteristics.Right | InputDeviceCharacteristics.Controller
            : InputDeviceCharacteristics.Left | InputDeviceCharacteristics.Controller;
        var devs = new List<InputDevice>();
        InputDevices.GetDevicesWithCharacteristics(flags, devs);
        return devs.Count > 0 ? devs[0] : default;
    }

    /// <summary>
    /// Send a haptic pulse to the active controller.
    /// amplitude: 0-1 (intensity), duration: seconds
    /// </summary>
    void SendHaptic(float amplitude, float duration)
    {
        InputDevice dev = GetDevice();
        if (dev.isValid)
        {
            dev.SendHapticImpulse(0, amplitude, duration);
        }
    }
}