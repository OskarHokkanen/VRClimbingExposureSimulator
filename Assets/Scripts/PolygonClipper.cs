using System.Collections.Generic;
using UnityEngine;

/*
 * -----------------------------
 * -----------------------------
 * NOT IN USED ANYMORE, OLD TEST
 * -----------------------------
 * -----------------------------
 */
public static class PolygonClipper
{
    /// <summary>
    /// Clip a polygon by a plane.
    /// keepPositiveSide=false → keep side opposite to normal.
    /// keepPositiveSide=true  → keep side the normal points to.
    /// </summary>
    public static List<Vector3> ClipByPlane(
        IList<Vector3> polygon, Vector3 planePoint, Vector3 planeNormal,
        bool keepPositiveSide = false)
    {
        if (polygon == null || polygon.Count < 3)
            return new List<Vector3>();

        float Sign(Vector3 p) => Vector3.Dot(p - planePoint, planeNormal);
        bool Inside(float d) => keepPositiveSide ? d >= -0.0001f : d <= 0.0001f;

        var output = new List<Vector3>();
        int count = polygon.Count;

        for (int i = 0; i < count; i++)
        {
            Vector3 cur = polygon[i];
            Vector3 nxt = polygon[(i + 1) % count];
            float dCur = Sign(cur), dNxt = Sign(nxt);

            if (Inside(dCur))
            {
                output.Add(cur);
                if (!Inside(dNxt))
                    output.Add(Intersect(cur, nxt, planePoint, planeNormal));
            }
            else if (Inside(dNxt))
            {
                output.Add(Intersect(cur, nxt, planePoint, planeNormal));
            }
        }
        return output;
    }

    /// <summary>
    /// Compute the intersection line of two planes.
    /// Returns false if planes are parallel.
    /// </summary>
    public static bool PlanePlaneIntersection(
        Vector3 pointA, Vector3 normalA,
        Vector3 pointB, Vector3 normalB,
        out Vector3 linePoint, out Vector3 lineDir)
    {
        lineDir = Vector3.Cross(normalA, normalB);
        float mag = lineDir.magnitude;

        if (mag < 0.001f)
        {
            // Parallel
            linePoint = (pointA + pointB) * 0.5f;
            lineDir = Vector3.zero;
            return false;
        }

        lineDir /= mag;

        // Find a point on the intersection line by solving:
        //   dot(normalA, p) = dot(normalA, pointA)  = dA
        //   dot(normalB, p) = dot(normalB, pointB)  = dB
        // with p = alpha * normalA + beta * normalB
        float dA = Vector3.Dot(normalA, pointA);
        float dB = Vector3.Dot(normalB, pointB);
        float n1n2 = Vector3.Dot(normalA, normalB);
        float denom = 1f - n1n2 * n1n2;

        if (Mathf.Abs(denom) < 1e-6f)
        {
            linePoint = (pointA + pointB) * 0.5f;
            return false;
        }

        float alpha = (dA - dB * n1n2) / denom;
        float beta  = (dB - dA * n1n2) / denom;
        linePoint = alpha * normalA + beta * normalB;

        return true;
    }

    /// <summary>
    /// Build a clipping plane at the intersection line of two wall planes,
    /// oriented so that 'keepCenter' is on the kept side.
    ///
    /// The clipping plane:
    ///   - Contains the intersection line
    ///   - Has a normal perpendicular to the line direction
    ///   - Normal points TOWARD keepCenter
    ///
    /// This ensures the wall whose center is 'keepCenter' keeps
    /// its own side of the intersection, regardless of whether
    /// the corner is convex or concave.
    /// </summary>
    public static bool BuildIntersectionClipPlane(
        Vector3 planePointA, Vector3 normalA,
        Vector3 planePointB, Vector3 normalB,
        Vector3 keepCenter,
        out Vector3 clipPlanePoint, out Vector3 clipPlaneNormal)
    {
        clipPlanePoint = Vector3.zero;
        clipPlaneNormal = Vector3.up;

        if (!PlanePlaneIntersection(planePointA, normalA, planePointB, normalB,
                out Vector3 linePoint, out Vector3 lineDir))
            return false;

        // Project (keepCenter - linePoint) onto the plane perpendicular to lineDir.
        // This gives us the direction from the intersection line toward the wall center.
        Vector3 toCenter = keepCenter - linePoint;
        Vector3 projected = toCenter - lineDir * Vector3.Dot(toCenter, lineDir);

        if (projected.sqrMagnitude < 0.0001f)
        {
            // Center is right on the intersection line — can't determine side
            return false;
        }

        clipPlanePoint = linePoint;
        clipPlaneNormal = projected.normalized;
        return true;
    }

    // ────────────────────────────────────────────

    static Vector3 Intersect(Vector3 a, Vector3 b, Vector3 pp, Vector3 pn)
    {
        float denom = Vector3.Dot(pn, b - a);
        if (Mathf.Abs(denom) < 1e-8f) return a;
        float t = Mathf.Clamp01(Vector3.Dot(pn, pp - a) / denom);
        return a + (b - a) * t;
    }

    /// <summary>Fan-triangulate a convex polygon.</summary>
    public static int[] Triangulate(int vertCount)
    {
        if (vertCount < 3) return new int[0];
        int[] tris = new int[(vertCount - 2) * 3];
        for (int i = 0; i < vertCount - 2; i++)
        {
            tris[i * 3]     = 0;
            tris[i * 3 + 1] = i + 1;
            tris[i * 3 + 2] = i + 2;
        }
        return tris;
    }
}