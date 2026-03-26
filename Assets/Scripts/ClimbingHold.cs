using UnityEngine;

/// <summary>
/// A single climbing hold on a calibrated wall.
/// Position stored in wall-relative UV so holds stay correct
/// if wall geometry is adjusted.
/// </summary>
[System.Serializable]
public class ClimbingHold
{
    public int wallIndex;

    /// <summary>
    /// Position on the wall in meters relative to wall center.
    /// x = along localRight, y = along localUp.
    /// </summary>
    public Vector2 wallUV;

    /// <summary>How far the hold protrudes from the wall surface (meters).</summary>
    public float protrusion = 0.05f;

    public HoldType holdType = HoldType.Jug;
    public HoldSize holdSize = HoldSize.Medium;
    public Color color = Color.red;

    [System.NonSerialized] public GameObject visualObject;

    public enum HoldType { Jug, Crimp, Sloper, Pinch, Pocket, Foothold }
    public enum HoldSize { Small, Medium, Large }

    public float ApproxRadius => holdSize switch
    {
        HoldSize.Small => 0.12f,
        HoldSize.Medium => 0.29f,
        HoldSize.Large => 0.50f,
        _ => 0.043f
    };

    public Vector3 GetWorldPosition(CalibratedWall wall)
    {
        return wall.WallUVToWorld(wallUV) + wall.normal * protrusion;
    }

    public Quaternion GetWorldRotation(CalibratedWall wall)
    {
        return Quaternion.LookRotation(wall.normal, wall.localUp);
    }
}