using UnityEngine;
public class EnvironmentManager : MonoBehaviour
{
    [Header("References")]
    public SimpleWallSystem wallSystem;

    [Tooltip("Parent for ground-level scenery. Design children with Y=0 as ground.")]
    public Transform environmentRoot;

    [Header("Wall Height")]
    public float wallHeight = 5f;
    public float minHeight = 2f;
    public float maxHeight = 100f;

    [Header("Physical Floor")]
    [Tooltip("Y of the real floor in tracking space. 0 if using Floor Level origin.")]
    public float physicalFloorY = 0f;
    public bool autoDetectFloor = true;
    
    private GameObject _defaultGround;
    private bool _floorDetected;

    /// <summary>World Y where the ground sits.</summary>
    public float GroundY => physicalFloorY - wallHeight;

    void Start()
    {
        
    }

    void Update()
    {
        if (autoDetectFloor && !_floorDetected
            && wallSystem != null
            && wallSystem.Walls.Count > 0)
        {
            DetectFloorFromWalls();
            _floorDetected = true;
        }

        UpdatePositions();
    }

    public void SetWallHeight(float h)
    {
        wallHeight = Mathf.Clamp(h, minHeight, maxHeight);
        UpdatePositions();
    }

    public void AdjustWallHeight(float delta)
    {
        SetWallHeight(wallHeight + delta);
    }

    void DetectFloorFromWalls()
    {
        float lowestY = float.MaxValue;
        foreach (var wall in wallSystem.Walls)
        {
            // Use wall center and bottom edge (sample points may be empty)
            if (wall.center.y < lowestY) lowestY = wall.center.y;

            float wallBottom = wall.center.y
                - wall.localUp.y * wall.height * 0.5f;
            if (wallBottom < lowestY) lowestY = wallBottom;
        }
        // Samples are at hand height, floor is ~0.8m below
        physicalFloorY = lowestY - 0.8f;
    }

    void UpdatePositions()
    {
        float gy = GroundY;

        if (environmentRoot != null)
        {
            var pos = environmentRoot.position;
            pos.y = gy;
            environmentRoot.position = pos;
        }

        if (_defaultGround != null)
            _defaultGround.transform.position = new Vector3(0f, gy, 0f);
    }
    
}