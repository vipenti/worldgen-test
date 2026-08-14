using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json.Linq;

public class GenerateAsset : MonoBehaviour
{
    [Header("Input")]
    public string listPath    = "Assets/Json/current_objects.json";
    public string assetFolder = "Assets/GeneratedAsset";

    [Header("References")]
    public LLMObjectGeneration llmObjectGeneration;
    public WorldManager worldManager;

    [Header("Pipeline batch — 2 worker Trellis in parallelo")]
    [Tooltip("Usa una SECONDA istanza Trellis in parallelo alla prima (GameSettings.gen3dPort2, porta 7961) invece di generare un oggetto alla volta — vedi GUIDA_UNITY_INTEGRAZIONE_PIPELINE.md. Porta 7961 confermata raggiungibile (GET /ping → 200, busy:false) il 13/08.")]
    public bool useSecondTrellisInstance = true;
    [Tooltip("Tentativi di retry su un 503 'Server is busy' di Trellis prima di rinunciare a un singolo oggetto (non blocca gli altri, vedi TrellisConsumer).")]
    public int trellisBusyMaxRetries = 8;
    public float trellisBusyRetryDelaySeconds = 2f;

    public static bool              IsGenerating       { get; private set; }
    public static event Action         OnAllAssetsComplete;
    public static event Action<int, int> OnAssetProgress;
    // Sblocca la transizione visiva verso il nuovo bioma (vedi LLMWorldDirector.TriggerTerrainTransition).
    // Prima si sbloccava non appena la sola Coda A (macro-asset critici) finiva, mentre la Coda B
    // (decorazioni minori) continuava a generare in sottofondo — il nuovo bioma appariva quindi già
    // "vivo" ma pieno di billboard placeholder per gli oggetti minori, che poi comparivano a scatti
    // mentre il giocatore ci camminava dentro. Ora c'è un'unica lista e questo evento scatta solo
    // quando è TUTTA generata, quindi il bioma appare completo fin dal primo istante.
    public static event Action         OnBiomeAssetsReady;

    public void GenerateNew(List<string> names)
    {
        // CORREZIONE CRITICA SICUREZZA: Accendiamo forzatamente il GameObject per evitare l'errore "not active"
        gameObject.SetActive(true);
        enabled = true;
        StartCoroutine(GenerateList(names));
    }

    public void RegenerateFromFile() => StartCoroutine(GenerateAll());

    IEnumerator GenerateList(List<string> names)
    {
        if (llmObjectGeneration != null)
        {
            llmObjectGeneration.gameObject.SetActive(true);
            llmObjectGeneration.enabled = true;
        }

        IsGenerating = true;

        int total = names.Count;
        int done  = 0;
        OnAssetProgress?.Invoke(done, total);

        // Passata 1: solo controlli cache/LOD/Resources, tutto locale e sincrono (nessuna rete) —
        // separiamo chi è già pronto da chi ha davvero bisogno di una generazione IA.
        var needsGeneration = new List<string>();
        foreach (string name in names)
        {
            bool satisfied = false;
            yield return StartCoroutine(TryLoadFromCache(name, ok => satisfied = ok));
            if (satisfied) OnAssetProgress?.Invoke(++done, total);
            else needsGeneration.Add(name);
        }

        // Passata 2: tutto ciò che manca va generato via IA. Pipeline a 2 stadi (vedi
        // GUIDA_UNITY_INTEGRAZIONE_PIPELINE.md) invece di immagine+voxel un oggetto alla volta in
        // sequenza totale: un produttore chiama Flux/SDXL in sequenza (server singolo, non ha
        // senso parallelizzarlo) e mette le immagini pronte in coda; uno o due consumer Trellis
        // (a seconda di useSecondTrellisInstance) pescano dalla stessa coda appena liberi.
        if (needsGeneration.Count > 0)
            yield return StartCoroutine(GenerateBatchPipelined(needsGeneration, () => OnAssetProgress?.Invoke(++done, total)));

        IsGenerating = false;

        // SBLOCCO GATING: il bioma è completo in RAM, ora la mappa può finalmente iniziare a sfumare.
        OnBiomeAssetsReady?.Invoke();
        OnAllAssetsComplete?.Invoke();
    }

    // Controlli cache/LOD/Resources per un singolo nome — nessuna chiamata di rete. Invoca
    // onResult(true) se l'asset è già pronto (e in tal caso applica già ReplacePlaceholders),
    // onResult(false) se serve generarlo via IA.
    IEnumerator TryLoadFromCache(string name, Action<bool> onResult)
    {
        // 1. Controlliamo se l'asset è in cache
        bool inRuntimeCache = VoxelRenderer.RuntimeMeshCache.ContainsKey(name);

        // 2. Controlliamo se esiste il file voxjson (in persistentDataPath o in Resources)
        string persistentVoxJson = Path.Combine(PathSettings.GetObjectsFolder(), name + ".voxjson");
        string jsonContent = "";
        bool jsonExists = false;

        if (File.Exists(persistentVoxJson))
        {
            jsonContent = File.ReadAllText(persistentVoxJson);
            jsonExists = true;
        }
        else
        {
            var textAsset = Resources.Load<TextAsset>($"Baseline/objects/{name}");
            if (textAsset != null)
            {
                jsonContent = textAsset.text;
                jsonExists = true;
            }
        }

        if ((jsonExists || inRuntimeCache) && worldManager != null)
        {
            if (!inRuntimeCache && !string.IsNullOrEmpty(jsonContent))
            {
                // GetComponentInChildren può restituire null (stesso motivo per cui EnsureMeshCached
                // ha un fallback a FindFirstObjectByType) — con `?.` il caricamento veniva saltato in
                // silenzio ma ReplacePlaceholders scattava comunque, dichiarando successo a vuoto.
                var renderer = worldManager.GetComponentInChildren<VoxelRenderer>();
                if (renderer == null) renderer = FindFirstObjectByType<VoxelRenderer>();

                if (renderer != null)
                {
                    Debug.Log($"[GenerateAsset] File voxjson per '{name}' trovato. Compilazione mesh a runtime...");
                    renderer.LoadFromJson(jsonContent, name);
                }
                else
                {
                    Debug.LogError($"[GenerateAsset] '{name}': voxjson trovato su disco ma nessun VoxelRenderer in scena, impossibile compilare la mesh.");
                    onResult(false);
                    yield break;
                }
            }
            else
            {
                Debug.Log($"[GenerateAsset] Asset '{name}' già presente in cache.");
            }
            worldManager.ReplacePlaceholders(name);
            onResult(true);
            yield break;
        }

        // 2b. Fallback per gli asset di cui esistono solo le varianti LOD (_low/_medium/_high, più
        // _superhigh per big/huge) ma non il voxjson base — sia gli oggetti generati prima che
        // VoxelRenderer iniziasse a salvare anche quello
        // (persistentDataPath/Assets/Json/objects in Editor), sia gli oggetti della baseline di
        // build: BuildAssetPacker impacchetta in Resources/Baseline/objects esattamente gli stessi
        // file voxjson presenti in Assets/Json/objects al momento dell'export, quindi senza questo
        // fallback la build non riconoscerebbe MAI questi oggetti come già pronti (il controllo al
        // punto 2 cerca solo "Baseline/objects/{name}", senza suffisso) e li rigenererebbe tutti via
        // IA al primo avvio. Se troviamo almeno una tier (su disco o nelle Resources), l'oggetto è
        // comunque già stato generato — le carichiamo direttamente invece di ripartire dalla
        // pipeline IA.
        if (!jsonExists && !inRuntimeCache && worldManager != null)
        {
            string objectsFolder = PathSettings.GetObjectsFolder();
            bool foundAnyLOD = false;
            var voxelRenderer = worldManager.GetComponentInChildren<VoxelRenderer>();
            if (voxelRenderer == null) voxelRenderer = FindFirstObjectByType<VoxelRenderer>();

            foreach (string tier in new[] { "_superhigh", "_high", "_medium", "_low" })
            {
                string tierName = name + tier;
                string diskPath = Path.Combine(objectsFolder, tierName + ".voxjson");
                string tierJson = null;

                if (File.Exists(diskPath))
                {
                    tierJson = File.ReadAllText(diskPath);
                }
                else
                {
                    var textAsset = Resources.Load<TextAsset>($"Baseline/objects/{tierName}");
                    if (textAsset != null) tierJson = textAsset.text;
                }

                // foundAnyLOD va a true solo se abbiamo anche potuto CARICARLA (voxelRenderer
                // trovato) — prima bastava che il file esistesse, e con `?.` un voxelRenderer null
                // faceva dichiarare "trovato" un LOD mai davvero caricato in RuntimeMeshCache.
                if (!string.IsNullOrEmpty(tierJson) && voxelRenderer != null)
                {
                    foundAnyLOD = true;
                    voxelRenderer.LoadFromJson(tierJson, tierName);
                }
            }

            if (foundAnyLOD)
            {
                Debug.Log($"[GenerateAsset] Solo LOD trovati per '{name}' (nessun voxjson base). Compilazione mesh dai LOD esistenti...");
                worldManager.ReplacePlaceholders(name);
                onResult(true);
                yield break;
            }
        }

        onResult(false);
    }

    // --- Pipeline batch a 2 stadi: 1 produttore immagini (Flux/SDXL) + 1-2 consumer voxel (Trellis) ---
    // Vedi GUIDA_UNITY_INTEGRAZIONE_PIPELINE.md. Le coroutine Unity girano cooperativamente sul
    // main thread, quindi Queue<T>/contatori condivisi qui sono sicuri senza lock — non c'è vero
    // multithreading in gioco, solo interleaving cooperativo.

    IEnumerator GenerateBatchPipelined(List<string> names, Action onItemDone)
    {
        var pendingPrompts = new Queue<string>(names);
        var readyImages = new Queue<(string name, byte[] bytes)>();
        int itemsRemaining = names.Count;

        string primaryEndpoint = GameSettings.Instance != null
            ? GameSettings.Instance.Gen3DEndpoint
            : "http://25.18.55.60:7960/generate_vox_only";

        var consumers = new List<Coroutine>
        {
            StartCoroutine(TrellisConsumer(primaryEndpoint, readyImages,
                () => itemsRemaining, () => { itemsRemaining--; onItemDone?.Invoke(); }))
        };

        if (useSecondTrellisInstance)
        {
            string secondaryEndpoint = GameSettings.Instance != null
                ? GameSettings.Instance.Gen3DEndpoint2
                : "http://25.18.55.60:7961/generate_vox_only";
            consumers.Add(StartCoroutine(TrellisConsumer(secondaryEndpoint, readyImages,
                () => itemsRemaining, () => { itemsRemaining--; onItemDone?.Invoke(); })));
        }

        Coroutine producer = StartCoroutine(ImageProducer(pendingPrompts, readyImages,
            () => { itemsRemaining--; onItemDone?.Invoke(); }));

        yield return producer;
        foreach (var c in consumers) yield return c;
    }

    // Chiama Flux/SDXL (/create_image) in sequenza, un'immagine alla volta — un solo server, non
    // ha senso parallelizzarlo (vedi GUIDA_UNITY_INTEGRAZIONE_PIPELINE.md) — e accoda i risultati
    // per i consumer Trellis. Su fallimento decrementa onFailed invece di lasciare per sempre in
    // attesa i consumer di quell'item.
    IEnumerator ImageProducer(Queue<string> pendingPrompts, Queue<(string name, byte[] bytes)> readyImages, Action onFailed)
    {
        string server = GameSettings.Instance != null
            ? GameSettings.Instance.ImageServerBase
            : (llmObjectGeneration != null ? llmObjectGeneration.serverAddress : "http://25.18.55.60:5000");
        string negativePrompt = llmObjectGeneration != null ? llmObjectGeneration.negativePrompt : "";
        int imageSeed = llmObjectGeneration != null ? llmObjectGeneration.imageSeed : 46;

        while (pendingPrompts.Count > 0)
        {
            string name = pendingPrompts.Dequeue();
            string prompt = LLMObjectGeneration.BuildStylePrompt(name);
            string json = $"{{\"prompt\": \"{EscapeJsonForBatch(prompt)}\", \"quality\": \"high\", \"negative_prompt\": \"{EscapeJsonForBatch(negativePrompt)}\", \"seed\": {imageSeed}}}";

            using var req = new UnityWebRequest($"{server}/create_image", "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = 120;

            Debug.Log($"[GenerateAsset] Produttore immagini: richiesta '{name}'...");
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success && req.downloadHandler.data != null && req.downloadHandler.data.Length > 0)
            {
                readyImages.Enqueue((name, req.downloadHandler.data));
            }
            else
            {
                Debug.LogError($"[GenerateAsset] Produttore immagini: '{name}' fallito — {req.responseCode} {req.error}");
                onFailed?.Invoke();
            }
        }
    }

    // Un worker Trellis: pesca dalla coda condivisa appena un'immagine è pronta, genera il voxel,
    // carica la mesh e sblocca il placeholder. Ritenta sul 503 "Server is busy" rimettendo l'item
    // in coda (un altro worker libero potrebbe prenderlo prima) invece di trattarlo come fallimento.
    IEnumerator TrellisConsumer(string trellisEndpoint, Queue<(string name, byte[] bytes)> readyImages,
                                 Func<int> remainingCount, Action onItemProcessed)
    {
        while (remainingCount() > 0)
        {
            if (readyImages.Count == 0) { yield return null; continue; }
            var item = readyImages.Dequeue();

            var voxReq = new VoxOnlyRequest
            {
                image_base64 = Convert.ToBase64String(item.bytes),
                output_dir   = Path.Combine("Assets", "Json", "objects"),
                output_name  = item.name,
            };
            var bodyBytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(voxReq));

            int attempt = 0;
            while (true)
            {
                using var req = new UnityWebRequest(trellisEndpoint, "POST");
                req.uploadHandler = new UploadHandlerRaw(bodyBytes);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = 180;

                Debug.Log($"[GenerateAsset] Trellis[{trellisEndpoint}]: generazione '{item.name}'...");
                yield return req.SendWebRequest();

                if (req.responseCode == 503)
                {
                    attempt++;
                    if (attempt > trellisBusyMaxRetries)
                    {
                        Debug.LogError($"[GenerateAsset] Trellis[{trellisEndpoint}]: '{item.name}' ancora occupato dopo {trellisBusyMaxRetries} tentativi, rinuncio.");
                        break;
                    }
                    yield return new WaitForSeconds(trellisBusyRetryDelaySeconds);
                    continue;
                }

                if (req.result == UnityWebRequest.Result.Success)
                {
                    // BUG trovato in test: GetComponentInChildren può restituire null (stesso motivo
                    // per cui EnsureMeshCached ha già un fallback a FindFirstObjectByType) — con `?.`
                    // il LoadFromJson veniva saltato IN SILENZIO, ma ReplacePlaceholders scattava
                    // comunque subito dopo, loggando "pronto" e distruggendo i placeholder in attesa
                    // senza che la mesh fosse mai stata caricata: falso positivo totale, nessun errore,
                    // nessun file su disco, oggetto sparito per sempre. Ora falliamo rumorosamente
                    // invece di dichiarare successo a vuoto.
                    var renderer = worldManager != null ? worldManager.GetComponentInChildren<VoxelRenderer>() : null;
                    if (renderer == null && worldManager != null) renderer = FindFirstObjectByType<VoxelRenderer>();

                    if (renderer != null)
                    {
                        renderer.LoadFromJson(req.downloadHandler.text, item.name);
                        worldManager?.ReplacePlaceholders(item.name);
                    }
                    else
                    {
                        Debug.LogError($"[GenerateAsset] Trellis[{trellisEndpoint}]: '{item.name}' — voxjson ricevuto ma nessun VoxelRenderer trovato in scena, impossibile caricare la mesh. Resta placeholder.");
                    }
                }
                else
                {
                    Debug.LogError($"[GenerateAsset] Trellis[{trellisEndpoint}]: '{item.name}' fallito — {req.responseCode} {req.error}");
                }
                break;
            }

            onItemProcessed?.Invoke();
        }
    }

    static string EscapeJsonForBatch(string s) =>
        string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");

    private string CurrentObjectsListPath => Path.Combine(PathSettings.GetJsonFolder(), "current_objects.json");

    IEnumerator GenerateAll()
    {
        if (llmObjectGeneration == null) yield break;
        var entries = JObject.Parse(File.ReadAllText(CurrentObjectsListPath));
        var names   = new List<string>();
        foreach (var entry in entries) names.Add(entry.Key);
        yield return StartCoroutine(GenerateList(names));
    }

    public void RegenerateAllFromRegistry()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[GenerateAsset] Avvia il gioco (Play Mode) prima di rigenerare gli asset via IA!");
            return;
        }
        StartCoroutine(RegenerateAllFromRegistryRoutine());
    }

    IEnumerator RegenerateAllFromRegistryRoutine()
    {
        string registryPath = Path.Combine(PathSettings.GetJsonFolder(), "all_objects.json");
        if (!File.Exists(registryPath))
        {
            Debug.LogError($"[GenerateAsset] File di registro non trovato in: {registryPath}");
            yield break;
        }

        List<string> names = new List<string>();
        try
        {
            var entries = JObject.Parse(File.ReadAllText(registryPath));
            foreach (var entry in entries)
            {
                names.Add(entry.Key);
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[GenerateAsset] Errore nel parsing di all_objects.json: {ex.Message}");
            yield break;
        }

        if (names.Count == 0)
        {
            Debug.LogWarning("[GenerateAsset] La lista di all_objects.json è vuota!");
            yield break;
        }

        Debug.Log($"[GenerateAsset] Inizio rigenerazione IA forzata per tutti i {names.Count} oggetti del registro globale.");
        
        // Forziamo la rigenerazione eliminando i prefabbricati e la cache delle mesh runtime
        foreach (string name in names)
        {
            string prefabDiskPath = Path.Combine(Application.dataPath, "GeneratedAsset", name + ".prefab");
            if (File.Exists(prefabDiskPath))
            {
                File.Delete(prefabDiskPath);
                string metaPath = prefabDiskPath + ".meta";
                if (File.Exists(metaPath)) File.Delete(metaPath);
            }
            
            string medPrefabPath = Path.Combine(Application.dataPath, "GeneratedAsset", name + "_medium.prefab");
            if (File.Exists(medPrefabPath)) { File.Delete(medPrefabPath); File.Delete(medPrefabPath + ".meta"); }
            string lowPrefabPath = Path.Combine(Application.dataPath, "GeneratedAsset", name + "_low.prefab");
            if (File.Exists(lowPrefabPath)) { File.Delete(lowPrefabPath); File.Delete(lowPrefabPath + ".meta"); }
            string highPrefabPath = Path.Combine(Application.dataPath, "GeneratedAsset", name + "_high.prefab");
            if (File.Exists(highPrefabPath)) { File.Delete(highPrefabPath); File.Delete(highPrefabPath + ".meta"); }
            string superHighPrefabPath = Path.Combine(Application.dataPath, "GeneratedAsset", name + "_superhigh.prefab");
            if (File.Exists(superHighPrefabPath)) { File.Delete(superHighPrefabPath); File.Delete(superHighPrefabPath + ".meta"); }

            // Elimina anche i voxjson dei livelli LOD (non il modello base): finché esistono su
            // disco, VoxelRenderer li considera già generati e salta la ricostruzione, quindi i
            // prefab LOD appena cancellati sopra non verrebbero mai ricreati.
            string objectsFolder = PathSettings.GetObjectsFolder();
            string lowVoxJsonPath = Path.Combine(objectsFolder, name + "_low.voxjson");
            if (File.Exists(lowVoxJsonPath)) File.Delete(lowVoxJsonPath);
            string medVoxJsonPath = Path.Combine(objectsFolder, name + "_medium.voxjson");
            if (File.Exists(medVoxJsonPath)) File.Delete(medVoxJsonPath);
            string highVoxJsonPath = Path.Combine(objectsFolder, name + "_high.voxjson");
            if (File.Exists(highVoxJsonPath)) File.Delete(highVoxJsonPath);
            string superHighVoxJsonPath = Path.Combine(objectsFolder, name + "_superhigh.voxjson");
            if (File.Exists(superHighVoxJsonPath)) File.Delete(superHighVoxJsonPath);

            VoxelRenderer.RuntimeMeshCache.Remove(name);
            VoxelRenderer.RuntimeMeshCache.Remove(name + "_low");
            VoxelRenderer.RuntimeMeshCache.Remove(name + "_medium");
            VoxelRenderer.RuntimeMeshCache.Remove(name + "_high");
            VoxelRenderer.RuntimeMeshCache.Remove(name + "_superhigh");
        }

#if UNITY_EDITOR
        UnityEditor.AssetDatabase.Refresh();
#endif

        yield return StartCoroutine(GenerateList(names));
    }
}

#if UNITY_EDITOR
[UnityEditor.CustomEditor(typeof(GenerateAsset))]
public class GenerateAssetEditor : UnityEditor.Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        GenerateAsset genAsset = (GenerateAsset)target;

        GUILayout.Space(15);
        GUI.backgroundColor = new Color(0.3f, 0.8f, 0.4f);
        if (GUILayout.Button("Regenerate All Objects from all_objects.json", GUILayout.Height(30)))
        {
            genAsset.RegenerateAllFromRegistry();
        }
        GUI.backgroundColor = Color.white;
    }
}
#endif