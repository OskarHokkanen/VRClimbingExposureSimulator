using UnityEngine;

/// <summary>
/// Attach this to each of the 3 reference point GameObjects that are
/// children of the wall root. They appear as coloured spheres in the
/// Scene view so you can easily position them on known spots of the mesh.
/// </summary>
[ExecuteAlways]
public class WallReferencePoint : MonoBehaviour
{
    [Tooltip("Index of this reference point (0, 1, or 2). Must match its slot in WallCalibrationManager.")]
    public int index = 0;

    private static readonly Color[] IndexColors =
    {
        new Color(0.2f, 0.8f, 1f),   // 0 – cyan
        new Color(1f,   0.6f, 0.1f), // 1 – orange
        new Color(0.4f, 1f,   0.4f)  // 2 – green
    };

    private Color GizmoColor => index >= 0 && index < IndexColors.Length
        ? IndexColors[index]
        : Color.white;

    void OnDrawGizmos()
    {
        Gizmos.color = GizmoColor;
        Gizmos.DrawSphere(transform.position, 0.04f);

        // Short upward spike so it's visible inside the mesh
        Gizmos.DrawLine(transform.position, transform.position + transform.up * 0.12f);
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.white;
        Gizmos.DrawWireSphere(transform.position, 0.06f);

#if UNITY_EDITOR
        UnityEditor.Handles.color = GizmoColor;
        UnityEditor.Handles.Label(
            transform.position + Vector3.up * 0.15f,
            $"  Ref Point {index}",
            new GUIStyle { normal = { textColor = GizmoColor }, fontStyle = FontStyle.Bold }
        );
#endif
    }
}