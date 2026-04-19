using UnityEngine;

/// <summary>
/// Plays climbing hall ambience and fades volume based on the player's
/// height above the ground — quieter the higher they climb.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class ClimbingHallAmbience : MonoBehaviour
{
    [Header("References")]
    public Transform playerHead;
    public EnvironmentManager environmentManager;

    [Header("Audio")]
    public AudioClip ambienceClip;
    [Range(0f, 1f)]
    public float maxVolume = 1f;
    [Range(0f, 1f)]
    public float minVolume = 0f;

    [Header("Height Fade")]
    [Tooltip("Player height (above ground) at which volume starts fading")]
    public float fadeStartHeight = 1.5f;
    [Tooltip("Player height (above ground) at which volume reaches minVolume")]
    public float fadeEndHeight   = 8f;

    private AudioSource _source;

    // ────────────────────────────────────────────────────────────────────
    // Lifecycle
    // ────────────────────────────────────────────────────────────────────

    void Awake()
    {
        _source = GetComponent<AudioSource>();
        _source.clip        = ambienceClip;
        _source.loop        = true;
        _source.spatialBlend = 0f;   // 2D — ambience should be non-positional
        _source.playOnAwake = false;
        _source.volume      = maxVolume;
    }

    void Start()
    {
        if (ambienceClip != null)
            _source.Play();
    }

    void Update()
    {
        if (playerHead == null) return;

        float groundY  = environmentManager != null ? environmentManager.GroundY : 0f;
        float heightAboveGround = playerHead.position.y - groundY;

        // Remap height to a 0-1 fade factor
        float t = Mathf.InverseLerp(fadeStartHeight, fadeEndHeight, heightAboveGround);

        _source.volume = Mathf.Lerp(maxVolume, minVolume, t);
    }

    // ────────────────────────────────────────────────────────────────────
    // Public API
    // ────────────────────────────────────────────────────────────────────

    public void SetMuted(bool muted)
    {
        _source.mute = muted;
    }

    public void SetMaxVolume(float vol)
    {
        maxVolume = Mathf.Clamp01(vol);
    }
}