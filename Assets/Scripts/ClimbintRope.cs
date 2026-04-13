using UnityEngine;

[RequireComponent(typeof(LineRenderer))]
public class ClimbingRope : MonoBehaviour
{
    [Header("References")]
    public Transform playerHead;
    public SimpleWallSystem wallSystem;

    [Header("Rope Settings")]
    public float harnessOffset = 0.5f;
    public float ropeSlack = 0.3f;
    public int ropeResolution = 20;

    [Header("Carabiner")]
    [Tooltip("How far below the player the carabiner is clipped to the wall (meters)")]
    public float carabinerHeightBelow = 2f;
    public GameObject carabinerPrefab;

    private LineRenderer _line;
    private GameObject _carabiner;
    private bool _isVisible = false;
    private Vector3 _fixedCarabinerPos;

    void Start()
    {
        _line = GetComponent<LineRenderer>();
        _line.positionCount = ropeResolution;
        _line.useWorldSpace = true;
        _line.startWidth = 0.012f;
        _line.endWidth   = 0.012f;

        if (carabinerPrefab != null)
        {
            _carabiner = Instantiate(carabinerPrefab);
            _carabiner.SetActive(false);
        }

        _line.enabled = false;
    }

    void Update()
    {
        if (!_isVisible) return;
        if (playerHead == null || wallSystem == null) return;
        if (!wallSystem.IsCalibrated || wallSystem.Walls.Count == 0) return;

        Vector3 harness = playerHead.position
            - Vector3.up * harnessOffset
            + playerHead.forward * 0.1f;

        // Carabiner position is fixed — only the harness end moves
        DrawCatenary(harness, _fixedCarabinerPos);
    }

    Vector3 GetCarabinerPosition(Vector3 harness)
    {
        Vector3 targetPos = harness - Vector3.up * carabinerHeightBelow;

        float bestDist = float.MaxValue;
        int bestWall = 0;
        var walls = wallSystem.Walls;

        for (int i = 0; i < walls.Count; i++)
        {
            float dist = Mathf.Abs(walls[i].SignedDistanceToPoint(targetPos));
            if (dist < bestDist)
            {
                bestDist = dist;
                bestWall = i;
            }
        }

        var wall = walls[bestWall];
        Vector3 onWall = wall.ProjectPointOntoWall(targetPos);
        return onWall + wall.normal * 0.03f;
    }

    void DrawCatenary(Vector3 start, Vector3 end)
    {
        for (int i = 0; i < ropeResolution; i++)
        {
            float t = i / (float)(ropeResolution - 1);
            Vector3 point = Vector3.Lerp(start, end, t);
            float sag = ropeSlack * 4f * t * (1f - t);
            point.y -= sag;
            _line.SetPosition(i, point);
        }
    }

    public void SetVisible(bool visible)
    {
        _isVisible = visible;
        _line.enabled = visible;

        if (visible && wallSystem != null && wallSystem.IsCalibrated)
        {
            // Calculate and lock carabiner position once
            Vector3 harness = playerHead.position
                - Vector3.up * harnessOffset
                + playerHead.forward * 0.1f;

            _fixedCarabinerPos = GetCarabinerPosition(harness);

            if (_carabiner != null)
            {
                _carabiner.transform.position = _fixedCarabinerPos;
                _carabiner.SetActive(true);
            }
        }
        else if (!visible && _carabiner != null)
        {
            _carabiner.SetActive(false);
        }
    }
}