using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Newtonsoft.Json.Linq;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Tool one-off (solo Editor) per ricostruire in scena, dal vivo in Play Mode, l'intero corridoio
/// esplorato durante una sessione registrata (VRPathRecorder + BiomeSnapshotRecorder) e salvarlo
/// come prefab statico. A differenza di WorldSnapshotLoader (che riproduce solo i punti di cambio
/// bioma e lascia che i chunk lontani vengano distrutti come in una sessione normale), questo:
///   1. Cammina lungo TUTTO il percorso registrato (non solo i 3 punti di switch), applicando i
///      cambi di bioma nell'ordine e nella posizione giusti lungo il tragitto.
///   2. Tiene in vita OGNI chunk generato (WorldManager.disableChunkRemovalForBake) invece di
///      lasciarli sparire uno alla volta man mano che ci si allontana.
///   3. A fine cammino, "cristallizza" tutte le mesh runtime (chunk + decorazioni, deduplicate per
///      riferimento) come asset .mesh persistenti, ricollega i materiali alle texture reali del
///      progetto invece delle copie runtime, e salva l'intera gerarchia come un unico prefab.
///
/// Uso: aggiungere questo componente a un GameObject in scena (in Play Mode), assegnare
/// worldManager/player, i due path assoluti (pathRecordingFile, sessionSnapshotFolder), poi
/// chiamare StartBake(). Progresso leggibile da Status/WaypointsDone/WaypointsTotal/IsDone.
/// </summary>
public class WorldCorridorBaker : MonoBehaviour
{
    public WorldManager worldManager;
    public Transform player;

    [Tooltip("Path assoluto al file vr_path_*.jsonl (VRPathRecorder) da usare come tragitto.")]
    public string pathRecordingFile = "";
    [Tooltip("Path assoluto alla cartella session_* (BiomeSnapshotRecorder) con i biome_000N_*.json.")]
    public string sessionSnapshotFolder = "";
    [Tooltip("Path del prefab risultante, relativo alla root del progetto (deve iniziare con Assets/).")]
    public string outputPrefabPath = "Assets/BakedWorlds/CorridorBake.prefab";

    [Tooltip("Distanza (world units) tra un waypoint e il successivo lungo il percorso — deve restare ben sotto viewDistance*chunkWidth per non lasciare buchi di copertura tra un waypoint e il successivo.")]
    public float waypointStepDistance = 24f;
    [Tooltip("Frame di attesa dopo ogni waypoint di solo movimento (di norma 1 basta: le code di WorldManager vengono forzate a drenare tutte nello stesso frame durante il bake).")]
    public int framesPerWaypoint = 1;
    [Tooltip("Frame di attesa extra dopo ogni switch di bioma, per lasciare che la nuova config si propaghi prima di riprendere a camminare.")]
    public int framesAfterBiomeSwitch = 20;

    public string Status { get; private set; } = "idle";
    public bool IsDone { get; private set; }
    public bool Failed { get; private set; }
    public int WaypointsTotal { get; private set; }
    public int WaypointsDone { get; private set; }

    struct Waypoint { public Vector3 pos; public int biomeIndexToApply; } // 0 = solo movimento, nessuno switch

    void Awake()
    {
        if (worldManager == null) worldManager = FindAnyObjectByType<WorldManager>();
        if (player == null && worldManager != null) player = worldManager.player;
    }

    public void StartBake() => StartCoroutine(BakeRoutine());

    IEnumerator BakeRoutine()
    {
#if UNITY_EDITOR
        if (worldManager == null || player == null)
        {
            Status = "ERRORE: worldManager o player mancante";
            Failed = true; IsDone = true; yield break;
        }

        Status = "loading path";
        List<Vector3> pathSamples = LoadPathSamples(pathRecordingFile);
        if (pathSamples.Count == 0)
        {
            Status = $"ERRORE: nessun campione leggibile in {pathRecordingFile}";
            Failed = true; IsDone = true; yield break;
        }

        List<(int index, Vector3 pos)> events = LoadBiomeEvents(sessionSnapshotFolder);

        // Per ogni evento bioma, trova il campione del path più vicino — serve per sapere IN CHE
        // PUNTO della sequenza di waypoint va inserito lo switch (i file non condividono un
        // timestamp comune, solo posizioni).
        var eventAtSampleIndex = new Dictionary<int, int>();
        foreach (var (index, pos) in events)
        {
            int best = 0; float bestDist = float.MaxValue;
            for (int i = 0; i < pathSamples.Count; i++)
            {
                float d = (pathSamples[i] - pos).sqrMagnitude;
                if (d < bestDist) { bestDist = d; best = i; }
            }
            eventAtSampleIndex[best] = index;
        }

        var eventSampleIndicesSorted = new List<int>(eventAtSampleIndex.Keys);
        eventSampleIndicesSorted.Sort();

        var waypoints = new List<Waypoint>();
        Vector3 lastAdded = pathSamples[0];
        int eventCursor = 0;
        for (int i = 0; i < pathSamples.Count; i++)
        {
            bool isEventHere = eventCursor < eventSampleIndicesSorted.Count && i == eventSampleIndicesSorted[eventCursor];
            bool farEnough = (pathSamples[i] - lastAdded).sqrMagnitude >= waypointStepDistance * waypointStepDistance;

            if (isEventHere)
            {
                waypoints.Add(new Waypoint { pos = pathSamples[i], biomeIndexToApply = eventAtSampleIndex[i] });
                lastAdded = pathSamples[i];
                eventCursor++;
            }
            else if (farEnough)
            {
                waypoints.Add(new Waypoint { pos = pathSamples[i], biomeIndexToApply = 0 });
                lastAdded = pathSamples[i];
            }
        }
        Vector3 tailPos = pathSamples[pathSamples.Count - 1];
        if (waypoints.Count == 0 || (waypoints[waypoints.Count - 1].pos - tailPos).sqrMagnitude > 1f)
            waypoints.Add(new Waypoint { pos = tailPos, biomeIndexToApply = 0 });

        WaypointsTotal = waypoints.Count;
        Debug.Log($"[WorldCorridorBaker] {pathSamples.Count} campioni path -> {waypoints.Count} waypoint, {events.Count} switch bioma.");

        int origMaxChunks = worldManager.maxChunksCreatedPerFrame;
        int origMaxColliders = worldManager.maxCollidersBakedPerFrame;
        bool origDisableRemoval = worldManager.disableChunkRemovalForBake;
        worldManager.maxChunksCreatedPerFrame = 100000;
        worldManager.maxCollidersBakedPerFrame = 100000;
        worldManager.disableChunkRemovalForBake = true;

        string jsonFolder = PathSettings.GetJsonFolder();
        string liveLlm = Path.Combine(jsonFolder, "llm_generated.json");
        string liveObjects = Path.Combine(jsonFolder, "current_objects.json");
        string backupLlm = liveLlm + ".bakebackup";
        string backupObjects = liveObjects + ".bakebackup";
        if (File.Exists(liveLlm)) File.Copy(liveLlm, backupLlm, true);
        if (File.Exists(liveObjects)) File.Copy(liveObjects, backupObjects, true);

        var controller = player.GetComponent<CharacterController>();

        Status = "walking corridor";
        for (int w = 0; w < waypoints.Count; w++)
        {
            var wp = waypoints[w];

            if (wp.biomeIndexToApply > 0)
            {
                string prefix = $"biome_{wp.biomeIndexToApply:D4}";
                string srcLlm = Path.Combine(sessionSnapshotFolder, prefix + "_llm_generated.json");
                string srcObjects = Path.Combine(sessionSnapshotFolder, prefix + "_current_objects.json");
                MovePlayer(controller, wp.pos);

                if (File.Exists(srcLlm) && File.Exists(srcObjects))
                {
                    File.Copy(srcLlm, liveLlm, true);
                    File.Copy(srcObjects, liveObjects, true);
                    worldManager.ApplyNewConfig();
                    Debug.Log($"[WorldCorridorBaker] Bioma #{wp.biomeIndexToApply} applicato al waypoint {w}/{waypoints.Count}.");
                    for (int f = 0; f < framesAfterBiomeSwitch; f++) yield return null;
                }
                else
                {
                    Debug.LogWarning($"[WorldCorridorBaker] Snapshot bioma #{wp.biomeIndexToApply} incompleto in {sessionSnapshotFolder}, switch saltato.");
                    yield return null;
                }
            }
            else
            {
                MovePlayer(controller, wp.pos);
                for (int f = 0; f < framesPerWaypoint; f++) yield return null;
            }

            WaypointsDone = w + 1;
        }

        for (int f = 0; f < 10; f++) yield return null; // margine per lasciare assestare l'ultimo tratto

        Status = "baking prefab";
        yield return null;

        try
        {
            BakePrefab();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[WorldCorridorBaker] Eccezione durante il bake del prefab: {ex}");
            Status = $"ERRORE: {ex.Message}";
            Failed = true;
        }

        worldManager.maxChunksCreatedPerFrame = origMaxChunks;
        worldManager.maxCollidersBakedPerFrame = origMaxColliders;
        worldManager.disableChunkRemovalForBake = origDisableRemoval;
        if (File.Exists(backupLlm)) { File.Copy(backupLlm, liveLlm, true); File.Delete(backupLlm); }
        if (File.Exists(backupObjects)) { File.Copy(backupObjects, liveObjects, true); File.Delete(backupObjects); }

        if (!Failed)
        {
            Status = "done";
            Debug.Log($"[WorldCorridorBaker] COMPLETATO. Prefab salvato in {outputPrefabPath}.");
        }
        IsDone = true;
#else
        yield break;
#endif
    }

    void MovePlayer(CharacterController controller, Vector3 pos)
    {
        if (controller != null) controller.enabled = false;
        player.position = pos;
        if (controller != null) controller.enabled = true;
    }

    // Bug noto in build su VRPathRecorder/BiomeSnapshotRecorder: il campo yaw viene scritto come
    // "yaw":F2 invece del numero formattato (probabilmente IL2CPP che non applica lo specificatore
    // di formato ":F2" su ToString/string.Format) — rende l'intera riga JSON non valida. Qui non ci
    // serve comunque lo yaw (solo la posizione, per generare i chunk), quindi normalizziamo il
    // valore spurio a 0 prima del parsing invece di scartare la riga intera.
    static string SanitizeYawBug(string line) => line.Replace("\"yaw\":F2}", "\"yaw\":0}");

    static List<Vector3> LoadPathSamples(string path)
    {
        var list = new List<Vector3>();
        if (!File.Exists(path)) return list;
        foreach (string rawLine in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;
            try
            {
                var o = JObject.Parse(SanitizeYawBug(rawLine));
                float x = o["x"]?.Value<float>() ?? 0f;
                float y = o["y"]?.Value<float>() ?? 0f;
                float z = o["z"]?.Value<float>() ?? 0f;
                list.Add(new Vector3(x, y, z));
            }
            catch { /* riga comunque non valida, saltata */ }
        }
        return list;
    }

    static List<(int index, Vector3 pos)> LoadBiomeEvents(string sessionFolder)
    {
        var list = new List<(int, Vector3)>();
        string eventsPath = Path.Combine(sessionFolder, "biome_events.jsonl");
        if (!File.Exists(eventsPath)) return list;
        foreach (string rawLine in File.ReadAllLines(eventsPath))
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;
            try
            {
                var o = JObject.Parse(SanitizeYawBug(rawLine));
                int index = o["index"]?.Value<int>() ?? (list.Count + 1);
                float x = o["x"]?.Value<float>() ?? 0f;
                float y = o["y"]?.Value<float>() ?? 0f;
                float z = o["z"]?.Value<float>() ?? 0f;
                list.Add((index, new Vector3(x, y, z)));
            }
            catch { }
        }
        return list;
    }

#if UNITY_EDITOR
    void BakePrefab()
    {
        var chunks = FindObjectsByType<Chunk>(FindObjectsSortMode.None);
        Debug.Log($"[WorldCorridorBaker] {chunks.Length} chunk trovati in scena da impacchettare.");
        if (chunks.Length == 0) throw new Exception("Nessun chunk in scena: il cammino non ha generato nulla.");

        string folder = Path.GetDirectoryName(outputPrefabPath).Replace('\\', '/');
        if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
        string matFolder = folder + "/Materials";
        if (!Directory.Exists(matFolder)) Directory.CreateDirectory(matFolder);

        string meshAssetPath = folder + "/" + Path.GetFileNameWithoutExtension(outputPrefabPath) + "_Meshes.asset";
        var meshContainer = ScriptableObject.CreateInstance<ScriptableObject>();
        AssetDatabase.CreateAsset(meshContainer, meshAssetPath);

        // runtime mesh -> mesh salvata come sub-asset (dedup per riferimento: le mesh delle
        // decorazioni sono già condivise via VoxelRenderer.RuntimeMeshCache tra tutte le istanze
        // dello stesso oggetto, quindi qui si salvano una volta sola indipendentemente da quante
        // copie ne esistono in scena).
        var savedMeshes = new Dictionary<Mesh, Mesh>();
        Mesh SaveMeshOnce(Mesh m, string name)
        {
            if (m == null) return null;
            if (savedMeshes.TryGetValue(m, out Mesh already)) return already;
            Mesh clone = Instantiate(m);
            clone.name = name;
            AssetDatabase.AddObjectToAsset(clone, meshAssetPath);
            savedMeshes[m] = clone;
            return clone;
        }

        var albedo = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/GeneratedTexture/TextureAtlas.png");
        var normal = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/GeneratedTexture/TextureAtlas_Normal.png");

        var root = new GameObject($"BakedWorld_{DateTime.Now:yyyyMMdd_HHmmss}");

        // Materiale unico dei chunk: sharedChunkMaterial è sempre una copia runtime
        // (new Material(...) in WorldManager, mai un asset) — lo ricreiamo come asset persistente
        // puntando alle texture REALI del progetto invece delle Texture2D caricate a runtime da
        // PNG grezzo (che sparirebbero uscendo da Play Mode).
        Material bakedChunkMaterial = null;

        foreach (var chunk in chunks)
        {
            // Forza tutto visibile e con collisione, indipendentemente da come il frustum culling
            // lo aveva lasciato durante il cammino (solo i chunk vicini all'ULTIMA posizione del
            // player restano "in vista" per costruzione — qui vogliamo l'intero corridoio visibile).
            chunk.gameObject.SetActive(true);

            var mf = chunk.GetComponent<MeshFilter>();
            var mr = chunk.GetComponent<MeshRenderer>();
            var mc = chunk.GetComponent<MeshCollider>();

            Mesh saved = null;
            if (mf != null && mf.sharedMesh != null)
            {
                saved = SaveMeshOnce(mf.sharedMesh, $"chunk_{chunk.ChunkCoord.x}_{chunk.ChunkCoord.y}");
                mf.sharedMesh = saved;
            }
            if (mc != null && saved != null)
            {
                mc.sharedMesh = saved; // sempre coerente col terreno, anche per i chunk mai stati "vicini" durante il cammino (altrimenti niente collisione lì)
                mc.enabled = true;
            }

            if (mr != null)
            {
                if (bakedChunkMaterial == null)
                {
                    bakedChunkMaterial = new Material(mr.sharedMaterial);
                    if (albedo != null) bakedChunkMaterial.SetTexture("_BaseMap", albedo);
                    if (normal != null) bakedChunkMaterial.SetTexture("_BumpMap", normal);
                    AssetDatabase.CreateAsset(bakedChunkMaterial, matFolder + "/ChunkMaterial.mat");
                }
                mr.sharedMaterial = bakedChunkMaterial;
            }

            chunk.transform.SetParent(root.transform, true);
        }

        // Decorazioni: figlie dirette del loro chunk (già riparentate sotto root sopra), quindi
        // basta scandire tutti i MeshFilter di root e scartare quelli già gestiti come terreno.
        Material bakedDecorMaterial = null;
        var decorMeshFilters = root.GetComponentsInChildren<MeshFilter>(true);
        foreach (var mf in decorMeshFilters)
        {
            if (mf.GetComponent<Chunk>() != null) continue;
            if (mf.sharedMesh == null) continue;

            mf.sharedMesh = SaveMeshOnce(mf.sharedMesh, mf.sharedMesh.name);

            var mc2 = mf.GetComponent<MeshCollider>();
            if (mc2 != null && mc2.sharedMesh != null)
                mc2.sharedMesh = savedMeshes.TryGetValue(mc2.sharedMesh, out Mesh cm2) ? cm2 : SaveMeshOnce(mc2.sharedMesh, mc2.sharedMesh.name + "_col");

            var mr2 = mf.GetComponent<MeshRenderer>();
            if (mr2 != null && mr2.sharedMaterial != null && !EditorUtility.IsPersistent(mr2.sharedMaterial))
            {
                if (bakedDecorMaterial == null)
                {
                    bakedDecorMaterial = new Material(mr2.sharedMaterial);
                    if (albedo != null) bakedDecorMaterial.SetTexture("_BaseMap", albedo);
                    AssetDatabase.CreateAsset(bakedDecorMaterial, matFolder + "/DecorMaterial.mat");
                }
                mr2.sharedMaterial = bakedDecorMaterial;
            }
        }

        AssetDatabase.SaveAssets();

        PrefabUtility.SaveAsPrefabAsset(root, outputPrefabPath, out bool ok);
        Debug.Log(ok
            ? $"[WorldCorridorBaker] Prefab salvato: {outputPrefabPath} ({chunks.Length} chunk, {savedMeshes.Count} mesh uniche)."
            : "[WorldCorridorBaker] ERRORE nel salvataggio del prefab.");
        if (!ok) throw new Exception("PrefabUtility.SaveAsPrefabAsset ha restituito false.");

        AssetDatabase.Refresh();
    }
#endif
}
