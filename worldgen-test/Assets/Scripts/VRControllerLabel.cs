using TMPro;
using UnityEngine;

/// <summary>
/// Piccola etichetta 3D (TextMeshPro world-space, non un Canvas) agganciata al modello del
/// controller vicino al bottone fisico a cui si riferisce. Invisibile di default: appare con un
/// fade quando il giocatore guarda verso quella mano (entro <see cref="gazeAngleThreshold"/> gradi
/// dallo sguardo), così non c'è testo perennemente a schermo. Billboard sul solo asse Y verso la
/// camera, per restare leggibile senza ruotare in modo strano quando ci si china.
/// </summary>
[RequireComponent(typeof(TextMeshPro))]
public class VRControllerLabel : MonoBehaviour
{
    [Tooltip("Testa del giocatore. Se vuoto, usa Camera.main al primo OnEnable.")]
    public Transform head;

    [Tooltip("Se assegnato, l'etichetta è candidata a comparire solo mentre questo GameObject è attivo (es. 'Conferma'/'Rifiuta' solo mentre il pannello di conferma è aperto). Lascia vuoto per un'etichetta sempre disponibile (es. 'Parla'/'Opzioni').")]
    public GameObject onlyVisibleWhile;

    [Header("Comfort")]
    [Tooltip("Entro quanti gradi dalla direzione giocatore->mano l'etichetta è totalmente visibile.")]
    public float gazeAngleThreshold = 30f;
    [Tooltip("Oltre gazeAngleThreshold, altri N gradi di transizione morbida fino a zero opacità.")]
    public float fadeMargin = 15f;
    public float fadeSpeed = 8f;

    TMP_Text _text;
    float _alpha;

    void Awake()
    {
        _text = GetComponent<TMP_Text>();
    }

    void OnEnable()
    {
        if (head == null && Camera.main != null) head = Camera.main.transform;
        // Parte invisibile: senza questo, per una frazione di secondo dopo l'attivazione la label
        // avrebbe l'alpha di editor-default (1) prima che LateUpdate la corregga.
        _alpha = 0f;
        ApplyAlpha(0f);
    }

    void LateUpdate()
    {
        if (head == null)
        {
            if (Camera.main != null) head = Camera.main.transform;
            else return;
        }

        bool eligible = onlyVisibleWhile == null || onlyVisibleWhile.activeInHierarchy;

        float targetAlpha = 0f;
        if (eligible)
        {
            Vector3 toHand = transform.position - head.position;
            float angle = Vector3.Angle(head.forward, toHand);
            if (angle <= gazeAngleThreshold) targetAlpha = 1f;
            else if (angle <= gazeAngleThreshold + fadeMargin) targetAlpha = 1f - (angle - gazeAngleThreshold) / fadeMargin;
        }

        _alpha = Mathf.MoveTowards(_alpha, targetAlpha, fadeSpeed * Time.deltaTime);
        ApplyAlpha(_alpha);

        if (_alpha > 0.01f)
        {
            Vector3 fwd = transform.position - head.position;
            fwd.y = 0f;
            if (fwd.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.LookRotation(fwd.normalized, Vector3.up);
        }
    }

    void ApplyAlpha(float a)
    {
        if (_text == null) return;
        var c = _text.color;
        c.a = a;
        _text.color = c;
    }
}
