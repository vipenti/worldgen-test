using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Riproduce un suono di passo quando il player si muove a terra, scegliendo il clip in base
/// al tipo di superficie ("grass"/"hard"/"soft"/"fluid"/"snow") sotto ai piedi, letto da
/// WorldManager → Chunk → materials.json (campo footstepType).
/// Da mettere sullo stesso GameObject di PlayerMovement (serve lo stesso CharacterController).
/// </summary>
public class FootstepAudio : MonoBehaviour
{
    [System.Serializable]
    public class SurfaceSound
    {
        [Tooltip("Deve corrispondere a uno dei footstepType usati in materials.json: grass, hard, soft, fluid, snow.")]
        public string footstepType;
        public AudioClip[] clips;
    }

    [Header("Riferimenti")]
    public CharacterController controller;
    public WorldManager worldManager;
    public AudioSource audioSource;

    [Header("Suoni per superficie")]
    [Tooltip("Un elemento per ogni footstepType che vuoi gestire (grass, hard, soft, fluid, snow). Più clip nello stesso elemento vengono scelti a caso per varietà.")]
    public List<SurfaceSound> surfaceSounds = new List<SurfaceSound>();
    [Tooltip("Usato se la superficie sotto ai piedi non ha un footstepType mappato in materials.json.")]
    public AudioClip defaultFootstepClip;

    [Header("Timing")]
    [Tooltip("Distanza percorsa (in metri) tra un passo e il successivo. Più bassa = passi più frequenti quando si corre.")]
    public float stepDistance = 2f;
    [Range(0f, 0.5f)]
    public float pitchVariation = 0.08f;
    [Range(0f, 0.5f)]
    public float volumeVariation = 0.1f;
    public float baseVolume = 0.6f;

    Vector3 lastStepPosition;
    Dictionary<string, SurfaceSound> _byType;

    void Awake()
    {
        if (controller == null) controller = GetComponent<CharacterController>();
        if (worldManager == null) worldManager = FindAnyObjectByType<WorldManager>();
        if (audioSource == null)
        {
            audioSource = GetComponent<AudioSource>();
            if (audioSource == null) audioSource = gameObject.AddComponent<AudioSource>();
        }
        audioSource.playOnAwake = false;
        audioSource.spatialBlend = 0f; // 2D: i passi si sentono uguali indipendentemente dall'orientamento camera

        _byType = new Dictionary<string, SurfaceSound>();
        foreach (var s in surfaceSounds)
        {
            if (!string.IsNullOrEmpty(s.footstepType) && s.clips != null && s.clips.Length > 0)
                _byType[s.footstepType] = s;
        }

        lastStepPosition = transform.position;
    }

    void Update()
    {
        if (LLMWorldDirector.IsUIFocused) return;
        if (controller == null || !controller.isGrounded)
        {
            lastStepPosition = transform.position;
            return;
        }

        // Solo distanza orizzontale: evita che salti/atterraggi (movimento verticale) contino come passi.
        Vector3 flatPos = new Vector3(transform.position.x, 0f, transform.position.z);
        Vector3 flatLast = new Vector3(lastStepPosition.x, 0f, lastStepPosition.z);
        float distSqr = (flatPos - flatLast).sqrMagnitude;

        if (distSqr >= stepDistance * stepDistance)
        {
            lastStepPosition = transform.position;
            PlayFootstep();
        }
    }

    void PlayFootstep()
    {
        string footstepType = worldManager != null ? worldManager.GetFootstepTypeAtWorldPos(transform.position) : null;

        AudioClip clip = null;
        if (footstepType != null && _byType.TryGetValue(footstepType, out var surface))
        {
            clip = surface.clips[Random.Range(0, surface.clips.Length)];
        }
        else if (defaultFootstepClip != null)
        {
            clip = defaultFootstepClip;
        }

        if (clip == null) return;

        audioSource.pitch = 1f + Random.Range(-pitchVariation, pitchVariation);
        float volume = baseVolume * (1f + Random.Range(-volumeVariation, volumeVariation));
        audioSource.PlayOneShot(clip, Mathf.Clamp01(volume));
    }
}
