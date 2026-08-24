using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// Bottoni del controller leggibili direttamente dal device XR (InputDevice/CommonUsages), senza
/// passare dall'input action asset condiviso — stesso approccio già usato da
/// <see cref="VRSprintController"/>, centralizzato qui perché ora più script ne hanno bisogno
/// (VoiceBiomeInput per il push-to-talk e la conferma/rifiuto della trascrizione, OptionsPanel
/// per il toggle del pannello opzioni) ed evitare di duplicare l'enum e lo switch in ognuno.
/// </summary>
public enum VRControllerButton
{
    PrimaryButton,      // A (destro) / X (sinistro)
    SecondaryButton,    // B (destro) / Y (sinistro)
    GripButton,         // già usato da Grab Move e Teleport Mode Cancel: evitare se attivi
    Trigger,
    Primary2DAxisClick, // click della thumbstick, già usato da VRSprintController (destro)
    Menu,               // bottone "menu/sistema": su alcuni runtime OpenXR (es. Meta Quest) è
                         // riservato al sistema operativo e non arriva mai all'app — verificare
                         // in-headset prima di affidarcisi, con fallback su un altro bottone.
}

public static class VRControllerInput
{
    public static InputFeatureUsage<bool> GetFeature(VRControllerButton button) => button switch
    {
        VRControllerButton.SecondaryButton    => CommonUsages.secondaryButton,
        VRControllerButton.GripButton         => CommonUsages.gripButton,
        VRControllerButton.Trigger            => CommonUsages.triggerButton,
        VRControllerButton.Primary2DAxisClick => CommonUsages.primary2DAxisClick,
        VRControllerButton.Menu               => CommonUsages.menuButton,
        _                                      => CommonUsages.primaryButton,
    };

    /// <summary>
    /// True se il bottone è premuto in questo istante. <paramref name="cachedDevice"/> va tenuto
    /// dal chiamante (un campo per mano/uso) e viene ri-risolto automaticamente se il device
    /// diventa non valido (controller spento/riacceso, riconnessione a runtime...).
    /// </summary>
    public static bool IsPressed(XRNode node, VRControllerButton button, ref InputDevice cachedDevice)
    {
        if (!cachedDevice.isValid) cachedDevice = InputDevices.GetDeviceAtXRNode(node);
        return cachedDevice.isValid
            && cachedDevice.TryGetFeatureValue(GetFeature(button), out bool pressed)
            && pressed;
    }
}
