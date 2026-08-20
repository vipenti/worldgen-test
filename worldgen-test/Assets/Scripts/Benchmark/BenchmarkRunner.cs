using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Harness di benchmark per il paper. All'avvio (Awake, PRIMA di qualunque altro script — vedi
/// DefaultExecutionOrder) azzera completamente asset/texture/registri così la run parte sempre
/// da 0 asset e 0 texture. Sceglie 10 prompt a caso (ordine casuale, senza ripetizioni) da un pool
/// di 50 biomi predefiniti (25 "structured" + 25 "natural", vedi Assets/Json/benchmark_biome_pool.json),
/// li esegue in sequenza sullo stesso LLMWorldDirector usato dal gioco, aspettando ogni volta che la
/// generazione precedente sia COMPLETAMENTE finita (stesso identico criterio di AutoPromptSequencer)
/// prima di lanciare il prompt successivo. Prima ancora del primo prompt (vedi SpawnWorldZero,
/// chiamata in Awake) il player spawna su un bioma piatto e vuoto ("world zero") scritto apposta su
/// disco, sempre uguale, invece che sull'ultimo bioma rimasto da una sessione precedente. Il player
/// inizia a camminare (dritto verso nord, con salti solo sui gradini — vedi AutoWalkerJumper) non
/// appena il primo bioma VERO è pronto, e non si ferma più fino a fine run. Per ciascun bioma
/// registra: tempo di generazione, asset nuovi vs riusati da cache (→ % di riutilizzo), FPS
/// (media/min/1% low), % di RAM di processo durante la sua permanenza come bioma attivo, e la
/// latenza di ogni singola chiamata HTTP verso i server LLM/generazione (vedi LlmCallMetrics:
/// chat-completion, image-gen, 3D voxel-gen). A fine run scrive un report .txt + .csv in
/// Assets/Benchmark/.
/// </summary>
[DefaultExecutionOrder(-1000)] // il reset in Awake deve girare prima di TextureAtlasManager & co, che leggono i registri da disco nel proprio Awake
public class BenchmarkRunner : MonoBehaviour
{
    [Header("Riferimenti (auto-trovati in scena se lasciati vuoti)")]
    public LLMWorldDirector llmWorldDirector;
    public AutoWalkerJumper autoWalker;
    public BenchmarkMetricsLogger metricsLogger;
    public PlayerMovement playerMovement;

    [Header("Pool prompt")]
    [Tooltip("Assets/Json/benchmark_biome_pool.json — 25 prompt 'structured' + 25 'natural'.")]
    public TextAsset biomePoolJson;

    [Header("Spawn iniziale (\"world zero\")")]
    [Tooltip("Scrive un bioma piatto e vuoto in llm_generated.json PRIMA che WorldManager carichi il terreno iniziale, così il player spawna su una pianura piatta nota e sempre uguale invece che sull'ultimo bioma rimasto su disco da una sessione precedente. Sovrascritto automaticamente non appena il primo bioma vero della run è pronto.")]
    public bool spawnOnFlatWorldZero = true;
    [Tooltip("Assets/Json/benchmark_world_zero.json — BiomeConfig piatta (ampBillow/ampRidged=0, niente laghi) usata come pianura di partenza.")]
    public TextAsset worldZeroConfigJson;

    [Header("Config run")]
    public int promptsPerRun = 10;
    [Tooltip("0 = selezione casuale diversa ad ogni run (basata sull'orologio). Un valore fisso rende la run riproducibile.")]
    public int randomSeed = 0;
    public float delayBetweenPrompts = 1f;
    [Tooltip("Distanza (unità mondo, stessa scala di AutoWalkerJumper.walkSpeed) percorsa DENTRO l'ultimo bioma prima di fermare il walker e chiudere la run. Senza questa attesa il segmento FPS/RAM dell'ultimo bioma avrebbe pochi o zero campioni: OnBiomeReady() apre il suo segmento nello stesso istante in cui il ciclo prompt finisce, quindi il player non faceva quasi in tempo a entrarci davvero prima dello StopWalking().")]
    public float finalBiomeWalkDistance = 400f;
    [Tooltip("Timeout di sicurezza per singolo prompt: oltre questo tempo si passa comunque al successivo invece di restare bloccati per sempre.")]
    public float perPromptTimeout = 600f;
    [Tooltip("Cancella asset/texture/registri generati e svuota le cache statiche PRIMA di iniziare la run, così si parte sempre da 0. Disattivare solo per debug del runner stesso (es. per non aspettare la generazione texture materiali ad ogni test).")]
    public bool resetStateOnAwake = true;

    public bool IsRunning { get; private set; }

    [Serializable]
    class PromptResult
    {
        public string prompt;
        public bool structured;
        public bool success;
        public float generationSeconds;
        public int newAssets;
        public int cachedAssets;
        public float reusePercent;
        public BenchmarkMetricsLogger.SegmentStats fpsRam;
        public Dictionary<string, LlmLatencyStats> llmLatencies;
    }

    // Statistiche di latenza per un singolo callType (es. "chat_completion", "asset_3d_gen") nella
    // finestra di un prompt: ogni singola chiamata HTTP verso i server LLM/generazione conta, anche
    // le retry su 503 (contate in failedCount, ma la loro durata entra comunque in avg/min/max —
    // è tempo di attesa reale speso da quel prompt).
    [Serializable]
    public struct LlmLatencyStats
    {
        public int count;
        public int failedCount;
        public float avgSeconds;
        public float minSeconds;
        public float maxSeconds;
    }

    readonly List<PromptResult> _results = new List<PromptResult>();
    readonly List<(string prompt, bool structured)> _selection = new List<(string, bool)>();

    // Latenze delle chiamate LLM nella finestra del prompt corrente (svuotato a ogni RunSinglePrompt)
    // e nell'intera run (mai svuotato, usato per il riepilogo aggregato a fine run).
    readonly Dictionary<string, List<(float seconds, bool success)>> _currentPromptLatencies = new Dictionary<string, List<(float, bool)>>();
    readonly Dictionary<string, List<(float seconds, bool success)>> _allRunLatencies = new Dictionary<string, List<(float, bool)>>();

    bool _terrainDone, _failed, _walkingStarted;
    int _newCount, _cachedCount;

    void Awake()
    {
        if (resetStateOnAwake) ResetGenerationState();
        if (spawnOnFlatWorldZero) SpawnWorldZero();

        if (llmWorldDirector == null) llmWorldDirector = FindAnyObjectByType<LLMWorldDirector>();
        if (autoWalker == null) autoWalker = FindAnyObjectByType<AutoWalkerJumper>();
        if (metricsLogger == null) metricsLogger = FindAnyObjectByType<BenchmarkMetricsLogger>();
        if (playerMovement == null) playerMovement = FindAnyObjectByType<PlayerMovement>();
    }

    void OnEnable()
    {
        GenerateAsset.OnAssetClassified   += OnAssetClassified;
        GenerateAsset.OnBiomeAssetsReady  += OnBiomeReady;
        LlmCallMetrics.OnCallCompleted    += OnLlmCallCompleted;
    }

    void OnDisable()
    {
        GenerateAsset.OnAssetClassified  -= OnAssetClassified;
        GenerateAsset.OnBiomeAssetsReady -= OnBiomeReady;
        LlmCallMetrics.OnCallCompleted   -= OnLlmCallCompleted;
    }

    void Start()
    {
        BuildSelection();
        if (_selection.Count > 0) StartCoroutine(RunBenchmark());
    }

    // ── Reset "parti da 0" ────────────────────────────────────────────────────────────────────
    void ResetGenerationState()
    {
        Debug.Log("<color=orange>[BenchmarkRunner]</color> Resetting state: clearing assets, textures and object registries to start the run from 0.");

        DeleteDirectoryContents(PathSettings.GetObjectsFolder());   // .voxjson generati
        DeleteDirectoryContents(PathSettings.GetTextureFolder());   // texture materiali generate
        DeleteDirectoryContents(PathSettings.GetImagesFolder());    // immagini intermedie oggetto

        string jsonFolder = PathSettings.GetJsonFolder();
        DeleteFileIfExists(Path.Combine(jsonFolder, "all_objects.json"));
        DeleteFileIfExists(Path.Combine(jsonFolder, "current_objects.json"));
        DeleteFileIfExists(Path.Combine(jsonFolder, "materials.json"));

        // Cartella prefab/materiale-vertex-color generata da VoxelRenderer — solo path Editor,
        // non passa da PathSettings (vedi VoxelRenderer.assetFolder).
#if UNITY_EDITOR
        DeleteDirectoryContents(Path.Combine(Application.dataPath, "GeneratedAsset"));
#endif

        // Cache statica in RAM: sopravvive a un semplice reload di scena senza domain reload, va
        // svuotata esplicitamente qui altrimenti gli oggetti della run precedente risulterebbero
        // "riusati da cache" anche nella prima run pulita.
        VoxelRenderer.RuntimeMeshCache.Clear();

#if UNITY_EDITOR
        AssetDatabase.Refresh();
#endif
    }

    // ── Spawn iniziale su un bioma piatto e vuoto ("world zero") ─────────────────────────────────
    // Gira in Awake, PRIMA che WorldManager legga llm_generated.json nel proprio Start/InitRoutine
    // (garantito dal DefaultExecutionOrder(-1000) di questa classe: tutti gli Awake girano prima
    // di qualunque Start, in ordine di esecuzione). Scrivendo qui la pianura piatta, il player
    // spawna sempre sullo stesso terreno noto invece che sull'ultimo bioma rimasto su disco da una
    // sessione precedente (che potrebbe essere una montagna, un canyon, o altro terreno instabile).
    // Nessun current_objects.json viene scritto: ResetGenerationState() lo ha già cancellato (o non
    // esiste), quindi zero decorazioni sulla pianura — WorldManager salta silenziosamente il
    // caricamento oggetti se il file manca. Il file viene scritto direttamente su disco via
    // File.ReadAllText/WriteAllText (non passa da AssetDatabase/Resources), stesso meccanismo con
    // cui WorldManager.LoadLatestConfig() legge llm_generated.json — nessun import Editor richiesto
    // perché la lettura a runtime sia corretta.
    void SpawnWorldZero()
    {
        if (worldZeroConfigJson == null)
        {
            Debug.LogWarning("[BenchmarkRunner] spawnOnFlatWorldZero is enabled but worldZeroConfigJson is not assigned — skipping, the player will spawn on whatever biome was last on disk.");
            return;
        }

        string jsonFolder = PathSettings.GetJsonFolder();
        Directory.CreateDirectory(jsonFolder);
        File.WriteAllText(Path.Combine(jsonFolder, "llm_generated.json"), worldZeroConfigJson.text);
        Debug.Log("<color=orange>[BenchmarkRunner]</color> World zero (flat plain) written — the player will spawn there until the first biome is ready.");
    }

    static void DeleteDirectoryContents(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (string file in Directory.GetFiles(path))
        {
            if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(file); } catch (Exception e) { Debug.LogWarning($"[BenchmarkRunner] Could not delete '{file}': {e.Message}"); }
        }
    }

    static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            try { File.Delete(path); } catch (Exception e) { Debug.LogWarning($"[BenchmarkRunner] Could not delete '{path}': {e.Message}"); }
        }
    }

    // ── Selezione prompt ──────────────────────────────────────────────────────────────────────
    void BuildSelection()
    {
        _selection.Clear();
        if (biomePoolJson == null)
        {
            Debug.LogError("[BenchmarkRunner] No biomePoolJson assigned in the Inspector — cannot select prompts.");
            return;
        }

        var root = JObject.Parse(biomePoolJson.text);
        var pool = new List<(string prompt, bool structured)>();
        foreach (var p in (JArray)root["structured"]) pool.Add((p.ToString(), true));
        foreach (var p in (JArray)root["natural"])    pool.Add((p.ToString(), false));

        var rng = randomSeed != 0 ? new System.Random(randomSeed) : new System.Random();
        for (int i = pool.Count - 1; i > 0; i--) // Fisher-Yates
        {
            int j = rng.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        int count = Mathf.Min(promptsPerRun, pool.Count);
        for (int i = 0; i < count; i++) _selection.Add(pool[i]);

        Debug.Log("<color=cyan>[BenchmarkRunner]</color> Selected prompts: " +
            string.Join(" | ", _selection.ConvertAll(s => $"\"{s.prompt}\" ({(s.structured ? "structured" : "natural")})")));
    }

    // ── Ciclo principale ──────────────────────────────────────────────────────────────────────
    IEnumerator RunBenchmark()
    {
        IsRunning = true;

        for (int i = 0; i < _selection.Count; i++)
        {
            var (prompt, structured) = _selection[i];
            yield return StartCoroutine(RunSinglePrompt(i, prompt, structured));

            if (delayBetweenPrompts > 0f) yield return new WaitForSeconds(delayBetweenPrompts);
        }

        // Lascia camminare il player DENTRO l'ultimo bioma per finalBiomeWalkDistance unità prima di
        // fermarsi — senza questa pausa il segmento dell'ultimo bioma verrebbe chiuso quasi subito
        // (vedi tooltip su finalBiomeWalkDistance), con FPS/RAM raccolti quasi a vuoto.
        if (autoWalker != null && finalBiomeWalkDistance > 0f && _results.Count > 0 && _results[_results.Count - 1].success)
        {
            float walkSeconds = autoWalker.walkSpeed > 0f ? finalBiomeWalkDistance / autoWalker.walkSpeed : 0f;
            if (walkSeconds > 0f) yield return new WaitForSeconds(walkSeconds);
        }

        // Chiude il segmento FPS/RAM del bioma rimasto attivo alla fine della run.
        if (metricsLogger != null && _results.Count > 0)
        {
            var last = _results[_results.Count - 1];
            if (last.success && last.fpsRam.sampleCount == 0)
                last.fpsRam = metricsLogger.EndSegment();
        }

        autoWalker?.StopWalking();
        IsRunning = false;

        WriteReport();
        Debug.Log("<color=green>[BenchmarkRunner]</color> Run completed — report written to Assets/Benchmark/.");
    }

    IEnumerator RunSinglePrompt(int index, string prompt, bool structured)
    {
        if (llmWorldDirector == null)
        {
            Debug.LogError("[BenchmarkRunner] No LLMWorldDirector in scene — cannot run the prompt.");
            yield break;
        }

        Debug.Log($"<color=cyan>[BenchmarkRunner]</color> Prompt {index + 1}/{_selection.Count}: \"{prompt}\" ({(structured ? "structured" : "natural")})");

        _terrainDone = false;
        _failed      = false;
        _newCount    = 0;
        _cachedCount = 0;
        _currentPromptLatencies.Clear();

        LLMWorldDirector.OnTerrainPipelineDone += OnTerrainDone;
        LLMWorldDirector.OnGenerationFailed    += OnFailed;

        float startTime = Time.realtimeSinceStartup;
        llmWorldDirector.SendRequestToLLM(prompt);

        float elapsed = 0f;
        while (!_terrainDone && !_failed && elapsed < perPromptTimeout)
        {
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        // Stesso identico criterio di completamento usato da AutoPromptSequencer: il terreno
        // segnala "fatto" molto prima che gli asset 3D del bioma abbiano finito di generare in
        // background (vedi LLMWorldDirector.SizeFrequencyCoroutine → GenerateAsset.GenerateNew,
        // fire-and-forget) — senza questa attesa extra il prompt successivo partirebbe mentre la
        // generazione asset del bioma corrente è ancora a metà.
        if (!_failed && _terrainDone)
            while (GenerateAsset.IsGenerating) yield return null;

        LLMWorldDirector.OnTerrainPipelineDone -= OnTerrainDone;
        LLMWorldDirector.OnGenerationFailed    -= OnFailed;

        float generationSeconds = Time.realtimeSinceStartup - startTime;
        bool success = _terrainDone && !_failed;

        _results.Add(new PromptResult
        {
            prompt            = prompt,
            structured        = structured,
            success           = success,
            generationSeconds = generationSeconds,
            newAssets         = _newCount,
            cachedAssets      = _cachedCount,
            reusePercent      = (_newCount + _cachedCount) > 0 ? _cachedCount * 100f / (_newCount + _cachedCount) : 0f,
            llmLatencies      = ComputeLatencyStats(_currentPromptLatencies),
        });

        if (!success)
            Debug.LogWarning($"[BenchmarkRunner] Prompt {index + 1} failed or timed out after {generationSeconds:F1}s.");
        else
            Debug.Log($"<color=cyan>[BenchmarkRunner]</color> Prompt {index + 1} completed in {generationSeconds:F1}s — new:{_newCount} cached:{_cachedCount} reuse:{_results[_results.Count - 1].reusePercent:F0}%");
    }

    void OnAssetClassified(string name, bool fromCache)
    {
        if (fromCache) _cachedCount++; else _newCount++;
    }

    // Ogni singola chiamata HTTP verso i server LLM/generazione (chat-completion LM Studio,
    // image-gen, 3D voxel-gen/Trellis) arriva qui — vedi LlmCallMetrics. Finché la run è in corso
    // ogni chiamata viene accodata sia nella finestra del prompt corrente sia nel totale di run.
    void OnLlmCallCompleted(string callType, float seconds, bool success)
    {
        if (!IsRunning) return;
        AddLatencySample(_currentPromptLatencies, callType, seconds, success);
        AddLatencySample(_allRunLatencies, callType, seconds, success);
    }

    static void AddLatencySample(Dictionary<string, List<(float seconds, bool success)>> target, string callType, float seconds, bool success)
    {
        if (!target.TryGetValue(callType, out var list))
        {
            list = new List<(float, bool)>();
            target[callType] = list;
        }
        list.Add((seconds, success));
    }

    static Dictionary<string, LlmLatencyStats> ComputeLatencyStats(Dictionary<string, List<(float seconds, bool success)>> source)
    {
        var result = new Dictionary<string, LlmLatencyStats>();
        foreach (var kvp in source)
        {
            var samples = kvp.Value;
            if (samples.Count == 0) continue;

            float sum = 0f, min = float.MaxValue, max = float.MinValue;
            int failed = 0;
            foreach (var (seconds, success) in samples)
            {
                sum += seconds;
                if (seconds < min) min = seconds;
                if (seconds > max) max = seconds;
                if (!success) failed++;
            }

            result[kvp.Key] = new LlmLatencyStats
            {
                count       = samples.Count,
                failedCount = failed,
                avgSeconds  = sum / samples.Count,
                minSeconds  = min,
                maxSeconds  = max,
            };
        }
        return result;
    }

    void OnTerrainDone() => _terrainDone = true;
    void OnFailed(string reason) => _failed = true;

    // Scatta ogni volta che un nuovo bioma diventa visivamente attivo (swap terreno completato).
    // Chiude il segmento FPS/RAM del bioma precedente (il prompt appena appeso a _results, se
    // ancora senza fpsRam) e ne apre uno nuovo per quello che sta per comparire. Alla primissima
    // chiamata avvia anche la camminata automatica.
    // NOTA: se un prompt fallisce/va in timeout il suo bioma non arriva mai a "pronto", quindi non
    // c'è un boundary pulito per lui — il suo fpsRam resta vuoto (sampleCount 0, riportato come
    // N/A nel report) e il tempo morto finisce nel segmento del bioma successivo.
    void OnBiomeReady()
    {
        if (metricsLogger != null)
        {
            if (_results.Count > 0)
            {
                var last = _results[_results.Count - 1];
                if (last.fpsRam.sampleCount == 0) last.fpsRam = metricsLogger.EndSegment();
            }

            string label = _results.Count < _selection.Count ? _selection[_results.Count].prompt : $"biome_{_results.Count}";
            bool structured = _results.Count < _selection.Count && _selection[_results.Count].structured;
            metricsLogger.BeginSegment(label, structured);
        }

        if (!_walkingStarted)
        {
            _walkingStarted = true;
            if (playerMovement != null) playerMovement.enabled = false; // evita due script sullo stesso CharacterController
            autoWalker?.BeginWalking();
        }
    }

    // ── Report ────────────────────────────────────────────────────────────────────────────────
    void WriteReport()
    {
        string folder = GetBenchmarkFolder();
        Directory.CreateDirectory(folder);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string txtPath = Path.Combine(folder, $"run_{stamp}.txt");
        string csvPath = Path.Combine(folder, $"run_{stamp}.csv");

        var txt = new StringBuilder();
        txt.AppendLine("=== Benchmark WorldGen — run report ===");
        txt.AppendLine($"Date/time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        txt.AppendLine($"Prompt selection seed: {randomSeed}");
        txt.AppendLine($"Prompts run: {_results.Count}/{_selection.Count}");
        txt.AppendLine();

        var csv = new StringBuilder();
        csv.AppendLine("index,prompt,structured,success,generation_seconds,new_assets,cached_assets,reuse_percent,fps_avg,fps_min,fps_1pct_low,ram_avg_percent,ram_max_percent,ram_avg_mb,ram_max_mb,llm_call_latencies");

        int successCount = 0;
        float sumGenTime = 0f, sumReuse = 0f, sumFpsAvg = 0f, sumRamAvg = 0f, sumRamMbAvg = 0f;
        int totalNew = 0, totalCached = 0;

        for (int i = 0; i < _results.Count; i++)
        {
            var r = _results[i];
            bool hasFps = r.fpsRam.sampleCount > 0;

            txt.AppendLine($"[{i + 1}] \"{r.prompt}\" ({(r.structured ? "structured" : "natural")}) — {(r.success ? "OK" : "FAILED/TIMEOUT")}");
            txt.AppendLine($"    Generation time: {r.generationSeconds:F1}s");
            txt.AppendLine($"    New assets: {r.newAssets}  |  Cached assets: {r.cachedAssets}  |  Reuse: {r.reusePercent:F1}%");
            txt.AppendLine(hasFps
                ? $"    Avg FPS: {r.fpsRam.avgFps:F1}  |  Min FPS: {r.fpsRam.minFps:F1}  |  1% low: {r.fpsRam.fps1PercentLow:F1}  |  Avg RAM: {r.fpsRam.avgRamPercent:F1}% ({r.fpsRam.avgRamMB:F0} MB)  |  Peak RAM: {r.fpsRam.maxRamPercent:F1}% ({r.fpsRam.maxRamMB:F0} MB)"
                : "    FPS/RAM: N/A (biome never became active — prompt failed/timed out)");

            if (r.llmLatencies != null && r.llmLatencies.Count > 0)
            {
                txt.AppendLine("    LLM call latency:");
                foreach (var kvp in r.llmLatencies)
                {
                    var s = kvp.Value;
                    string failedNote = s.failedCount > 0 ? $"  ({s.failedCount} failed/retried)" : "";
                    txt.AppendLine($"      {kvp.Key}: {s.count} calls  |  avg {s.avgSeconds:F2}s  |  min {s.minSeconds:F2}s  |  max {s.maxSeconds:F2}s{failedNote}");
                }
            }
            else
            {
                txt.AppendLine("    LLM call latency: N/A (no calls recorded for this prompt)");
            }
            txt.AppendLine();

            csv.AppendLine(string.Join(",",
                i + 1,
                CsvEscape(r.prompt),
                r.structured,
                r.success,
                r.generationSeconds.ToString("F2", CultureInfo.InvariantCulture),
                r.newAssets,
                r.cachedAssets,
                r.reusePercent.ToString("F1", CultureInfo.InvariantCulture),
                hasFps ? r.fpsRam.avgFps.ToString("F1", CultureInfo.InvariantCulture) : "",
                hasFps ? r.fpsRam.minFps.ToString("F1", CultureInfo.InvariantCulture) : "",
                hasFps ? r.fpsRam.fps1PercentLow.ToString("F1", CultureInfo.InvariantCulture) : "",
                hasFps ? r.fpsRam.avgRamPercent.ToString("F1", CultureInfo.InvariantCulture) : "",
                hasFps ? r.fpsRam.maxRamPercent.ToString("F1", CultureInfo.InvariantCulture) : "",
                hasFps ? r.fpsRam.avgRamMB.ToString("F0", CultureInfo.InvariantCulture) : "",
                hasFps ? r.fpsRam.maxRamMB.ToString("F0", CultureInfo.InvariantCulture) : "",
                CsvEscape(FormatLatenciesForCsv(r.llmLatencies))));

            if (r.success)
            {
                successCount++;
                sumGenTime += r.generationSeconds;
                sumReuse   += r.reusePercent;
                totalNew   += r.newAssets;
                totalCached += r.cachedAssets;
                if (hasFps) { sumFpsAvg += r.fpsRam.avgFps; sumRamAvg += r.fpsRam.avgRamPercent; sumRamMbAvg += r.fpsRam.avgRamMB; }
            }
        }

        txt.AppendLine("--- Summary ---");
        txt.AppendLine($"Successful prompts: {successCount}/{_results.Count}");
        if (successCount > 0)
        {
            txt.AppendLine($"Average generation time: {sumGenTime / successCount:F1}s");
            txt.AppendLine($"Total new assets: {totalNew}  |  Total cached assets: {totalCached}");
            txt.AppendLine($"Average per-biome reuse: {sumReuse / successCount:F1}%  |  Cumulative reuse: {(totalNew + totalCached > 0 ? totalCached * 100f / (totalNew + totalCached) : 0f):F1}%");
            txt.AppendLine($"Overall average FPS: {sumFpsAvg / successCount:F1}  |  Overall average RAM: {sumRamAvg / successCount:F1}% ({sumRamMbAvg / successCount:F0} MB)");
        }

        var runLatencies = ComputeLatencyStats(_allRunLatencies);
        if (runLatencies.Count > 0)
        {
            txt.AppendLine();
            txt.AppendLine("--- LLM call latency (aggregated across the whole run) ---");
            foreach (var kvp in runLatencies)
            {
                var s = kvp.Value;
                string failedNote = s.failedCount > 0 ? $"  ({s.failedCount} failed/retried)" : "";
                txt.AppendLine($"{kvp.Key}: {s.count} total calls  |  avg {s.avgSeconds:F2}s  |  min {s.minSeconds:F2}s  |  max {s.maxSeconds:F2}s{failedNote}");
            }
        }

        File.WriteAllText(txtPath, txt.ToString());
        File.WriteAllText(csvPath, csv.ToString());
#if UNITY_EDITOR
        AssetDatabase.Refresh();
#endif
    }

    static string CsvEscape(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    // Codifica compatta delle statistiche di latenza per una singola cella CSV, es.:
    // "chat_completion:n=1,avg=1.23s,min=1.23s,max=1.23s|asset_create_image:n=3,avg=0.45s,min=0.30s,max=0.60s"
    static string FormatLatenciesForCsv(Dictionary<string, LlmLatencyStats> latencies)
    {
        if (latencies == null || latencies.Count == 0) return "";
        var parts = new List<string>();
        foreach (var kvp in latencies)
        {
            var s = kvp.Value;
            parts.Add($"{kvp.Key}:n={s.count},avg={s.avgSeconds.ToString("F2", CultureInfo.InvariantCulture)}s,min={s.minSeconds.ToString("F2", CultureInfo.InvariantCulture)}s,max={s.maxSeconds.ToString("F2", CultureInfo.InvariantCulture)}s");
        }
        return string.Join("|", parts);
    }

    static string GetBenchmarkFolder()
    {
#if UNITY_EDITOR
        return Path.Combine(Application.dataPath, "Benchmark");
#else
        return Path.Combine(Application.persistentDataPath, "Benchmark");
#endif
    }
}
