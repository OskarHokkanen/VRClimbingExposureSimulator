using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Represents a single calibrated physical wall surface.
/// Stores the fitted plane geometry and original sample data.
/// </summary>
[System.Serializable]
public class CalibratedWall
{
    public int wallIndex;

    // ── Plane geometry ──
    /// <summary>Center point of the wall in world space.</summary>
    public Vector3 center;

    /// <summary>Outward-facing normal (points toward the climber).</summary>
    public Vector3 normal;

    /// <summary>Local X axis of the wall (horizontal along the surface).</summary>
    public Vector3 localRight;

    /// <summary>Local Y axis of the wall (vertical along the surface).</summary>
    public Vector3 localUp;

    // ── Dimensions ──
    /// <summary>Wall width in meters (along localRight).</summary>
    public float width;

    /// <summary>Wall height in meters (along localUp).</summary>
    public float height;

    // ── Calibration data ──
    public List<Vector3> samplePoints = new List<Vector3>();
    public List<Vector3> sampleNormals = new List<Vector3>();
    public float calibrationTimestamp;

    // ── Runtime reference (not serialized) ──
    [System.NonSerialized]
    public GameObject visualObject;

    // ── Derived Properties ──

    /// <summary>
    /// Tilt angle in degrees: 0° = vertical wall, positive = overhang (tilted toward climber),
    /// negative = slab (tilted away from climber).
    /// </summary>
    public float TiltAngleDegrees
    {
        get
        {
            // Project normal onto the horizontal plane
            Vector3 horizontalNormal = new Vector3(normal.x, 0, normal.z).normalized;

            // Angle between the actual normal and horizontal
            float angleFromHorizontal = Vector3.SignedAngle(horizontalNormal, normal, 
                Vector3.Cross(horizontalNormal, Vector3.up));

            // Vertical wall → normal is horizontal → angle = 0
            // Overhang → normal tilts upward → positive angle
            // Slab → normal tilts downward → negative angle
            return angleFromHorizontal;
        }
    }

    /// <summary>
    /// Compass heading the wall faces (0°=North, 90°=East, etc.)
    /// </summary>
    public float FacingAngleDegrees
    {
        get
        {
            float angle = Mathf.Atan2(normal.x, normal.z) * Mathf.Rad2Deg;
            if (angle < 0) angle += 360f;
            return angle;
        }
    }

    /// <summary>
    /// The rotation that transforms a default forward-facing quad 
    /// to align with this wall surface.
    /// </summary>
    public Quaternion Rotation => Quaternion.LookRotation(-normal, localUp);

    /// <summary>
    /// Returns the plane equation: ax + by + cz + d = 0
    /// where (a,b,c) = normal and d = -dot(normal, center)
    /// </summary>
    public Vector4 PlaneEquation
    {
        get
        {
            float d = -Vector3.Dot(normal, center);
            return new Vector4(normal.x, normal.y, normal.z, d);
        }
    }

    /// <summary>
    /// Get the signed distance from a world-space point to this wall's plane.
    /// Positive = in front of wall (toward climber), Negative = behind wall.
    /// </summary>
    public float SignedDistanceToPoint(Vector3 point)
    {
        return Vector3.Dot(normal, point - center);
    }

    /// <summary>
    /// Project a world-space point onto this wall's surface plane.
    /// </summary>
    public Vector3 ProjectPointOntoWall(Vector3 point)
    {
        float dist = SignedDistanceToPoint(point);
        return point - normal * dist;
    }

    /// <summary>
    /// Convert a world-space point to 2D coordinates on the wall surface.
    /// Returns (u, v) where u is along localRight and v is along localUp,
    /// with (0,0) at the wall center.
    /// </summary>
    public Vector2 WorldToWallUV(Vector3 worldPoint)
    {
        Vector3 projected = ProjectPointOntoWall(worldPoint);
        Vector3 offset = projected - center;
        float u = Vector3.Dot(offset, localRight);
        float v = Vector3.Dot(offset, localUp);
        return new Vector2(u, v);
    }

    /// <summary>
    /// Convert 2D wall surface coordinates back to world space.
    /// </summary>
    public Vector3 WallUVToWorld(Vector2 uv)
    {
        return center + localRight * uv.x + localUp * uv.y;
    }

    /// <summary>
    /// Check if a world-space point is within the wall's bounds
    /// (projected onto the wall plane).
    /// </summary>
    public bool IsPointWithinBounds(Vector3 worldPoint, float margin = 0f)
    {
        Vector2 uv = WorldToWallUV(worldPoint);
        float halfW = width * 0.5f + margin;
        float halfH = height * 0.5f + margin;
        return Mathf.Abs(uv.x) <= halfW && Mathf.Abs(uv.y) <= halfH;
    }

    /// <summary>
    /// Calculate the mean fitting error: average distance of sample points 
    /// from the fitted plane. Lower = more accurate calibration.
    /// </summary>
    public float MeanFittingError
    {
        get
        {
            if (samplePoints == null || samplePoints.Count == 0) return 0f;
            float total = 0f;
            foreach (var p in samplePoints)
                total += Mathf.Abs(SignedDistanceToPoint(p));
            return total / samplePoints.Count;
        }
    }

    public override string ToString()
    {
        return $"Wall[{wallIndex}] center={center:F3} normal={normal:F3} " +
               $"tilt={TiltAngleDegrees:F1}° size={width:F2}×{height:F2}m " +
               $"error={MeanFittingError * 1000:F1}mm";
    }
}
