using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit.Samples.StarterAssets;

// Bottone del controller che attiva/disattiva lo sprint. Volutamente un enum invece di un
// InputFeatureUsage<bool> esposto in Inspector: quest'ultimo non ha un editor decente e
// risulterebbe scomodo da configurare a mano.
public enum VRSprintButton
{
    PrimaryButton,      // X (sinistro) / A (destro) sui controller standard
    SecondaryButton,    // Y (sinistro) / B (destro)
    GripButton,         // già usato da Grab Move e Teleport Mode Cancel: evitare se quei provider sono attivi
    Trigger,
    Primary2DAxisClick, // pressione dell'analogico (click della thumbstick)
}

/// <summary>
/// Aggiunge lo sprint alla locomotion VR: premendo il tasto configurato si attiva lo sprint
/// (moveSpeed moltiplicato), premendolo di nuovo si torna alla velocità normale — un toggle, non
/// una pressione da tenere premuta. Non tocca l'input action asset condiviso (XRI Default Input
/// Actions) — legge il bottone direttamente dal device XR, così resta autonomo e non rischia di
/// alterare binding già in uso da altri provider di locomotion (Move, Grab Move, Teleport, Jump...).
/// </summary>
public class VRSprintController : MonoBehaviour
{
    [Tooltip("Il DynamicMoveProvider di cui modulare la velocità mentre lo sprint è attivo. Se lasciato vuoto, cerca un DynamicMoveProvider su questo stesso GameObject.")]
    public DynamicMoveProvider moveProvider;

    [Tooltip("Controller la cui pressione attiva/disattiva lo sprint.")]
    public XRNode sprintHand = XRNode.RightHand;

    [Tooltip("Bottone da premere per attivare/disattivare lo sprint (toggle, non hold). Default: click dell'analogico destro.")]
    public VRSprintButton sprintButton = VRSprintButton.Primary2DAxisClick;

    [Tooltip("Moltiplicatore applicato a moveSpeed mentre lo sprint è attivo.")]
    public float sprintMultiplier = 1.8f;

    public bool IsSprinting { get; private set; }

    float _baseMoveSpeed = -1f;
    InputDevice _device;
    bool _wasPressed;

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

        bool isPressed = _device.isValid
            && _device.TryGetFeatureValue(GetFeature(sprintButton), out bool pressed)
            && pressed;

        // Toggle sul fronte di salita (non premuto -> premuto), non mentre resta tenuto premuto:
        // altrimenti cliccare l'analogico per sprintare lo terrebbe anche schiacciato lateralmente,
        // muovendo il player, e comunque non è quello che è stato chiesto ("ripremi per smettere").
        if (isPressed && !_wasPressed) IsSprinting = !IsSprinting;
        _wasPressed = isPressed;

        moveProvider.moveSpeed = IsSprinting ? _baseMoveSpeed * sprintMultiplier : _baseMoveSpeed;
    }

    static InputFeatureUsage<bool> GetFeature(VRSprintButton button) => button switch
    {
        VRSprintButton.SecondaryButton    => CommonUsages.secondaryButton,
        VRSprintButton.GripButton         => CommonUsages.gripButton,
        VRSprintButton.Trigger            => CommonUsages.triggerButton,
        VRSprintButton.Primary2DAxisClick => CommonUsages.primary2DAxisClick,
        _                                 => CommonUsages.primaryButton,
    };

    void OnDisable()
    {
        // Ripristina la velocità normale se il componente viene disattivato mentre si stava
        // correndo, altrimenti il player resterebbe "incollato" alla velocità sprint per sempre.
        IsSprinting = false;
        if (moveProvider != null && _baseMoveSpeed >= 0f) moveProvider.moveSpeed = _baseMoveSpeed;
    }
}
