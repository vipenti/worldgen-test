using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class WorldManager : MonoBehaviour
{
    [Header("Riferimenti")]
    public Transform player;
    public GameObject chunkPrefab;
    public TextureAtlasManager atlasManager;

    [Header("Impostazioni Visuale")]
    public int viewDistance = 4;
    public int blendDistance = 10; // Quanti chunk dura la sfumatura tra biomi

    [Header("Impostazioni Path")]
    public float pathWidth = 10f;      // Larghezza piatta del path in blocchi
    public float pathBlendWidth = 20f; // Blocchi di raccordo verso le colline
    public int pathLevel = 8;          // Quota del path

    [Header("Layout Map")]
    public GameObject defaultObjectPrefab;  // fallback quando nessun prefab bioma è disponibile
    [Range(0f, 1f)]
    public float objectSpawnChance  = 0.2f;
    public float objectGroundOffset = -0.3f;  // offset Y applicato dopo il bounds-fit (negativo = affonda leggermente)

    [Header("Object Scales by Size")]
    public float scaleHuge    = 15f;
    public float scaleBig     =  7f;
    public float scaleSmall   =  3f;
    public float scaleTiny    =  1f;
    public float scaleDefault =  5f;

    private Dictionary<string, GameObject>                 _readyPrefabs       = new Dictionary<string, GameObject>();
    private List<string>                                   _pendingObjectNames = new List<string>();
    private Dictionary<string, List<BillboardPlaceholder>> _placeholders      = new Dictionary<string, List<BillboardPlaceholder>>();
    private Dictionary<string, float>                      _objectScaleFactors = new Dictionary<string, float>();
    private Dictionary<string, float>                      _objectFreqChances  = new Dictionary<string, float>();
    private HashSet<string>                                _uniqueSpawned      = new HashSet<string>();

    [Header("Debug Visualization")]
    public MapVisualizer mapVisualizer;

    private Dictionary<Vector2Int, Chunk> activeChunks = new Dictionary<Vector2Int, Chunk>();
    private Vector2Int lastPlayerChunk;

    // Gestione Biomi e Transizioni
    private BiomeConfig oldConfig;
    private BiomeConfig targetConfig;
    private bool isTransitioning = false;
    private Vector2Int transitionStartCoord;

    // Atlas texture condivisa tra tutti i chunk
    private Texture2D atlasTexture;
    private Texture2D atlasNormalTexture;
    private Material sharedChunkMaterial;

    void Start() => StartCoroutine(InitRoutine());

    IEnumerator InitRoutine()
    {
        player.gameObject.SetActive(false);

        var prefabRenderer = chunkPrefab.GetComponent<MeshRenderer>();
        if (prefabRenderer != null)
            sharedChunkMaterial = new Material(prefabRenderer.sharedMaterial);
        else
            Debug.LogError("[WorldManager] chunkPrefab non ha MeshRenderer — sharedChunkMaterial non creato.");

        // Se l'atlas non esiste ma ci sono materiali salvati, ricostruisce dai PNG locali
        string atlasPath = Path.Combine(Application.dataPath, "GeneratedTexture", "TextureAtlas.png");
        if (!File.Exists(atlasPath) && atlasManager != null && atlasManager.HasMaterials)
        {
            Debug.Log("<color=cyan>[WorldManager]</color> Atlas mancante — ricostruzione dai file locali...");
            atlasManager.RebuildAtlasFromFiles();
        }

        LoadAtlasTexture();
        GenerateTexture.OnGenerationComplete += _ => LoadAtlasTexture();

        targetConfig = LoadLatestConfig();
        oldConfig = targetConfig;

        mapVisualizer?.GenerateMaps(targetConfig);
        LoadBiomePrefabs();
        UpdateChunks();
        yield return StartCoroutine(SpawnPlayer());
    }

    void OnDestroy()
    {
        GenerateTexture.OnGenerationComplete -= _ => LoadAtlasTexture();
    }

    void LoadAtlasTexture()
    {
        string albedoPath = Path.Combine(Application.dataPath, "GeneratedTexture", "TextureAtlas.png");
        if (File.Exists(albedoPath))
        {
            atlasTexture ??= new Texture2D(2, 2);
            atlasTexture.LoadImage(File.ReadAllBytes(albedoPath));
            sharedChunkMaterial?.SetTexture("_BaseMap", atlasTexture);
            Debug.Log($"[WorldManager] Atlas albedo caricata. Material: {(sharedChunkMaterial != null ? sharedChunkMaterial.shader.name : "NULL")}");
        }
        else
        {
            Debug.LogWarning($"[WorldManager] TextureAtlas.png non trovata in: {albedoPath}");
        }

        // string normalPath = Path.Combine(Application.dataPath, "GeneratedTexture", "TextureAtlas_Normal.png");
        // if (File.Exists(normalPath))
        // {
        //     atlasNormalTexture ??= new Texture2D(2, 2, TextureFormat.RGBA32, true);
        //     atlasNormalTexture.LoadImage(File.ReadAllBytes(normalPath));
        //     if (sharedChunkMaterial != null)
        //     {
        //         sharedChunkMaterial.SetTexture("_BumpMap", atlasNormalTexture);
        //         sharedChunkMaterial.EnableKeyword("_NORMALMAP");
        //     }
        // }
    }

    IEnumerator SpawnPlayer()
    {
        // Aspetta un frame perché Unity processi i MeshCollider appena assegnati
        yield return null;

        Vector3 rayOrigin = new Vector3(player.position.x, 500f, player.position.z);
        if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, 1000f))
            player.position = new Vector3(player.position.x, hit.point.y + 1.5f, player.position.z);

        player.gameObject.SetActive(true);
    }

    void Update()
    {
        // Calcolo posizione attuale del player in coordinate Chunk
        int pX = Mathf.FloorToInt(player.position.x / targetConfig.chunkWidth);
        int pZ = Mathf.FloorToInt(player.position.z / targetConfig.chunkWidth);
        Vector2Int currentPlayerChunk = new Vector2Int(pX, pZ);

        if (currentPlayerChunk != lastPlayerChunk)
        {
            // Ogni volta che cambiamo chunk, controlliamo se l'LLM ha scritto un nuovo JSON
            CheckForConfigChange(currentPlayerChunk);
            UpdateChunks();
            lastPlayerChunk = currentPlayerChunk;
        }
    }

    void CheckForConfigChange(Vector2Int currentCoord)
    {
        BiomeConfig freshConfig = LoadLatestConfig();

        if (freshConfig.perlinScale != targetConfig.perlinScale || freshConfig.surfaceTileID != targetConfig.surfaceTileID)
        {
            Debug.Log("<color=orange>WorldManager:</color> Rilevato nuovo bioma. Inizio interpolazione.");

            oldConfig = targetConfig;
            targetConfig = freshConfig;
            transitionStartCoord = currentCoord;
            isTransitioning = true;

            mapVisualizer?.GenerateMaps(targetConfig);
            LoadBiomePrefabs();
        }
    }

    BiomeConfig LoadLatestConfig()
    {
        string path = Path.Combine(Application.dataPath, "Json", "llm_generated.json");

        if (File.Exists(path))
            return JsonUtility.FromJson<BiomeConfig>(File.ReadAllText(path));

        // Fallback se non esiste nessun file
        return new BiomeConfig {
            perlinScale = 0.1f, chunkHeight = 64, chunkWidth = 16,
            surfaceTileID = 1, middleTileID = 2, deepTileID = 3,
            liquidTileID = 13, waterLevel = 5,
            octaves = 4, ridgeFactor = 0f, warpStrength = 0.2f
        };
    }

    void UpdateChunks()
    {
        int pX = Mathf.FloorToInt(player.position.x / targetConfig.chunkWidth);
        int pZ = Mathf.FloorToInt(player.position.z / targetConfig.chunkWidth);

        for (int x = -viewDistance; x <= viewDistance; x++)
        {
            for (int z = -viewDistance; z <= viewDistance; z++)
            {
                Vector2Int chunkCoord = new Vector2Int(pX + x, pZ + z);

                if (!activeChunks.ContainsKey(chunkCoord))
                {
                    CreateChunk(chunkCoord);
                }
            }
        }
        
        RemoveFarChunks(new Vector2Int(pX, pZ));
    }

    void CreateChunk(Vector2Int coord)
    {
        Vector3 worldPos = new Vector3(coord.x * targetConfig.chunkWidth, 0, coord.y * targetConfig.chunkWidth);
        GameObject newChunkGO = Instantiate(chunkPrefab, worldPos, Quaternion.identity);
        Chunk chunkScript = newChunkGO.GetComponent<Chunk>();

        if (chunkScript != null)
        {
            if (sharedChunkMaterial != null)
                newChunkGO.GetComponent<MeshRenderer>().sharedMaterial = sharedChunkMaterial;

            Vector2 tCenter     = isTransitioning ? (Vector2)transitionStartCoord : new Vector2(-99999f, -99999f);
            float   innerRadius = isTransitioning ? (float)viewDistance : 0f;

            chunkScript.Initialize(coord, oldConfig, targetConfig, tCenter, innerRadius, blendDistance, pathWidth * 0.5f, pathBlendWidth, pathLevel);
            activeChunks.Add(coord, chunkScript);
            SpawnLayoutObjects(chunkScript, worldPos, targetConfig);
        }
    }

    void SpawnLayoutObjects(Chunk chunk, Vector3 worldPos, BiomeConfig cfg)
    {
        var allNames = new List<string>(_readyPrefabs.Keys);
        allNames.AddRange(_pendingObjectNames);
        if (allNames.Count == 0) return;

        float[,] layout  = chunk.LayoutMap;
        int[,]   heights = chunk.HeightMap;
        int w = cfg.chunkWidth;

        for (int x = 0; x < w; x++)
        for (int z = 0; z < w; z++)
        {
            if (layout[x, z] <= cfg.objectDensityThreshold) continue;

            string name      = allNames[Random.Range(0, allNames.Count)];
            float  freqChance = _objectFreqChances.TryGetValue(name, out float fc) ? fc : 1f;
            float  scaleFactor = _objectScaleFactors.TryGetValue(name, out float sf) ? sf : 1f;

            if (freqChance < 0f) // unique
            {
                if (_uniqueSpawned.Contains(name)) continue;
            }
            else if (Random.value > freqChance * objectSpawnChance) continue;

            float   wx      = worldPos.x + x;
            float   wz      = worldPos.z + z;
            float   groundY = GetGroundY(wx, wz, heights[x, z]);
            Vector3 pos     = new Vector3(wx, groundY, wz);

            if (_readyPrefabs.TryGetValue(name, out var prefab))
            {
                var obj = Instantiate(prefab, pos, Quaternion.Euler(-90f, 0f, 0f), chunk.transform);
                obj.transform.localScale = Vector3.one * scaleFactor;
                var rend = obj.GetComponentInChildren<Renderer>();
                if (rend != null)
                    obj.transform.position += Vector3.up * (groundY - rend.bounds.min.y + objectGroundOffset);
                if (freqChance < 0f) _uniqueSpawned.Add(name);
            }
            else
            {
                float   billH        = scaleFactor;
                Vector3 billboardPos = new Vector3(pos.x, groundY + billH * 0.5f, pos.z);
                SpawnBillboard(name, billboardPos, chunk.transform, scaleFactor);
                if (freqChance < 0f) _uniqueSpawned.Add(name);
            }
        }
    }

    float GetGroundY(float wx, float wz, int fallbackY)
    {
        var origin = new Vector3(wx, fallbackY + 10f, wz);
        if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 30f))
            return hit.point.y;
        return fallbackY;
    }

    void SpawnBillboard(string objName, Vector3 pos, Transform parent, float scaleFactor = 1f)
    {
        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.transform.SetParent(parent);
        quad.transform.position   = pos;
        quad.transform.localScale = Vector3.one * scaleFactor;
        Destroy(quad.GetComponent<MeshCollider>());

        var placeholder = quad.AddComponent<BillboardPlaceholder>();
        placeholder.objectName = objName;

        if (!_placeholders.ContainsKey(objName))
            _placeholders[objName] = new List<BillboardPlaceholder>();
        _placeholders[objName].Add(placeholder);
    }

    public void UpdatePlaceholderTexture(string objectName, byte[] pngBytes)
    {
        if (!_placeholders.TryGetValue(objectName, out var list)) return;
        var tex = new Texture2D(2, 2);
        tex.LoadImage(pngBytes);
        foreach (var p in list)
            if (p != null) p.SetTexture(tex);
    }

    public void ReplacePlaceholders(string objectName)
    {
#if UNITY_EDITOR
        string prefabPath = $"Assets/GeneratedAsset/{objectName}.prefab";
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefab == null)
        {
            Debug.LogWarning($"[WorldManager] Prefab non trovato per sostituzione: {prefabPath}");
            return;
        }

        _readyPrefabs[objectName] = prefab;
        _pendingObjectNames.Remove(objectName);
        float scaleFactor = _objectScaleFactors.TryGetValue(objectName, out float sf) ? sf : scaleDefault;

        if (!_placeholders.TryGetValue(objectName, out var list)) return;
        foreach (var p in list)
        {
            if (p == null) continue;
            float     billH   = scaleFactor;
            float     groundY = p.transform.position.y - billH * 0.5f;
            Transform parent  = p.transform.parent;
            Destroy(p.gameObject);
            var obj = Instantiate(prefab, new Vector3(p.transform.position.x, groundY, p.transform.position.z), Quaternion.Euler(-90f, 0f, 0f), parent);
            obj.transform.localScale = Vector3.one * scaleFactor;
            var rend = obj.GetComponentInChildren<Renderer>();
            if (rend != null)
                obj.transform.position += Vector3.up * (groundY - rend.bounds.min.y + objectGroundOffset);
        }
        _placeholders.Remove(objectName);
        Debug.Log($"[WorldManager] Sostituiti placeholder di '{objectName}' con prefab 3D.");
#endif
    }

    public void ReloadBiomePrefabs() => LoadBiomePrefabs();

    void LoadBiomePrefabs()
    {
        _readyPrefabs.Clear();
        _pendingObjectNames.Clear();
        _objectScaleFactors.Clear();
        _objectFreqChances.Clear();
        _uniqueSpawned.Clear();
        _placeholders.Clear();

        string path = Path.Combine(Application.dataPath, "Json", "current_objects.json");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "{}");
        }

        try
        {
            var root = JObject.Parse(File.ReadAllText(path));
            foreach (var entry in root)
            {
                string name = entry.Key;

                // Support both formats: { name: id } and { name: { id, size, frequency } }
                float scaleF = 1f, freqC = 1f;
                if (entry.Value?.Type == JTokenType.Object)
                {
                    scaleF = SizeToScale(entry.Value["size"]?.ToString() ?? "big");
                    freqC  = FreqToChance(    entry.Value["frequency"]?.ToString() ?? "common");
                }
                _objectScaleFactors[name] = scaleF;
                _objectFreqChances[name]  = freqC;

                string prefabPath = $"Assets/GeneratedAsset/{name}.prefab";
#if UNITY_EDITOR
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab != null) _readyPrefabs[name] = prefab;
                else                _pendingObjectNames.Add(name);
#else
                _pendingObjectNames.Add(name);
#endif
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[WorldManager] Errore lettura current_objects.json: {e.Message}");
        }

        if (_readyPrefabs.Count == 0 && _pendingObjectNames.Count == 0 && defaultObjectPrefab != null)
            _readyPrefabs["__default__"] = defaultObjectPrefab;

        Debug.Log($"[WorldManager] {_readyPrefabs.Count} prefab pronti, {_pendingObjectNames.Count} in attesa.");
    }

    float SizeToScale(string size) => size switch
    {
        "huge"  => scaleHuge,
        "big"   => scaleBig,
        "small" => scaleSmall,
        "tiny"  => scaleTiny,
        _       => scaleDefault
    };

    // Returns per-object spawn probability multiplier; -1 = unique (max 1 in world)
    static float FreqToChance(string freq) => freq switch
    {
        "very_common" => 1.00f,
        "common"      => 0.50f,
        "rare"        => 0.10f,
        "super_rare"  => 0.03f,
        "unique"      => -1f,
        _             => 0.50f
    };

    public void RegenerateAllChunks()
    {
        foreach (var kvp in activeChunks)
            if (kvp.Value != null) Destroy(kvp.Value.gameObject);
        activeChunks.Clear();
        _uniqueSpawned.Clear();
        UpdateChunks();
        Debug.Log("[WorldManager] Tutti i chunk rigenerati con i nuovi oggetti.");
    }

    void RemoveFarChunks(Vector2Int playerChunkPos)
    {
        List<Vector2Int> coordsToRemove = new List<Vector2Int>();

        foreach (var chunk in activeChunks)
        {
            float distance = Vector2Int.Distance(playerChunkPos, chunk.Key);
            if (distance > viewDistance + 1)
            {
                coordsToRemove.Add(chunk.Key);
            }
        }

        foreach (Vector2Int coord in coordsToRemove)
        {
            if (activeChunks.ContainsKey(coord))
            {
                Destroy(activeChunks[coord].gameObject);
                activeChunks.Remove(coord);
            }
        }
    }
}