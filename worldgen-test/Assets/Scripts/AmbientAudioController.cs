using UnityEngine;

/// <summary>
/// Suono ambientale generale, unico per tutta la sessione (non per bioma): parte in loop
/// all'avvio e resta attivo. Da mettere su un GameObject persistente in scena (es. WorldManager
/// stesso, o un GameObject "AudioManager" dedicato).
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class AmbientAudioController : MonoBehaviour
{
    public AudioClip generalAmbientClip;
    [Range(0f, 1f)]
    public float volume = 0.4f;

    AudioSource _source;

    void Awake()
    {
        _source = GetComponent<AudioSource>();
        _source.playOnAwake = false;
        _source.loop = true;
        _source.spatialBlend = 0f; // 2D, sempre udibile indipendentemente dalla posizione del player
        _source.volume = volume;
    }

    void Start()
    {
        if (generalAmbientClip == null) return;
        _source.clip = generalAmbientClip;
        _source.Play();
    }

    void OnValidate()
    {
        if (_source != null) _source.volume = volume;
    }
}
