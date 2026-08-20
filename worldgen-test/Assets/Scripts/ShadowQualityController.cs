using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Applica il livello "Shadow Quality" (Off/Low/Medium/High) alla luce direzionale della scena e
/// al UniversalRenderPipelineAsset attivo. Da aggiungere una volta per main scene
/// (MainScene/MainScene_VR, es. sullo stesso GameObject di WorldManager): trova da solo sia la
/// luce (RenderSettings.sun, o il primo Light direzionale in scena) sia l'URP asset attivo, senza
/// bisogno di wiring manuale in Inspector — stesso pattern di auto-discovery già usato da
/// LLMWorldDirector/AutoPromptSequencer per i loro riferimenti.
///
/// Due modi per cambiare la qualità:
/// 1) Da QUESTO Inspector — cambia il campo "Shadow Quality" qui sotto: si applica subito, anche
///    fuori Play Mode (vedi OnValidate), utile per confrontare a occhio in Scene View.
/// 2) A runtime — dal dropdown nel pannello Opzioni (OptionsPanel.shadowQualityDropdown) o
///    modificando GameSettingsData.shadowQuality in settings.json. Qualunque cambio da lì
///    sovrascrive il campo qui sotto (GameSettings resta l'unica sorgente di verità persistita).
///
/// I 4 livelli rispecchiano (e vanno tenuti allineati a) i valori di default in
/// Assets/Settings/PC_RPAsset.asset — "High" qui riproduce lo stesso tuning fatto lì dopo il calo
/// di framerate osservato quando le ombre sono state estese a tutti gli oggetti del mondo: vedi
/// WorldManager.ApplyShadowCastingMode per il secondo pezzo del fix (disattiva il casting ombra sui
/// prop "tiny"/"small", troppo numerosi e visivamente trascurabili per giustificarne il costo).
/// </summary>
public class ShadowQualityController : MonoBehaviour
{
    public enum Level { Off = 0, Low = 1, Medium = 2, High = 3 }

    [Tooltip("Cambiala qui per un test immediato (anche fuori Play Mode) — applicata subito. A runtime, un cambio dal pannello Opzioni o da settings.json sovrascrive questo valore, che resta comunque lo specchio dell'ultimo livello applicato.")]
    [SerializeField] Level shadowQuality = Level.High;

    Light _sun;

    void OnEnable()
    {
        if (GameSettings.Instance != null)
        {
            shadowQuality = (Level)GameSettings.Instance.Data.shadowQuality;
            GameSettings.Instance.OnShadowQualityChanged += HandleGameSettingsChanged;
        }
        Apply();
    }

    void OnDisable()
    {
        if (GameSettings.Instance != null)
            GameSettings.Instance.OnShadowQualityChanged -= HandleGameSettingsChanged;
    }

    void HandleGameSettingsChanged()
    {
        shadowQuality = (Level)GameSettings.Instance.Data.shadowQuality;
        Apply();
    }

#if UNITY_EDITOR
    // Scatta ogni volta che il campo "Shadow Quality" cambia in Inspector — anche fuori Play Mode,
    // per vedere l'effetto delle ombre direttamente in Scene View senza dover premere Play. In Play
    // Mode, propaga anche a GameSettings così il valore viene salvato e resta la sorgente di verità
    // per il pannello Opzioni (stesso identico effetto di muovere il dropdown lì).
    void OnValidate()
    {
        Apply();
        if (Application.isPlaying && GameSettings.Instance != null)
            GameSettings.Instance.ApplyShadowQuality((int)shadowQuality);
    }
#endif

    void Apply()
    {
        if (_sun == null) FindSun();
        if (_sun == null) return;

        var pipelineAsset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;

        switch (shadowQuality)
        {
            case Level.Off: // Nessun draw call di shadow-map, il costo scompare del tutto.
                _sun.shadows = LightShadows.None;
                break;

            case Level.Low: // Ombre hard (niente PCSS), 1 sola cascata, distanza corta.
                _sun.shadows = LightShadows.Hard;
                SetPipelineShadows(pipelineAsset, distance: 60f, cascades: 1, resolution: 1024);
                break;

            case Level.Medium:
                _sun.shadows = LightShadows.Soft;
                SetPipelineShadows(pipelineAsset, distance: 90f, cascades: 2, resolution: 1024);
                break;

            default: // High — stessi valori del tuning di default in PC_RPAsset.asset.
                _sun.shadows = LightShadows.Soft;
                SetPipelineShadows(pipelineAsset, distance: 110f, cascades: 2, resolution: 1024);
                break;
        }

#if UNITY_EDITOR
        // Fuori Play Mode le modifiche a un ScriptableObject (l'URP asset) non vengono marcate
        // "dirty" automaticamente solo per aver assegnato le sue proprietà da script — senza questo
        // Unity potrebbe non salvarle su disco, e riaprendo il progetto PC_RPAsset.asset apparirebbe
        // ancora al valore precedente nonostante l'anteprima in Scene View mostrasse quello nuovo.
        if (!Application.isPlaying && pipelineAsset != null)
            EditorUtility.SetDirty(pipelineAsset);
#endif
    }

    static void SetPipelineShadows(UniversalRenderPipelineAsset pipelineAsset, float distance, int cascades, int resolution)
    {
        if (pipelineAsset == null) return;
        pipelineAsset.shadowDistance = distance;
        pipelineAsset.shadowCascadeCount = cascades;
        pipelineAsset.mainLightShadowmapResolution = resolution;
    }

    void FindSun()
    {
        _sun = RenderSettings.sun;
        if (_sun != null) return;

        foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
        {
            if (l.type == LightType.Directional) { _sun = l; break; }
        }

        if (_sun == null)
            Debug.LogWarning("[ShadowQualityController] No directional light found in scene — cannot apply shadow quality.");
    }
}
