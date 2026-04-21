using System;
using UnityEngine;

/// <summary>
/// JSON-serializable calibration result saved to disk.
/// </summary>
[Serializable]
public class CalibrationData
{
    public float scale = 1f;
    public SerializableVector3 position;
    public SerializableQuaternion rotation;
}

[Serializable]
public struct SerializableVector3
{
    public float x, y, z;

    public SerializableVector3(Vector3 v) { x = v.x; y = v.y; z = v.z; }
    public Vector3 ToVector3() => new Vector3(x, y, z);
}

[Serializable]
public struct SerializableQuaternion
{
    public float x, y, z, w;

    public SerializableQuaternion(Quaternion q) { x = q.x; y = q.y; z = q.z; w = q.w; }
    public Quaternion ToQuaternion() => new Quaternion(x, y, z, w);
}