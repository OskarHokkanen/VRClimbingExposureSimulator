using UnityEngine;

public class RopeMiddleManager : MonoBehaviour
{
    public Transform playerTransform;

    public Transform groundTransform;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        var v = new Vector3(playerTransform.position.x, groundTransform.position.y*-3, playerTransform.position.z);
        transform.position = v;
    }
}
