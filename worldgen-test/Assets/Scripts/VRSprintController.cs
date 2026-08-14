using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit.Samples.StarterAssets;

// Bottone del controller che attiva lo sprint. Volutamente un enum invece di un
// InputFeatureUsage<bool> esposto in Inspector: quest'ultimo non ha un editor decente e
// risulterebbe scomodo da configurare a mano.
public enum VRSprintButton
{
    PrimaryButton,   // X (sinistro) / A (destro) sui controller standard
    SecondaryButton, // Y (sinistro) / B (destro)
    GripButton,      // già usato da Grab Move e Teleport Mode Cancel: evitare se quei provider sono attivi
    Trigger
}

/// <summary>
/// Aggiunge lo sprint alla locomotion VR: tenendo premuto un bottone del controller la velocità
/// del DynamicMoveProvider viene moltiplicata, per tornare a quella normale al rilascio.
/// Non tocca l'input action asset condiviso (XRI Default Input Actions) — legge il bottone
/// direttamente dal device XR, così resta autonomo e non rischia di alterare binding già in uso
/// da altri provider di locomotion (Move, Grab Move, Teleport, Jump...).
/// </summary>
public class VRSprintController : MonoBehaviour
{
    [Tooltip("Il DynamicMoveProvider di cui modulare la velocità mentre lo sprint è attivo. Se lasciato vuoto, cerca un DynamicMoveProvider su questo stesso GameObject.")]
    public DynamicMoveProvider moveProvider;

    [Tooltip("Controller la cui pressione attiva lo sprint.")]
    public XRNode sprintHand = XRNode.LeftHand;

    [Tooltip("Bottone da tenere premuto per correre. Di default il Primary Button sinistro (X): il Primary Button destro (A) è già usato dal Jump, così le due mani restano simmetriche (destra = salta, sinistra = corri).")]
    public VRSprintButton sprintButton = VRSprintButton.PrimaryButton;

    [Tooltip("Moltiplicatore applicato a moveSpeed mentre il tasto sprint è tenuto premuto.")]
    public float sprintMultiplier = 1.8f;

    float _baseMoveSpeed = -1f;
    InputDevice _device;

    void Start()
    {
        if (moveProvider == null) moveProvider = GetComponent<DynamicMoveProvider>();
    }

    void Update()
    {
        if (moveProvider == null) return;

        // Cattura la velocità "normale" configurata in Inspector la prima volta: così se in futuro
        // si cambia moveSpeed dall'Inspector del DynamicMoveProvider, questo script continua a
        // usarla come base invece di un valore hardcoded qui.
        if (_baseMoveSpeed < 0f) _baseMoveSpeed = moveProvider.moveSpeed;

        if (!_device.isValid) _device = InputDevices.GetDeviceAtXRNode(sprintHand);

        bool sprinting = _device.isValid
            && _device.TryGetFeatureValue(GetFeature(sprintButton), out bool pressed)
            && pressed;

        moveProvider.moveSpeed = sprinting ? _baseMoveSpeed * sprintMultiplier : _baseMoveSpeed;
    }

    static InputFeatureUsage<bool> GetFeature(VRSprintButton button) => button switch
    {
        VRSprintButton.SecondaryButton => CommonUsages.secondaryButton,
        VRSprintButton.GripButton      => CommonUsages.gripButton,
        VRSprintButton.Trigger         => CommonUsages.triggerButton,
        _                              => CommonUsages.primaryButton,
    };

    void OnDisable()
    {
        // Ripristina la velocità normale se il componente viene disattivato mentre si stava
        // correndo, altrimenti il player resterebbe "incollato" alla velocità sprint per sempre.
        if (moveProvider != null && _baseMoveSpeed >= 0f) moveProvider.moveSpeed = _baseMoveSpeed;
    }
}
