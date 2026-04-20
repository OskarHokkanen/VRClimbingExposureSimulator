using UnityEngine;

public class AnimationPingPong : MonoBehaviour
{
    public Animator animator;
    public string stateName = "climbingstate"; // name of your animation state
    public float switchTime = 10f;

    private float timer = 0f;
    private float direction = 1f;

    void Update()
    {
        timer += Time.deltaTime;

        // Switch direction every X seconds
        if (timer >= switchTime)
        {
            direction *= -1f;
            timer = 0f;
        }

        // Apply speed (1 = forward, -1 = backward)
        animator.speed = direction;
    }
}