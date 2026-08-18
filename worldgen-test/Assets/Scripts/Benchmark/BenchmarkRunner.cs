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
/// prima di lanciare il prompt successivo. Il player inizia a camminare (dritto, con salti
/// periodici — vedi AutoWalkerJumper) non appena il primo bioma è pronto, e non si ferma più fino a
/// fine run. Per ciascun bioma registra: tempo di generazione, asset nuovi vs riusati da cache
/// (→ % di riutilizzo), FPS (media/min/1% low) e % di RAM di processo durante la sua permanenza
/// come bioma attivo. A fine run scrive un report .txt + .csv in Assets/Benchmark/.
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

    [Header("Config run")]
    public int promptsPerRun = 10;
    [Tooltip("0 = selezione casuale diversa ad ogni run (basata sull'orologio). Un valore fisso rende la run riproducibile.")]
    public int randomSeed = 0;
    public float delayBetweenPrompts = 1f;
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
    }

    readonly List<PromptResult> _results = new List<PromptResult>();
    readonly List<(string prompt, bool structured)> _selection = new List<(string, bool)>();

    bool _terrainDone, _failed, _walkingStarted;
    int _newCount, _cachedCount;

    void Awake()
    {
        if (resetStateOnAwake) ResetGenerationState();

        if (llmWorldDirector == null) llmWorldDirector = FindAnyObjectByType<LLMWorldDirector>();
        if (autoWalker == null) autoWalker = FindAnyObjectByType<AutoWalkerJumper>();
        if (metricsLogger == null) metricsLogger = FindAnyObjectByType<BenchmarkMetricsLogger>();
        if (playerMovement == null) playerMovement = FindAnyObjectByType<PlayerMovement>();
    }

    void OnEnable()
    {
        GenerateAsset.OnAssetClassified   += OnAssetClassified;
        GenerateAsset.OnBiomeAssetsReady  += OnBiomeReady;
    }

    void OnDisable()
    {
        GenerateAsset.OnAssetClassified  -= OnAssetClassified;
        GenerateAsset.OnBiomeAssetsReady -= OnBiomeReady;
    }

    void Start()
    {
        BuildSelection();
        if (_selection.Count > 0) StartCoroutine(RunBenchmark());
    }

    // ── Reset "parti da 0" ────────────────────────────────────────────────────────────────────
    void ResetGenerationState()
    {
        Debug.Log("<color=orange>[BenchmarkRunner]</color> Reset stato: azzero asset, texture e registri oggetti per iniziare la run da 0.");

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

    static void DeleteDirectoryContents(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (string file in Directory.GetFiles(path))
        {
            if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(file); } catch (Exception e) { Debug.LogWarning($"[BenchmarkRunner] Impossibile cancellare '{file}': {e.Message}"); }
        }
    }

    static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            try { File.Delete(path); } catch (Exception e) { Debug.LogWarning($"[BenchmarkRunner] Impossibile cancellare '{path}': {e.Message}"); }
        }
    }

    // ── Selezione prompt ──────────────────────────────────────────────────────────────────────
    void BuildSelection()
    {
        _selection.Clear();
        if (biomePoolJson == null)
        {
            Debug.LogError("[BenchmarkRunner] Nessun biomePoolJson assegnato in Inspector — impossibile selezionare i prompt.");
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

        Debug.Log("<color=cyan>[BenchmarkRunner]</color> Prompt selezionati: " +
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
        Debug.Log("<color=green>[BenchmarkRunner]</color> Run completata — report scritto in Assets/Benchmark/.");
    }

    IEnumerator RunSinglePrompt(int index, string prompt, bool structured)
    {
        if (llmWorldDirector == null)
        {
            Debug.LogError("[BenchmarkRunner] Nessun LLMWorldDirector in scena — impossibile eseguire il prompt.");
            yield break;
        }

        Debug.Log($"<color=cyan>[BenchmarkRunner]</color> Prompt {index + 1}/{_selection.Count}: \"{prompt}\" ({(structured ? "structured" : "natural")})");

        _terrainDone = false;
        _failed      = false;
        _newCount    = 0;
        _cachedCount = 0;

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
        });

        if (!success)
            Debug.LogWarning($"[BenchmarkRunner] Prompt {index + 1} fallito o in timeout dopo {generationSeconds:F1}s.");
        else
            Debug.Log($"<color=cyan>[BenchmarkRunner]</color> Prompt {index + 1} completato in {generationSeconds:F1}s — nuovi:{_newCount} cache:{_cachedCount} riuso:{_results[_results.Count - 1].reusePercent:F0}%");
    }

    void OnAssetClassified(string name, bool fromCache)
    {
        if (fromCache) _cachedCount++; else _newCount++;
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
        txt.AppendLine("=== Benchmark WorldGen — report run ===");
        txt.AppendLine($"Data/ora: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        txt.AppendLine($"Seed selezione prompt: {randomSeed}");
        txt.AppendLine($"Prompt eseguiti: {_results.Count}/{_selection.Count}");
        txt.AppendLine();

        var csv = new StringBuilder();
        csv.AppendLine("index,prompt,structured,success,generation_seconds,new_assets,cached_assets,reuse_percent,fps_avg,fps_min,fps_1pct_low,ram_avg_percent,ram_max_percent");

        int successCount = 0;
        float sumGenTime = 0f, sumReuse = 0f, sumFpsAvg = 0f, sumRamAvg = 0f;
        int totalNew = 0, totalCached = 0;

        for (int i = 0; i < _results.Count; i++)
        {
            var r = _results[i];
            bool hasFps = r.fpsRam.sampleCount > 0;

            txt.AppendLine($"[{i + 1}] \"{r.prompt}\" ({(r.structured ? "structured" : "natural")}) — {(r.success ? "OK" : "FALLITO/TIMEOUT")}");
            txt.AppendLine($"    Tempo generazione: {r.generationSeconds:F1}s");
            txt.AppendLine($"    Asset nuovi: {r.newAssets}  |  Asset da cache: {r.cachedAssets}  |  Riutilizzo: {r.reusePercent:F1}%");
            txt.AppendLine(hasFps
                ? $"    FPS medio: {r.fpsRam.avgFps:F1}  |  FPS min: {r.fpsRam.minFps:F1}  |  1% low: {r.fpsRam.fps1PercentLow:F1}  |  RAM media: {r.fpsRam.avgRamPercent:F1}%  |  RAM picco: {r.fpsRam.maxRamPercent:F1}%"
                : "    FPS/RAM: N/A (bioma mai diventato attivo — prompt fallito/timeout)");
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
                hasFps ? r.fpsRam.maxRamPercent.ToString("F1", CultureInfo.InvariantCulture) : ""));

            if (r.success)
            {
                successCount++;
                sumGenTime += r.generationSeconds;
                sumReuse   += r.reusePercent;
                totalNew   += r.newAssets;
                totalCached += r.cachedAssets;
                if (hasFps) { sumFpsAvg += r.fpsRam.avgFps; sumRamAvg += r.fpsRam.avgRamPercent; }
            }
        }

        txt.AppendLine("--- Riepilogo ---");
        txt.AppendLine($"Prompt riusciti: {successCount}/{_results.Count}");
        if (successCount > 0)
        {
            txt.AppendLine($"Tempo generazione medio: {sumGenTime / successCount:F1}s");
            txt.AppendLine($"Asset nuovi totali: {totalNew}  |  Asset da cache totali: {totalCached}");
            txt.AppendLine($"Riutilizzo medio per-bioma: {sumReuse / successCount:F1}%  |  Riutilizzo cumulativo: {(totalNew + totalCached > 0 ? totalCached * 100f / (totalNew + totalCached) : 0f):F1}%");
            txt.AppendLine($"FPS medio complessivo: {sumFpsAvg / successCount:F1}  |  RAM media complessiva: {sumRamAvg / successCount:F1}%");
        }

        File.WriteAllText(txtPath, txt.ToString());
        File.WriteAllText(csvPath, csv.ToString());
#if UNITY_EDITOR
        AssetDatabase.Refresh();
#endif
    }

    static string CsvEscape(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    static string GetBenchmarkFolder()
    {
#if UNITY_EDITOR
        return Path.Combine(Application.dataPath, "Benchmark");
#else
        return Path.Combine(Application.persistentDataPath, "Benchmark");
#endif
    }
}
