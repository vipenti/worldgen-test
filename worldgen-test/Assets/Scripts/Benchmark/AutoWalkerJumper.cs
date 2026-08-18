using UnityEngine;

/// <summary>
/// Cammina il player dritto in avanti a velocità costante e bassa, con salti periodici a
/// intervallo casuale — usato dal benchmark (vedi BenchmarkRunner) per mettere sotto carico
/// rendering/streaming dei chunk mentre si campiona l'FPS, senza bisogno di input reale.
/// Bypassa completamente PlayerMovement (va disabilitato da chi attiva questo componente, per
/// evitare che due script chiamino CharacterController.Move nello stesso frame).
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class AutoWalkerJumper : MonoBehaviour
{
    [Header("Camminata")]
    public float walkSpeed = 2.5f;
    public float gravity = -19.62f; // stesso valore di PlayerMovement, per coerenza di "feeling"

    [Header("Salto periodico")]
    public bool jumpingEnabled = true;
    public float jumpHeight = 2.5f;
    public float minSecondsBetweenJumps = 4f;
    public float maxSecondsBetweenJumps = 10f;

    CharacterController _controller;
    Vector3 _velocity;
    float _nextJumpAt;
    bool _walking;

    void Awake() => _controller = GetComponent<CharacterController>();

    public void BeginWalking()
    {
        if (_walking) return;
        _walking = true;
        ScheduleNextJump();
    }

    public void StopWalking() => _walking = false;

    void ScheduleNextJump() =>
        _nextJumpAt = Time.time + Random.Range(minSecondsBetweenJumps, maxSecondsBetweenJumps);

    void Update()
    {
        if (!_walking || _controller == null) return;

        bool grounded = _controller.isGrounded;
        if (grounded && _velocity.y < 0f) _velocity.y = -2f; // resta incollato a terra, come PlayerMovement

        _controller.Move(transform.forward * walkSpeed * Time.deltaTime);

        if (jumpingEnabled && grounded && Time.time >= _nextJumpAt)
        {
            _velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
            ScheduleNextJump();
        }

        _velocity.y += gravity * Time.deltaTime;
        _controller.Move(_velocity * Time.deltaTime);
    }
}
