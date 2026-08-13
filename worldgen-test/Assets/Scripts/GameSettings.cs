using System;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// Dati serializzabili delle impostazioni utente. Salvati/caricati come JSON da
/// <see cref="GameSettings"/> — separati dalla classe MonoBehaviour per poterli
/// serializzare/deserializzare direttamente con Newtonsoft senza passare per Unity.
/// </summary>
[Serializable]
public class GameSettingsData
{
    // ── Server ──────────────────────────────────────────────────────────
    // Un solo host, tre porte: sullo stesso server girano di norma il modello LLM (chat),
    // il generatore immagini e il generatore 3D, ognuno su una porta diversa.
    public string serverHost = "25.18.55.60";
    public int llmPort = 1234;
    public int imagePort = 5000;
    public int gen3dPort = 7960;
    // Seconda istanza Trellis in parallelo alla prima (vedi GUIDA_UNITY_INTEGRAZIONE_PIPELINE.md).
    // Default 7960+1 per convenzione, ma NON confermato per questo server specifico — verificare
    // prima di attivare GenerateAsset.useSecondTrellisInstance.
    public int gen3dPort2 = 7961;

    // ── Audio input ─────────────────────────────────────────────────────
    // null/"" = usa il device di default del sistema.
    public string micDeviceName = "";

    // ── Parametri mondo ─────────────────────────────────────────────────
    // Moltiplicatore applicato alle dimensioni cella (cellVeryCommon/Common/Rare/...) di
    // WorldManager: >1 = oggetti più fitti (celle più piccole), <1 = più radi.
    public float objectDensityMultiplier = 1f;
    // Moltiplicatore applicato a tutte le renderDist* (huge/big/normal/small/tiny) di WorldManager.
    public float renderDistanceMultiplier = 1f;
    // Sovrascrive WorldManager.transitionLengthInChunks (ampiezza della sfumatura tra biomi).
    public int transitionLengthInChunks = 8;
    // Se false, non viene generato/renderizzato il percorso/strada centrale del bioma.
    public bool enablePath = true;

    // Moltiplicatori applicati a scaleHuge/Big/Normal/Small/Tiny di WorldManager, uno per
    // categoria di size (stesso pattern di objectDensityMultiplier: il campo Inspector resta il
    // valore "base", il moltiplicatore si applica al momento in cui l'oggetto viene registrato —
    // non è retroattivo sugli oggetti già piazzati, solo su quelli del prossimo bioma/chunk).
    public float scaleHugeMultiplier = 1f;
    public float scaleBigMultiplier = 1f;
    public float scaleNormalMultiplier = 1f;
    public float scaleSmallMultiplier = 1f;
    public float scaleTinyMultiplier = 1f;

    // Moltiplicatori applicati a pathWidth/pathBlendWidth di WorldManager (larghezza del
    // percorso/strada centrale del bioma e ampiezza della sua sfumatura ai bordi).
    public float pathWidthMultiplier = 1f;
    public float pathBlendWidthMultiplier = 1f;
}

/// <summary>
/// Singleton persistente (DontDestroyOnLoad) che è l'unica sorgente di verità a runtime per le
/// impostazioni utente — server, microfono, parametri mondo. Prima della sua introduzione,
/// script diversi (LLMObjectGeneration, GenerateTexture, LLM3DGeneration, LLMWorldDirector)
/// avevano ciascuno la propria copia locale dell'indirizzo server, quindi cambiarne uno da un
/// pannello Opzioni non si propagava agli altri. Ora tutti leggono da qui (con fallback al
/// proprio campo Inspector se GameSettings.Instance è null, es. in scene di test isolate).
/// Persistenza su disco in PathSettings.GetJsonFolder()/settings.json.
/// </summary>
public class GameSettings : MonoBehaviour
{
    public static GameSettings Instance { get; private set; }

    public GameSettingsData Data { get; private set; } = new GameSettingsData();

    /// <summary>Server (host/porte) cambiato — chi ha un endpoint cablato lo ricalcola.</summary>
    public event Action OnServerSettingsChanged;
    /// <summary>Device microfono cambiato.</summary>
    public event Action OnAudioSettingsChanged;
    /// <summary>Parametri mondo (densità/render distance/blend/path) cambiati.</summary>
    public event Action OnWorldParamsChanged;

    static string SettingsPath => Path.Combine(PathSettings.GetJsonFolder(), "settings.json");

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        transform.SetParent(null);
        DontDestroyOnLoad(gameObject);
        Load();
    }

    // ── Endpoint derivati ───────────────────────────────────────────────
    public string LlmEndpoint => $"http://{Data.serverHost}:{Data.llmPort}/v1/chat/completions";
    public string ImageServerBase => $"http://{Data.serverHost}:{Data.imagePort}";
    public string Gen3DEndpoint => $"http://{Data.serverHost}:{Data.gen3dPort}/generate_vox_only";
    public string Gen3DEndpoint2 => $"http://{Data.serverHost}:{Data.gen3dPort2}/generate_vox_only";

    // ── Setter "applica + salva + notifica" usati dal pannello Opzioni ─────
    public void ApplyServerSettings(string host, int llmPort, int imagePort, int gen3dPort)
    {
        if (!string.IsNullOrWhiteSpace(host)) Data.serverHost = host.Trim();
        Data.llmPort = llmPort;
        Data.imagePort = imagePort;
        Data.gen3dPort = gen3dPort;
        Save();
        OnServerSettingsChanged?.Invoke();
    }

    public void ApplyMicDevice(string deviceName)
    {
        Data.micDeviceName = deviceName ?? "";
        Save();
        OnAudioSettingsChanged?.Invoke();
    }

    public void ApplyWorldParams(float densityMultiplier, float renderDistanceMultiplier, int transitionLengthInChunks, bool enablePath)
    {
        Data.objectDensityMultiplier = Mathf.Max(0.1f, densityMultiplier);
        Data.renderDistanceMultiplier = Mathf.Max(0.1f, renderDistanceMultiplier);
        Data.transitionLengthInChunks = Mathf.Max(1, transitionLengthInChunks);
        Data.enablePath = enablePath;
        Save();
        OnWorldParamsChanged?.Invoke();
    }

    public void ApplyObjectScale(float hugeMultiplier, float bigMultiplier, float normalMultiplier, float smallMultiplier, float tinyMultiplier)
    {
        Data.scaleHugeMultiplier = Mathf.Max(0.1f, hugeMultiplier);
        Data.scaleBigMultiplier = Mathf.Max(0.1f, bigMultiplier);
        Data.scaleNormalMultiplier = Mathf.Max(0.1f, normalMultiplier);
        Data.scaleSmallMultiplier = Mathf.Max(0.1f, smallMultiplier);
        Data.scaleTinyMultiplier = Mathf.Max(0.1f, tinyMultiplier);
        Save();
        OnWorldParamsChanged?.Invoke();
    }

    public void ApplyPathDimensions(float widthMultiplier, float blendWidthMultiplier)
    {
        Data.pathWidthMultiplier = Mathf.Max(0.1f, widthMultiplier);
        Data.pathBlendWidthMultiplier = Mathf.Max(0.1f, blendWidthMultiplier);
        Save();
        OnWorldParamsChanged?.Invoke();
    }

    void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonConvert.DeserializeObject<GameSettingsData>(File.ReadAllText(SettingsPath));
                if (loaded != null) Data = loaded;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[GameSettings] Impossibile caricare settings.json, uso i default: {ex.Message}");
        }
    }

    void Save()
    {
        try
        {
            string dir = PathSettings.GetJsonFolder();
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(Data, Formatting.Indented));
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[GameSettings] Impossibile salvare settings.json: {ex.Message}");
        }
    }
}
