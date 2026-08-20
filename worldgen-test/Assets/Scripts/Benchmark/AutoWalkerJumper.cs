using System.Collections;
using UnityEngine;

/// <summary>
/// Cammina il player sempre verso nord (asse world fisso, indipendente dalla rotazione del
/// player) a velocità costante — usato dal benchmark (vedi BenchmarkRunner) per mettere sotto
/// carico rendering/streaming dei chunk mentre si campiona l'FPS, senza bisogno di input reale.
/// Bypassa completamente PlayerMovement (va disabilitato da chi attiva questo componente, per
/// evitare che due script chiamino CharacterController.Move nello stesso frame).
///
/// Solo per la scena di benchmark: mentre cammina disattiva periodicamente i Collider di tutti
/// gli oggetti piazzati in scena (alberi, edifici, asset generati — qualunque cosa NON sia un
/// Chunk di terreno), così il walker li attraversa senza restare bloccato. I MeshCollider del
/// terreno (sul GameObject Chunk stesso) restano intatti: servono per stare in piedi e per
/// rilevare i gradini del terreno.
///
/// Rilevamento "gradino": non c'è più un salto periodico a intervallo casuale. Invece, ogni frame
/// si controlla quanto la posizione è avanzata lungo l'asse nord rispetto al frame precedente. Se
/// per stuckFrameThreshold frame consecutivi (a terra) l'avanzamento resta sotto stuckPositionEpsilon,
/// vuol dire che il player è bloccato contro un gradino del terreno: si tenta un salto basso
/// (stepJumpHeight, molto più basso del vecchio jumpHeight) per superarlo.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class AutoWalkerJumper : MonoBehaviour
{
    [Header("Camminata")]
    [Tooltip("Direzione di marcia fissa in world space (\"nord\"). Il player cammina sempre in questa direzione, indipendentemente da come è ruotato.")]
    public Vector3 walkDirection = Vector3.forward;
    [Tooltip("Velocità di camminata. Minimo 8 — vedi OnValidate.")]
    public float walkSpeed = 8f;
    public float gravity = -19.62f; // stesso valore di PlayerMovement, per coerenza di "feeling"

    [Header("Rilevamento gradino (sostituisce il vecchio salto periodico)")]
    [Tooltip("Quanti frame consecutivi di avanzamento nullo verso nord (a terra) prima di considerare il player bloccato su un gradino e far scattare il salto.")]
    public int stuckFrameThreshold = 15;
    [Tooltip("Avanzamento minimo (metri/frame) lungo l'asse nord sotto il quale il player è considerato fermo.")]
    public float stuckPositionEpsilon = 0.02f;
    [Tooltip("Altezza del salto usato per superare un gradino — volutamente basso, non è il salto \"esplorativo\" di prima.")]
    public float stepJumpHeight = 0.6f;

    [Header("Disattivazione collider oggetti (solo scena benchmark)")]
    [Tooltip("Ogni quanti secondi si ripete la scansione per disattivare i Collider degli oggetti piazzati dopo l'inizio della camminata (nuovi asset generati durante il bioma corrente).")]
    public float colliderScanInterval = 1f;

    CharacterController _controller;
    Vector3 _velocity;
    bool _walking;

    float _lastNorthPos;
    int _stuckFrames;
    Vector3 _northAxis;

    Coroutine _colliderScanRoutine;

    void Awake() => _controller = GetComponent<CharacterController>();

    void OnValidate()
    {
        if (walkSpeed < 8f) walkSpeed = 8f;
    }

    public void BeginWalking()
    {
        if (_walking) return;
        _walking = true;

        _northAxis = walkDirection.normalized;
        _lastNorthPos = Vector3.Dot(transform.position, _northAxis);
        _stuckFrames = 0;

        DisableSceneObjectColliders(); // prima passata subito, non aspettare il primo intervallo
        _colliderScanRoutine = StartCoroutine(DisableObjectCollidersLoop());
    }

    public void StopWalking()
    {
        _walking = false;
        if (_colliderScanRoutine != null)
        {
            StopCoroutine(_colliderScanRoutine);
            _colliderScanRoutine = null;
        }
    }

    void Update()
    {
        if (!_walking || _controller == null) return;

        bool grounded = _controller.isGrounded;
        if (grounded && _velocity.y < 0f) _velocity.y = -2f; // resta incollato a terra, come PlayerMovement

        _controller.Move(_northAxis * walkSpeed * Time.deltaTime);

        // Rilevamento gradino: quanto siamo avanzati verso nord da questo frame all'ultimo controllo.
        float northPos = Vector3.Dot(transform.position, _northAxis);
        float northDelta = northPos - _lastNorthPos;
        _lastNorthPos = northPos;

        if (grounded)
        {
            if (northDelta < stuckPositionEpsilon)
            {
                _stuckFrames++;
                if (_stuckFrames >= stuckFrameThreshold)
                {
                    _velocity.y = Mathf.Sqrt(stepJumpHeight * -2f * gravity);
                    _stuckFrames = 0;
                }
            }
            else
            {
                _stuckFrames = 0;
            }
        }

        _velocity.y += gravity * Time.deltaTime;
        _controller.Move(_velocity * Time.deltaTime);
    }

    IEnumerator DisableObjectCollidersLoop()
    {
        var wait = new WaitForSeconds(colliderScanInterval);
        while (_walking)
        {
            yield return wait;
            DisableSceneObjectColliders();
        }
    }

    // Disattiva tutti i Collider in scena TRANNE quelli sul GameObject Chunk (pavimento/terreno)
    // e il CharacterController del player stesso. Rieseguita a intervalli perché nuovi oggetti
    // (asset del bioma) continuano a essere piazzati mentre il walker cammina.
    void DisableSceneObjectColliders()
    {
        var colliders = FindObjectsByType<Collider>(FindObjectsSortMode.None);
        foreach (var col in colliders)
        {
            if (!col.enabled) continue;
            if (col == _controller) continue;
            if (col.GetComponent<Chunk>() != null) continue; // pavimento/terreno: da non toccare
            col.enabled = false;
        }
    }
}
