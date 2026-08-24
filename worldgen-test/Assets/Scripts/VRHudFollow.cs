using UnityEngine;

/// <summary>
/// Fa sì che questo Canvas (pensato per essere World Space, con Event Camera = camera XR) resti
/// sempre leggibile davanti al giocatore in VR, riposizionandosi/riorientandosi verso la testa
/// (<see cref="target"/>) con smoothing e una piccola "deadzone" angolare — così non insegue ogni
/// micro-movimento della testa (fastidioso e nauseante in VR) ma si riallinea appena lo sguardo
/// si sposta oltre la soglia. Pensato per ospitare l'intero HUD (pulsante voce, pannello di
/// conferma trascrizione, pannello opzioni...): tutti i figli del Canvas seguono insieme.
/// </summary>
public class VRHudFollow : MonoBehaviour
{
    [Tooltip("Testa del giocatore (Main Camera dentro l'XR Origin). Se lasciato vuoto, usa Camera.main al primo OnEnable.")]
    public Transform target;

    [Header("Posizionamento")]
    public float distance = 1.1f;
    [Tooltip("Offset verticale rispetto all'altezza degli occhi (negativo = più in basso, comodo per non coprire l'orizzonte).")]
    public float verticalOffset = -0.15f;

    [Header("Comfort")]
    [Tooltip("Gradi di scostamento tra lo sguardo e il centro del pannello oltre cui il pannello ricomincia a seguire. 0 = segue sempre, anche i micro-movimenti (sconsigliato, jitter continuo).")]
    public float deadzoneAngle = 15f;
    [Tooltip("Velocità di riposizionamento/riorientamento una volta superata la deadzone.")]
    public float followSpeed = 6f;
    [Tooltip("Se true, il pannello resta sempre verticale (ignora il beccheggio/rollio della testa) — più leggibile.")]
    public bool keepUpright = true;

    Vector3 _desiredPos;
    Quaternion _desiredRot;
    bool _initialized;

    void OnEnable()
    {
        ResolveTarget();
        if (target != null) SnapToTarget();
    }

    void ResolveTarget()
    {
        if (target == null && Camera.main != null) target = Camera.main.transform;
    }

    void SnapToTarget()
    {
        ComputeDesired(out _desiredPos, out _desiredRot);
        transform.SetPositionAndRotation(_desiredPos, _desiredRot);
        _initialized = true;
    }

    void LateUpdate()
    {
        if (target == null)
        {
            ResolveTarget();
            if (target == null) return;
        }
        if (!_initialized) { SnapToTarget(); return; }

        ComputeDesired(out Vector3 candidatePos, out Quaternion candidateRot);

        // Deadzone: confronta dove "guarda" il pannello attuale rispetto a dove lo vorremmo ora.
        // Sotto soglia non tocchiamo nulla, altrimenti il pannello inseguirebbe ogni oscillazione
        // della testa invece di restare fermo finché non serve davvero riposizionarlo.
        Vector3 toCurrent = (transform.position - target.position).normalized;
        Vector3 toDesired = (candidatePos - target.position).normalized;
        float angle = Vector3.Angle(toCurrent, toDesired);

        if (angle > deadzoneAngle)
        {
            _desiredPos = candidatePos;
            _desiredRot = candidateRot;
        }

        float t = 1f - Mathf.Exp(-followSpeed * Time.deltaTime);
        transform.position = Vector3.Lerp(transform.position, _desiredPos, t);
        transform.rotation = Quaternion.Slerp(transform.rotation, _desiredRot, t);
    }

    void ComputeDesired(out Vector3 pos, out Quaternion rot)
    {
        Vector3 fwd = target.forward;
        if (keepUpright) fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.forward;
        fwd.Normalize();

        pos = target.position + fwd * distance + Vector3.up * verticalOffset;
        rot = keepUpright
            ? Quaternion.LookRotation(fwd, Vector3.up)
            : Quaternion.LookRotation((pos - target.position).normalized, target.up);
    }
}
