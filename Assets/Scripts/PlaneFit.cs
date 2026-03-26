using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Least-squares plane fitting from a set of 3D points.
/// Uses covariance-matrix eigen-decomposition (Jacobi iteration)
/// to find the best-fit plane normal.
///
/// This gives significantly better results than a simple 3-point
/// cross-product, especially when samples have measurement noise
/// (which they always will with hand-held controller input).
/// </summary>
public static class PlaneFit
{
    /// <summary>
    /// Fit a plane to a set of 3D points using least-squares.
    /// </summary>
    /// <param name="points">At least 3 non-collinear points.</param>
    /// <param name="centroid">Output: centroid (average) of the points.</param>
    /// <param name="normal">Output: unit normal of the best-fit plane.</param>
    /// <returns>True if successful, false if degenerate (collinear or insufficient points).</returns>
    public static bool FitPlane(IList<Vector3> points, out Vector3 centroid, out Vector3 normal)
    {
        centroid = Vector3.zero;
        normal = Vector3.up;

        if (points.Count < 3)
        {
            Debug.LogWarning("PlaneFit: Need at least 3 points.");
            return false;
        }

        // 1. Compute centroid
        centroid = ComputeCentroid(points);

        // 2. Build 3×3 covariance matrix
        //    C = Σ (p_i - centroid) ⊗ (p_i - centroid)
        float xx = 0, xy = 0, xz = 0;
        float yy = 0, yz = 0;
        float zz = 0;

        for (int i = 0; i < points.Count; i++)
        {
            float dx = points[i].x - centroid.x;
            float dy = points[i].y - centroid.y;
            float dz = points[i].z - centroid.z;

            xx += dx * dx;
            xy += dx * dy;
            xz += dx * dz;
            yy += dy * dy;
            yz += dy * dz;
            zz += dz * dz;
        }

        // 3. Find the eigenvector corresponding to the smallest eigenvalue.
        //    That eigenvector is the plane normal.
        //    We use a direct analytical method for 3×3 symmetric matrices.
        normal = SmallestEigenvector3x3(xx, xy, xz, yy, yz, zz);

        return true;
    }

    /// <summary>
    /// Compute the fitting error (RMS distance of points from the plane).
    /// </summary>
    public static float ComputeRMSError(IList<Vector3> points, Vector3 centroid, Vector3 normal)
    {
        if (points.Count == 0) return 0f;

        float sumSq = 0f;
        for (int i = 0; i < points.Count; i++)
        {
            float dist = Vector3.Dot(points[i] - centroid, normal);
            sumSq += dist * dist;
        }

        return Mathf.Sqrt(sumSq / points.Count);
    }

    /// <summary>
    /// Quick 3-point plane (for preview when only 3 points available).
    /// </summary>
    public static bool FitPlane3Points(Vector3 a, Vector3 b, Vector3 c,
        out Vector3 centroid, out Vector3 normal)
    {
        centroid = (a + b + c) / 3f;
        normal = Vector3.Cross(b - a, c - a).normalized;

        if (normal.sqrMagnitude < 0.001f)
        {
            normal = Vector3.up;
            return false; // collinear
        }
        return true;
    }

    // ────────────────────────────────────────────────────────────
    // Internal: 3×3 symmetric eigenvalue solver
    // ────────────────────────────────────────────────────────────

    static Vector3 ComputeCentroid(IList<Vector3> points)
    {
        Vector3 sum = Vector3.zero;
        for (int i = 0; i < points.Count; i++)
            sum += points[i];
        return sum / points.Count;
    }

    /// <summary>
    /// Find the eigenvector of a 3×3 symmetric matrix corresponding
    /// to its smallest eigenvalue, using Jacobi iteration.
    /// 
    /// Matrix:  | xx  xy  xz |
    ///          | xy  yy  yz |
    ///          | xz  yz  zz |
    /// </summary>
    static Vector3 SmallestEigenvector3x3(
        float xx, float xy, float xz,
        float yy, float yz, float zz)
    {
        // Jacobi eigenvalue algorithm for 3×3 symmetric matrix
        // We maintain the matrix A and rotation matrix V (eigenvectors as columns)

        float[,] a = { { xx, xy, xz }, { xy, yy, yz }, { xz, yz, zz } };
        float[,] v = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } }; // identity

        const int maxIterations = 50;
        const float epsilon = 1e-10f;

        for (int iter = 0; iter < maxIterations; iter++)
        {
            // Find the largest off-diagonal element
            int p = 0, q = 1;
            float maxVal = Mathf.Abs(a[0, 1]);

            if (Mathf.Abs(a[0, 2]) > maxVal) { maxVal = Mathf.Abs(a[0, 2]); p = 0; q = 2; }
            if (Mathf.Abs(a[1, 2]) > maxVal) { maxVal = Mathf.Abs(a[1, 2]); p = 1; q = 2; }

            if (maxVal < epsilon) break; // converged

            // Compute Jacobi rotation
            float app = a[p, p];
            float aqq = a[q, q];
            float apq = a[p, q];

            float theta;
            if (Mathf.Abs(app - aqq) < epsilon)
                theta = Mathf.PI / 4f;
            else
                theta = 0.5f * Mathf.Atan2(2f * apq, app - aqq);

            float c = Mathf.Cos(theta);
            float s = Mathf.Sin(theta);

            // Apply rotation to A: A' = J^T A J
            float[,] newA = (float[,])a.Clone();

            newA[p, p] = c * c * app + 2f * s * c * apq + s * s * aqq;
            newA[q, q] = s * s * app - 2f * s * c * apq + c * c * aqq;
            newA[p, q] = 0f;
            newA[q, p] = 0f;

            for (int i = 0; i < 3; i++)
            {
                if (i == p || i == q) continue;
                float aip = a[i, p];
                float aiq = a[i, q];
                newA[i, p] = c * aip + s * aiq;
                newA[p, i] = newA[i, p];
                newA[i, q] = -s * aip + c * aiq;
                newA[q, i] = newA[i, q];
            }

            a = newA;

            // Update eigenvector matrix V
            for (int i = 0; i < 3; i++)
            {
                float vip = v[i, p];
                float viq = v[i, q];
                v[i, p] = c * vip + s * viq;
                v[i, q] = -s * vip + c * viq;
            }
        }

        // Eigenvalues are now on the diagonal of a
        // Find the smallest one
        int minIdx = 0;
        float minEig = a[0, 0];
        if (a[1, 1] < minEig) { minEig = a[1, 1]; minIdx = 1; }
        if (a[2, 2] < minEig) { minEig = a[2, 2]; minIdx = 2; }

        Vector3 result = new Vector3(v[0, minIdx], v[1, minIdx], v[2, minIdx]);
        return result.normalized;
    }
}
