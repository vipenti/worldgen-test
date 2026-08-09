using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Newtonsoft.Json.Linq;

public class GenerateAsset : MonoBehaviour
{
    [Header("Input")]
    public string listPath    = "Assets/Json/current_objects.json";
    public string assetFolder = "Assets/GeneratedAsset";

    [Header("References")]
    public LLMObjectGeneration llmObjectGeneration;
    public WorldManager worldManager;

    public static bool              IsGenerating       { get; private set; }
    public static event Action         OnAllAssetsComplete;
    public static event Action<int, int> OnAssetProgress;
    public static event Action         OnCriticalAssetsReady;

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
        ClassifyAssets(names, out List<string> queueA, out List<string> queueB);

        int total = names.Count;
        int done  = 0;
        OnAssetProgress?.Invoke(done, total);

        // FASE 1: Generazione in background silente e forzata degli elementi della Coda A (Macro-Asset)
        if (queueA.Count > 0)
        {
            foreach (string name in queueA)
            {
                yield return StartCoroutine(ProcessSingleAsset(name));
                OnAssetProgress?.Invoke(++done, total);
            }
        }

        // SBLOCCO GATING: I guardiani del bioma sono pronti in RAM, ora la mappa può finalmente iniziare a sfumare!
        OnCriticalAssetsReady?.Invoke();

        // FASE 2: Streaming di sfondo invisibile degli elementi minori (Coda B)
        if (queueB.Count > 0)
        {
            foreach (string name in queueB)
            {
                yield return StartCoroutine(ProcessSingleAsset(name));
                OnAssetProgress?.Invoke(++done, total);
            }
        }

        IsGenerating = false;
        OnAllAssetsComplete?.Invoke();
    }

    IEnumerator ProcessSingleAsset(string name)
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
                Debug.Log($"[GenerateAsset] File voxjson per '{name}' trovato. Compilazione mesh a runtime...");
                worldManager.GetComponentInChildren<VoxelRenderer>()?.LoadFromJson(jsonContent, name);
            }
            else
            {
                Debug.Log($"[GenerateAsset] Asset '{name}' già presente in cache.");
            }
            worldManager.ReplacePlaceholders(name);
            yield break;
        }

        // 2b. Fallback per gli asset di cui esistono solo le tre varianti LOD ma non il voxjson
        // base — sia gli oggetti generati prima che VoxelRenderer iniziasse a salvare anche quello
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

            foreach (string tier in new[] { "_high", "_medium", "_low" })
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

                if (!string.IsNullOrEmpty(tierJson))
                {
                    foundAnyLOD = true;
                    voxelRenderer?.LoadFromJson(tierJson, tierName);
                }
            }

            if (foundAnyLOD)
            {
                Debug.Log($"[GenerateAsset] Solo LOD trovati per '{name}' (nessun voxjson base). Compilazione mesh dai LOD esistenti...");
                worldManager.ReplacePlaceholders(name);
                yield break;
            }
        }

        // 3. Solo se non c'è traccia dell'asset, avviamo la pipeline IA
        if (llmObjectGeneration != null)
        {
            Debug.Log($"[GenerateAsset] Asset '{name}' non trovato. Avvio generazione IA...");
            llmObjectGeneration.object_name = name;
            llmObjectGeneration.StartObjectGeneration();
            yield return new WaitUntil(() => !llmObjectGeneration.IsGenerating);
            worldManager?.ReplacePlaceholders(name);
        }
    }

    private string CurrentObjectsListPath => Path.Combine(PathSettings.GetJsonFolder(), "current_objects.json");

    void ClassifyAssets(List<string> names, out List<string> queueA, out List<string> queueB)
    {
        queueA = new List<string>(); queueB = new List<string>();
        string listFilePath = CurrentObjectsListPath;
        if (!File.Exists(listFilePath)) { queueB.AddRange(names); return; }

        try
        {
            var root = JObject.Parse(File.ReadAllText(listFilePath));
            foreach (string name in names)
            {
                if (root[name] != null && root[name].Type == JTokenType.Object)
                {
                    string size = root[name]["size"]?.ToString() ?? "big";
                    string freq = root[name]["frequency"]?.ToString() ?? "common";
                    bool isLarge = (size == "huge" || size == "big");
                    bool isCommon = (freq == "very_common" || freq == "common");

                    if (isLarge && isCommon) queueA.Add(name);
                    else queueB.Add(name);
                }
                else queueB.Add(name);
            }
        }
        catch (Exception) { queueB.AddRange(names); }
    }

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

            VoxelRenderer.RuntimeMeshCache.Remove(name);
            VoxelRenderer.RuntimeMeshCache.Remove(name + "_low");
            VoxelRenderer.RuntimeMeshCache.Remove(name + "_medium");
            VoxelRenderer.RuntimeMeshCache.Remove(name + "_high");
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