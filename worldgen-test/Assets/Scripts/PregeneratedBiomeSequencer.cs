using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Modalità "tour pregenerato": prima di far giocare, genera per davvero (via LLM + pipeline asset,
/// come una generazione normale) N biomi in sequenza da <see cref="biomePrompts"/>, salvando ogni
/// risultato come snapshot (formato biome_000N_llm_generated.json
/// / biome_000N_current_objects.json) invece di lasciarlo semplicemente "l'ultimo bioma attivo".
/// Una volta che tutti sono pronti (asset 3D già in cache su disco), il gameplay parte normale — il
/// MONDO resta generato proceduralmente a runtime come sempre (chunk streaming, WorldManager
/// invariato) — ma quale bioma è attivo avanza da solo mano a mano che il giocatore percorre la
/// distanza assegnata a ciascuno (<see cref="biomeLengths"/>), applicando lo snapshot successivo
/// con ApplyNewConfig() — zero chiamate LLM durante il gioco, innescato dalla distanza percorsa.
/// </summary>
public class PregeneratedBiomeSequencer : MonoBehaviour
{
    public WorldManager worldManager;
    public LLMWorldDirector llmWorldDirector;

    [Tooltip("Un prompt per bioma, nell'ordine in cui verranno generati e poi attraversati in gioco.")]
    public List<string> biomePrompts = new List<string> { "", "", "" };

    [Tooltip("Lunghezza (world units) di ciascun bioma prima di passare al successivo — un valore per prompt, nello stesso ordine. Se la lista è più corta di biomePrompts, i biomi in eccesso usano defaultBiomeLength. Default allineati alla sessione analizzata (~772/828/2024 unità).")]
    public List<float> biomeLengths = new List<float> { 772f, 828f, 2024f };
    public float defaultBiomeLength = 800f;

    [Tooltip("Timeout di sicurezza (secondi) per singolo bioma durante la pre-generazione, stesso criterio di AutoPromptSequencer.")]
    public float perPromptTimeout = 300f;

    [Tooltip("Se true, parte da solo all'avvio della scena.")]
    public bool startOnAwake = true;

    public bool IsPregenerating { get; private set; }
    public bool IsReady { get; private set; }
    public int CurrentBiomeIndex { get; private set; } = -1;
    public string PregenSessionFolder { get; private set; }

    Vector2 _segmentStart2D;
    bool _advancing;
    int _pregenIndex = -1; // quale bioma sta generando ORA durante la fase di pre-generazione, per etichettare i log di progresso

    void Start()
    {
        if (worldManager == null) worldManager = FindAnyObjectByType<WorldManager>();
        if (llmWorldDirector == null) llmWorldDirector = FindAnyObjectByType<LLMWorldDirector>();
        if (startOnAwake) StartCoroutine(RunSequence());
    }

    // Riusa gli stessi eventi di progresso già letti da FirstGeneration per la sua progress bar —
    // qui invece li logghiamo (console + LogPanel in gioco, se presente): senza questo si vedeva
    // solo "inizio bioma N" e "bioma N pronto" con un buco di anche un paio di minuti in mezzo,
    // tra i 4 step LLM e la generazione degli asset 3D, senza nessun segnale che non fosse bloccato.
    void OnEnable()
    {
        LLMWorldDirector.OnGenerationProgress += HandleGenerationProgress;
        GenerateAsset.OnAssetProgress += HandleAssetProgress;
    }

    void OnDisable()
    {
        LLMWorldDirector.OnGenerationProgress -= HandleGenerationProgress;
        GenerateAsset.OnAssetProgress -= HandleAssetProgress;
    }

    void HandleGenerationProgress(string status, float progress)
    {
        if (!IsPregenerating) return; // ignora eventuali generazioni scatenate fuori da questa sequenza
        LogPregenStatus($"{status} ({progress * 100f:F0}%)");
    }

    void HandleAssetProgress(int done, int total)
    {
        if (!IsPregenerating || total <= 0) return;
        LogPregenStatus($"Generating 3D assets... ({done}/{total})");
    }

    // Riga "viva": si sovrascrive ad ogni progresso granulare (step LLM, asset 3D N/M) — via
    // LogPanelUI.LogProgress, pensata apposta per questo (vedi LogPanelUI.AddLine: due LogProgress
    // di fila sostituiscono la stessa riga invece di accumularsi).
    void LogPregenStatus(string status)
    {
        string msg = $"Biome {_pregenIndex + 1}/{biomePrompts.Count}: {status}";
        Debug.Log($"[PregeneratedBiomeSequencer] {msg}");
        worldManager?.SetStatusText(msg);
    }

    // Riga PERMANENTE nello storico del pannello — usata solo per i momenti chiave (inizio/fine di
    // ogni bioma, sequenza pronta): a differenza di LogPregenStatus/LogProgress, questa non viene
    // mai sovrascritta dal prossimo aggiornamento, quindi resta visibile il completamento di OGNI
    // bioma della sequenza, non solo dell'ultimo.
    void LogPregenEvent(string text)
    {
        Debug.Log($"[PregeneratedBiomeSequencer] {text}");
        worldManager?.logPanel?.LogEvent(text);
    }

    void LogPregenError(string text)
    {
        Debug.LogError($"[PregeneratedBiomeSequencer] {text}");
        worldManager?.logPanel?.LogError(text);
    }

    void Update()
    {
        if (!_advancing || worldManager == null || worldManager.player == null) return;
        if (CurrentBiomeIndex >= biomePrompts.Count - 1) return; // ultimo bioma raggiunto, resta lì

        var p2d = new Vector2(worldManager.player.position.x, worldManager.player.position.z);
        float targetLength = CurrentBiomeIndex < biomeLengths.Count ? biomeLengths[CurrentBiomeIndex] : defaultBiomeLength;

        if (Vector2.Distance(p2d, _segmentStart2D) >= targetLength)
        {
            ApplyPregeneratedBiome(CurrentBiomeIndex + 1);
            _segmentStart2D = p2d;
        }
    }

    public IEnumerator RunSequence()
    {
        if (IsPregenerating || IsReady) yield break;
        if (worldManager == null || llmWorldDirector == null)
        {
            Debug.LogError("[PregeneratedBiomeSequencer] worldManager or llmWorldDirector missing.");
            worldManager?.logPanel?.LogError("PregeneratedBiomeSequencer: worldManager or llmWorldDirector missing.");
            yield break;
        }
        if (biomePrompts.Count == 0)
        {
            LogPregenError("No prompts in biomePrompts.");
            yield break;
        }

        IsPregenerating = true;
        // Congela il player durante tutta la pre-generazione (stesso flag già letto da
        // PlayerMovement/MouseLook per bloccare il movimento mentre si scrive nel campo di testo) —
        // niente vagare in un mondo a metà mentre i biomi si susseguono uno dopo l'altro.
        LLMWorldDirector.IsUIFocused = true;

        PregenSessionFolder = Path.Combine(Application.persistentDataPath, "PregeneratedSequences", $"seq_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(PregenSessionFolder);
        string jsonFolder = PathSettings.GetJsonFolder();

        for (int i = 0; i < biomePrompts.Count; i++)
        {
            string prompt = biomePrompts[i];
            if (string.IsNullOrWhiteSpace(prompt))
            {
                LogPregenError($"Prompt #{i + 1} empty — sequence stopped.");
                IsPregenerating = false;
                LLMWorldDirector.IsUIFocused = false;
                yield break;
            }

            _pregenIndex = i;
            bool terrainDone = false, failed = false;
            Action onDone = () => terrainDone = true;
            Action<string> onFail = _ => failed = true;
            LLMWorldDirector.OnTerrainPipelineDone += onDone;
            LLMWorldDirector.OnGenerationFailed += onFail;

            LogPregenEvent($"Biome {i + 1}/{biomePrompts.Count}: starting generation — \"{prompt}\"");
            llmWorldDirector.SendRequestToLLM(prompt);

            float elapsed = 0f;
            while (!terrainDone && !failed && elapsed < perPromptTimeout)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            LLMWorldDirector.OnTerrainPipelineDone -= onDone;
            LLMWorldDirector.OnGenerationFailed -= onFail;

            if (failed || !terrainDone)
            {
                LogPregenError($"Biome {i + 1}/{biomePrompts.Count} failed or timed out — sequence stopped.");
                IsPregenerating = false;
                LLMWorldDirector.IsUIFocused = false;
                yield break;
            }

            // Il terreno è pronto, ma la generazione 3D degli asset del bioma continua in
            // background (vedi GenerateAsset.IsGenerating) — aspettiamo anche quella: vogliamo che
            // TUTTI gli asset di ogni bioma siano già in cache su disco prima di passare al
            // successivo, così a runtime lo swap è sempre istantaneo (stesso criterio di
            // AutoPromptSequencer/FirstGeneration).
            yield return null;
            yield return null;
            while (GenerateAsset.IsGenerating) yield return null;

            string llmSrc = Path.Combine(jsonFolder, "llm_generated.json");
            string objSrc = Path.Combine(jsonFolder, "current_objects.json");
            if (!File.Exists(llmSrc) || !File.Exists(objSrc))
            {
                LogPregenError($"Biome {i + 1}/{biomePrompts.Count}: config files missing after generation — sequence stopped.");
                IsPregenerating = false;
                LLMWorldDirector.IsUIFocused = false;
                yield break;
            }

            string prefix = $"biome_{(i + 1):D4}";
            File.Copy(llmSrc, Path.Combine(PregenSessionFolder, prefix + "_llm_generated.json"), true);
            File.Copy(objSrc, Path.Combine(PregenSessionFolder, prefix + "_current_objects.json"), true);
            LogPregenEvent($"Biome {i + 1}/{biomePrompts.Count}: ready and saved.");
        }

        IsPregenerating = false;
        IsReady = true;
        LLMWorldDirector.IsUIFocused = false;

        // Il bioma #1 è già quello attivo in questo momento (è l'ultimo generato dal loop sopra se
        // biomePrompts.Count==1, altrimenti va riapplicato esplicitamente perché l'ultimo applicato
        // dal loop è l'ULTIMO della sequenza, non il primo).
        ApplyPregeneratedBiome(0);
        _segmentStart2D = worldManager.player != null
            ? new Vector2(worldManager.player.position.x, worldManager.player.position.z)
            : Vector2.zero;
        _advancing = true;
        LogPregenEvent($"Sequence ready ({biomePrompts.Count} biomes) — gameplay started from biome #1.");
    }

    void ApplyPregeneratedBiome(int index)
    {
        if (index < 0 || index >= biomePrompts.Count) return;

        string prefix = $"biome_{(index + 1):D4}";
        string jsonFolder = PathSettings.GetJsonFolder();
        string srcLlm = Path.Combine(PregenSessionFolder, prefix + "_llm_generated.json");
        string srcObjects = Path.Combine(PregenSessionFolder, prefix + "_current_objects.json");

        if (!File.Exists(srcLlm) || !File.Exists(srcObjects))
        {
            LogPregenError($"Snapshot for biome #{index + 1} missing in {PregenSessionFolder}, apply skipped.");
            return;
        }

        File.Copy(srcLlm, Path.Combine(jsonFolder, "llm_generated.json"), true);
        File.Copy(srcObjects, Path.Combine(jsonFolder, "current_objects.json"), true);
        worldManager.ApplyNewConfig();
        CurrentBiomeIndex = index;

        // Riga permanente anche qui: durante il gameplay vero e proprio (non solo in fase di
        // pre-generazione) il giocatore vede quando entra in un nuovo bioma della sequenza.
        LogPregenEvent($"Biome #{index + 1}/{biomePrompts.Count} reached in-game (no LLM call, assets already cached).");
    }
}
