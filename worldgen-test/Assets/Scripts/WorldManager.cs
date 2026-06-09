using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Coordinates the generation, dynamic updating, asset distribution, 
/// and lifecycle of procedural chunks based on runtime LLM outputs.
/// </summary>
public class WorldManager : MonoBehaviour
{
    [Header("Core References")]
    public Transform player;
    public GameObject chunkPrefab;
    public TextureAtlasManager atlasManager;

    [Header("Vision Settings")]
    public int viewDistance = 4;
    [Tooltip("Duration of the smooth interpolation blend between biomes measured in chunks.")]
    public int blendDistance = 10; 

    [Header("Path / Road Alignment")]
    public float pathWidth = 10f;      
    public float pathBlendWidth = 20f; 
    public int pathLevel = 8;          

    [Header("Layout & Spawning Rules")]
    public GameObject defaultObjectPrefab;  
    [Range(0f, 1f)]
    public float objectSpawnChance = 0.2f;
    [Tooltip("Slightly sinks objects into the ground to ensure seamless placement without gaps.")]
    public float objectGroundOffset = -0.3f;  

    [Header("Object Scales by Categorized Size")]
    public float scaleHuge = 15f;
    public float scaleBig = 7f;
    public float scaleSmall = 3f;
    public float scaleTiny = 1f;
    public float scaleDefault = 5f;

    [Header("Object Spacing (Jittered Grid)")]
    [Tooltip("Grid cell sizes. Determines minimum boundaries and padding between assets.")]
    public int cellVeryCommon = 3;
    public int cellCommon = 5;
    public int cellRare = 10;
    public int cellSuperRare = 20;
    public int cellUnique = 32;
    [Tooltip("Radius around unique macro-structures where low-priority assets are suppressed.")]
    public int uniqueExclusionRadius = 12;

    // Runtime tracking collections for asset streaming
    private Dictionary<string, GameObject> _readyPrefabs = new Dictionary<string, GameObject>();
    private List<string> _pendingObjectNames = new List<string>();
    private Dictionary<string, List<BillboardPlaceholder>> _placeholders = new Dictionary<string, List<BillboardPlaceholder>>();
    private Dictionary<string, float> _objectScaleFactors = new Dictionary<string, float>();
    private Dictionary<string, float> _objectFreqChances = new Dictionary<string, float>();
    private HashSet<string> _uniqueSpawned = new HashSet<string>();

    [Header("Debug & Map Visualization")]
    public MapVisualizer mapVisualizer;
    public bool skipInitialPlayerSpawn = false;

    // Chunk tracking and interpolation states
    private Dictionary<Vector2Int, Chunk> activeChunks = new Dictionary<Vector2Int, Chunk>();
    private Vector2Int lastPlayerChunk;
    private BiomeConfig oldConfig;
    private BiomeConfig targetConfig;
    private bool isTransitioning = false;
    private Vector2Int transitionStartCoord;

    // Shared runtime material definitions
    private Texture2D atlasTexture;
    private Material sharedChunkMaterial;

    void Start() => StartCoroutine(InitRoutine());

    /// <summary>
    /// Handles chronological setup steps ensuring assets are synced before rendering.
    /// </summary>
    IEnumerator InitRoutine()
    {
        player.gameObject.SetActive(false);

        // Instantiates a shared material instance from the chunk prefab template
        var prefabRenderer = chunkPrefab.GetComponent<MeshRenderer>();
        if (prefabRenderer != null)
            sharedChunkMaterial = new Material(prefabRenderer.sharedMaterial);
        else
            Debug.LogError("[WorldManager] ChunkPrefab is missing a MeshRenderer component.");

        // Fallback check to rebuild texture atlas if missing on local disk
        string atlasPath = Path.Combine(Application.dataPath, "GeneratedTexture", "TextureAtlas.png");
        if (!File.Exists(atlasPath) && atlasManager != null && atlasManager.HasMaterials)
        {
            Debug.Log("<color=cyan>[WorldManager]</color> Atlas file missing. Rebuilding from available source imagery...");
            atlasManager.RebuildAtlasFromFiles();
        }

        LoadAtlasTexture();
        
        // Contextual listeners for texture engine extensions
        GenerateTexture.OnGenerationComplete += _ => LoadAtlasTexture();
        VoxelRenderer.OnPrefabSaved += HandlePrefabSaved;

        // Load the initial operational config written by the pipeline
        targetConfig = LoadLatestConfig();
        oldConfig = targetConfig;

        mapVisualizer?.GenerateMaps(targetConfig);

#if UNITY_EDITOR
        AssetDatabase.Refresh();
#endif
        LoadBiomePrefabs();
        UpdateChunks();

        if (!skipInitialPlayerSpawn)
            yield return StartCoroutine(SpawnPlayer());
    }

    void OnDestroy()
    {
        GenerateTexture.OnGenerationComplete -= _ => LoadAtlasTexture();
        VoxelRenderer.OnPrefabSaved -= HandlePrefabSaved;
    }

    /// <summary>
    /// Reads and applies the consolidated master atlas sheets into the material pipeline.
    /// </summary>
    void LoadAtlasTexture()
    {
        string albedoPath = Path.Combine(Application.dataPath, "GeneratedTexture", "TextureAtlas.png");
        if (File.Exists(albedoPath))
        {
            atlasTexture ??= new Texture2D(2, 2);
            atlasTexture.LoadImage(File.ReadAllBytes(albedoPath));
            sharedChunkMaterial?.SetTexture("_BaseMap", atlasTexture);
            Debug.Log("[WorldManager] Successfully bounded shared albedo texture atlas to rendering layer.");
        }
    }

    /// <summary>
    /// Snaps the player entity safely onto the terrain surface via downcasting ray projections.
    /// </summary>
    public IEnumerator SpawnPlayer()
    {
        yield return null; // Yield to ensure physics engines handle populated colliders

        Vector3 rayOrigin = new Vector3(player.position.x, 500f, player.position.z);
        if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, 1000f))
            player.position = new Vector3(player.position.x, hit.point.y + 1.5f, player.position.z);

        player.gameObject.SetActive(true);
    }

    void Update()
    {
        int pX = Mathf.FloorToInt(player.position.x / targetConfig.chunkWidth);
        int pZ = Mathf.FloorToInt(player.position.z / targetConfig.chunkWidth);
        Vector2Int currentPlayerChunk = new Vector2Int(pX, pZ);

        if (currentPlayerChunk != lastPlayerChunk)
        {
            CheckForConfigChange(currentPlayerChunk);
            UpdateChunks();
            lastPlayerChunk = currentPlayerChunk;
        }
    }

    /// <summary>
    /// Evaluates if the LLM backend wrote updated target specifications into the ecosystem files.
    /// </summary>
    void CheckForConfigChange(Vector2Int currentCoord)
    {
        if (GenerateAsset.IsGenerating) return;

        BiomeConfig freshConfig = LoadLatestConfig();

        // Check variance across crucial map identifiers to trigger linear morphing transitions
        if (freshConfig.perlinScale != targetConfig.perlinScale || freshConfig.surfaceTileID != targetConfig.surfaceTileID)
        {
            Debug.Log("<color=orange>[WorldManager]</color> Dynamic biome modification detected. Initializing spatial interpolation...");

            oldConfig = targetConfig;
            targetConfig = freshConfig;
            transitionStartCoord = currentCoord;
            isTransitioning = true;

            mapVisualizer?.GenerateMaps(targetConfig);
            LoadBiomePrefabs();
        }
    }

    /// <summary>
    /// Parses configuration variables directly from the target infrastructure file on disk.
    /// </summary>
    BiomeConfig LoadLatestConfig()
    {
        string path = Path.Combine(Application.dataPath, "Json", "llm_generated.json");

        if (File.Exists(path))
            return JsonUtility.FromJson<BiomeConfig>(File.ReadAllText(path));

        return new BiomeConfig {
            perlinScale = 0.1f, chunkHeight = 64, chunkWidth = 16,
            surfaceTileID = 1, middleTileID = 2, deepTileID = 3,
            liquidTileID = 13, waterLevel = 5,
            octaves = 4, ridgeFactor = 0f, warpStrength = 0.2f
        };
    }

    /// <summary>
    /// Populates newly visible spatial coordinates and unloads distant nodes outside user perimeter.
    /// </summary>
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

            Vector2 tCenter = isTransitioning ? (Vector2)transitionStartCoord : new Vector2(-99999f, -99999f);
            float innerRadius = isTransitioning ? (float)viewDistance : 0f;

            chunkScript.Initialize(coord, oldConfig, targetConfig, tCenter, innerRadius, blendDistance, pathWidth * 0.5f, pathBlendWidth, pathLevel);
            activeChunks.Add(coord, chunkScript);
            SpawnLayoutObjects(chunkScript, worldPos, targetConfig);
        }
    }

    /// <summary>
    /// Processes landscape geometry distributions, preventing overlapping or clipped asset placements.
    /// Fully adapted to support direct memory instantiations inside standalone builds.
    /// </summary>
    void SpawnLayoutObjects(Chunk chunk, Vector3 worldPos, BiomeConfig cfg)
    {
        var allNames = new List<string>(_readyPrefabs.Keys);
        allNames.AddRange(_pendingObjectNames);
        if (allNames.Count == 0) return;

        var veryCommonNames = new List<string>();
        var commonNames = new List<string>();
        var rareNames = new List<string>();
        var superRareNames = new List<string>();
        var uniqueNames = new List<string>();

        foreach (string n in allNames)
        {
            float fc = _objectFreqChances.TryGetValue(n, out float f) ? f : 0.5f;
            if (fc < 0f) uniqueNames.Add(n);
            else if (fc <= 0.03f) superRareNames.Add(n);
            else if (fc <= 0.10f) rareNames.Add(n);
            else if (fc <= 0.50f) commonNames.Add(n);
            else veryCommonNames.Add(n);
        }

        (List<string> names, int cellSize, int exclRadius, bool isStructureTier)[] tiers =
        {
            (uniqueNames, cellUnique, uniqueExclusionRadius, true),
            (superRareNames, cellSuperRare, cellSuperRare / 3, true),
            (rareNames, cellRare, cellRare / 3, true),
            (commonNames, cellCommon, 0, false),
            (veryCommonNames, cellVeryCommon, 0, false),
        };

        int w = cfg.chunkWidth;
        int bx = Mathf.RoundToInt(worldPos.x);
        int bz = Mathf.RoundToInt(worldPos.z);

        bool[,] excluded = new bool[w, w];

        foreach (var (names, cellSize, exclRadius, isStructureTier) in tiers)
        {
            if (names.Count == 0) continue;

            int gcx0 = Mathf.FloorToInt((float)bx / cellSize);
            int gcz0 = Mathf.FloorToInt((float)bz / cellSize);
            int gcx1 = Mathf.FloorToInt((float)(bx + w - 1) / cellSize);
            int gcz1 = Mathf.FloorToInt((float)(bz + w - 1) / cellSize);

            for (int gcx = gcx0; gcx <= gcx1; gcx++)
            for (int gcz = gcz0; gcz <= gcz1; gcz++)
            {
                int nameSeed = PlacementHash(gcx, gcz, cellSize * 1000);
                string name = names[(int)((uint)nameSeed % (uint)names.Count)];

                bool isUnique = _objectFreqChances.TryGetValue(name, out float fc) && fc < 0f;
                if (isUnique && _uniqueSpawned.Contains(name)) continue;

                int jSeed = PlacementHash(gcx, gcz, cellSize * 997 + 1);
                float jx = ((jSeed & 0xFF) / 255f) * cellSize;
                float jz = (((jSeed >> 8) & 0xFF) / 255f) * cellSize;

                float wx = gcx * cellSize + jx;
                float wz = gcz * cellSize + jz;

                int lx = Mathf.FloorToInt(wx - bx);
                int lz = Mathf.FloorToInt(wz - bz);

                if (lx < 0 || lx >= w || lz < 0 || lz >= w) continue;
                if (excluded[lx, lz]) continue;

                float layoutVal = chunk.LayoutMap[lx, lz];

                if (isStructureTier)
                {
                    if (layoutVal <= cfg.objectDensityThreshold) continue;
                }
                else
                {
                    if (layoutVal > 0.75f || layoutVal <= 0.15f) continue;
                    if (chunk.HeightMap[lx, lz] <= cfg.waterLevel) continue;
                }

                if (isUnique) _uniqueSpawned.Add(name);

                if (exclRadius > 0)
                {
                    for (int dx = -exclRadius; dx <= exclRadius; dx++)
                    for (int dz = -exclRadius; dz <= exclRadius; dz++)
                    {
                        int nx = lx + dx, nz = lz + dz;
                        if ((uint)nx < (uint)w && (uint)nz < (uint)w)
                            excluded[nx, nz] = true;
                    }
                }

                float scaleFactor = _objectScaleFactors.TryGetValue(name, out float sf) ? sf : scaleDefault;
                float groundY = GetGroundY(wx, wz, chunk.HeightMap[lx, lz]);

                // COMPATIBILITÀ CRUCIALE STANDALONE BUILD:
                // Canale A: Legge il prefabbricato pronto dall'Editor (se disponibile)
                if (_readyPrefabs.TryGetValue(name, out var prefab) && prefab != null)
                {
                    var obj = Instantiate(prefab, new Vector3(wx, groundY, wz), Quaternion.Euler(-90f, 0f, 0f), chunk.transform);
                    obj.transform.localScale = Vector3.one * scaleFactor;
                    var rend = obj.GetComponentInChildren<Renderer>();
                    if (rend != null)
                        obj.transform.position += Vector3.up * (groundY - rend.bounds.min.y + objectGroundOffset);
                }
                // Canale B: Legge direttamente la RAM (Sblocca il funzionamento nelle build compile ed eseguite)
                else if (VoxelRenderer.RuntimeMeshCache.TryGetValue(name, out Mesh runtimeMesh))
                {
                    GameObject obj = new GameObject(name);
                    obj.transform.SetParent(chunk.transform);
                    obj.transform.position = new Vector3(wx, groundY, wz);
                    obj.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);

                    var mf = obj.AddComponent<MeshFilter>();
                    mf.sharedMesh = runtimeMesh;

                    var mr = obj.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = VoxelRenderer.RuntimeMaterialInstance;

                    var mc = obj.AddComponent<MeshCollider>();
                    mc.sharedMesh = runtimeMesh;

                    obj.transform.localScale = Vector3.one * scaleFactor;
                    var rend = obj.GetComponentInChildren<Renderer>();
                    if (rend != null)
                        obj.transform.position += Vector3.up * (groundY - rend.bounds.min.y + objectGroundOffset);
                }
                // Canale C: Asset non ancora generato da Trellis, spawna l'ancora/cartellone temporaneo
                else
                {
                    float fc2 = _objectFreqChances.TryGetValue(name, out float fv) ? fv : 0.5f;
                    bool showBillboard = fc2 > 0.10f; 
                    SpawnBillboard(name, new Vector3(wx, groundY + scaleFactor * 0.5f, wz), chunk.transform, scaleFactor, showBillboard);
                }
            }
        }
    }

    /// <summary>
    /// Highly optimized, reproducible Murmur-like positioning layout seed multiplier.
    /// </summary>
    static int PlacementHash(int x, int z, int seed)
    {
        unchecked
        {
            int h = x * 374761393 + z * 668265263 + seed;
            h ^= h >> 13;
            h *= 1274126177;
            return h ^ (h >> 16);
        }
    }

    float GetGroundY(float wx, float wz, int fallbackY)
    {
        var origin = new Vector3(wx, fallbackY + 10f, wz);
        if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 30f))
            return hit.point.y;
        return fallbackY;
    }

    /// <summary>
    /// Generates segonposto placeholders used to safely update streamed image textures or 3D prefabs.
    /// </summary>
    void SpawnBillboard(string objName, Vector3 pos, Transform parent, float scaleFactor = 1f, bool visible = true)
    {
        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.transform.SetParent(parent);
        quad.transform.position = pos;
        quad.transform.localScale = Vector3.one * scaleFactor;
        
        Destroy(quad.GetComponent<MeshCollider>());
        if (!visible) quad.GetComponent<MeshRenderer>().enabled = false;

        var placeholder = quad.AddComponent<BillboardPlaceholder>();
        placeholder.objectName = objName;

        if (!_placeholders.ContainsKey(objName))
            _placeholders[objName] = new List<BillboardPlaceholder>();
        _placeholders[objName].Add(placeholder);
    }

    /// <summary>
    /// Seamlessly binds live pixel data streamed via stable diffusion setups into designated placeholder instances.
    /// </summary>
    public void UpdatePlaceholderTexture(string objectName, byte[] pngBytes)
    {
        if (!_placeholders.TryGetValue(objectName, out var list)) return;
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        tex.LoadImage(pngBytes);
        
        var pixels = tex.GetPixels();
        for (int i = 0; i < pixels.Length; i++)
        {
            Color c = pixels[i];
            if (c.r > 0.85f && c.g > 0.85f && c.b > 0.85f)
                pixels[i].a = 0f;
        }
        tex.SetPixels(pixels);
        tex.Apply();
        
        foreach (var p in list)
            if (p != null) p.SetTexture(tex);
    }

    /// <summary>
    /// Synchronous anchor callback intercepting completed Voxel compilation pipelines.
    /// </summary>
    void HandlePrefabSaved(string name, GameObject prefab)
    {
        _readyPrefabs[name] = prefab;
        ReplacePlaceholders(name);
    }

    /// <summary>
    /// Rimpiazza i billboard/placeholder con i modelli 3D reali.
    /// Garantisce stabilità assoluta sia a runtime nell'Editor che nella Build compilata (.exe).
    /// </summary>
    public void ReplacePlaceholders(string objectName)
    {
        // 1. TENTA IL RECUPERO DEL PREFAB (Valido principalmente all'avvio o nell'Editor)
        if (!_readyPrefabs.TryGetValue(objectName, out var prefab))
        {
#if UNITY_EDITOR
            // Se siamo nell'Editor, eseguiamo un caricamento forzato dal database dei file
            string prefabPath = $"Assets/GeneratedAsset/{objectName}.prefab";
            AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceSynchronousImport);
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab != null) _readyPrefabs[objectName] = prefab;
#endif
        }

        // Rimuove l'oggetto dalla lista di quelli in attesa di generazione
        _pendingObjectNames.Remove(objectName);
        float scaleFactor = _objectScaleFactors.TryGetValue(objectName, out float sf) ? sf : scaleDefault;

        // Se non ci sono placeholder o ancore registrate per questo oggetto, non c'è nulla da fare
        if (!_placeholders.TryGetValue(objectName, out var list)) return;

        // Cache locale di coordinate e padri prima della distruzione dei vecchi GameObject
        var toSpawn = new List<(Vector3 pos, Transform parent)>();
        foreach (var p in list)
        {
            if (p != null) toSpawn.Add((p.transform.position, p.transform.parent));
        }

        // Distrugge i vecchi placeholder (cartelloni o ancore invisibili) per fare spazio al 3D
        foreach (var p in list)
        {
            if (p != null) Destroy(p.gameObject);
        }
        _placeholders.Remove(objectName);

        // 2. ISTANZIAZIONE REALE: Priorità alla RAM (Build-Safe) con Fallback su Prefab (Editor)
        foreach (var (pos, parent) in toSpawn)
        {
            float groundY = pos.y - scaleFactor * 0.5f;
            GameObject obj;

            // CANALE RUNTIME / COMPILATO: Se la mesh è presente nella cache volatile della RAM (Funziona ovunque!)
            if (VoxelRenderer.RuntimeMeshCache.TryGetValue(objectName, out Mesh runtimeMesh))
            {
                obj = new GameObject(objectName);
                obj.transform.SetParent(parent);
                obj.transform.position = new Vector3(pos.x, groundY, pos.z);
                obj.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);

                // Assegna la mesh al volo in memoria
                var mf = obj.AddComponent<MeshFilter>();
                mf.sharedMesh = runtimeMesh;

                // Applica il materiale condiviso delle texture vertex-color
                var mr = obj.AddComponent<MeshRenderer>();
                mr.sharedMaterial = VoxelRenderer.RuntimeMaterialInstance;

                // Genera il collisore runtime basato sulla mesh appena calcolata
                var mc = obj.AddComponent<MeshCollider>();
                mc.sharedMesh = runtimeMesh;
            }
            // CANALE EDITOR / FALLBACK: Se il file .prefab su disco è pronto e accessibile
            else if (prefab != null)
            {
                obj = Instantiate(prefab, new Vector3(pos.x, groundY, pos.z), Quaternion.Euler(-90f, 0f, 0f), parent);
            }
            else
            {
                Debug.LogWarning($"[WorldManager] Impossibile rimpiazzare l'oggetto '{objectName}': risorsa non trovata né in RAM né su disco.");
                continue;
            }

            // Applica la scala corretta configurata dal Tier dell'asset
            obj.transform.localScale = Vector3.one * scaleFactor;

            // Allineamento millimetrico dei piedi dell'oggetto alla superficie del terreno voxel
            var rend = obj.GetComponentInChildren<Renderer>();
            if (rend != null)
            {
                obj.transform.position += Vector3.up * (groundY - rend.bounds.min.y + objectGroundOffset);
            }
        }

        Debug.Log($"[WorldManager] Sostituzione completata runtime per '{objectName}' ({toSpawn.Count} modelli 3D istanziati con successo).");
    }

    public void ReloadBiomePrefabs()
    {
#if UNITY_EDITOR
        AssetDatabase.Refresh();
#endif
        LoadBiomePrefabs();
    }

    /// <summary>
    /// Manually commands structural parameter adjustments during user updates.
    /// </summary>
    public void ApplyNewConfig()
    {
        BiomeConfig freshConfig = LoadLatestConfig();
        if (freshConfig.perlinScale == targetConfig.perlinScale && freshConfig.surfaceTileID != targetConfig.surfaceTileID)
            return;
        oldConfig = targetConfig;
        targetConfig = freshConfig;
        int pX = Mathf.FloorToInt(player.position.x / targetConfig.chunkWidth);
        int pZ = Mathf.FloorToInt(player.position.z / targetConfig.chunkWidth);
        transitionStartCoord = new Vector2Int(pX, pZ);
        isTransitioning = true;
        mapVisualizer?.GenerateMaps(targetConfig);
    }

    /// <summary>
    /// Reads environmental tracking parameters to sync current target objectives.
    /// </summary>
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
#if UNITY_EDITOR
        AssetDatabase.ImportAsset("Assets/Json/current_objects.json", ImportAssetOptions.ForceSynchronousImport);
#endif

        try
        {
            var root = JObject.Parse(File.ReadAllText(path));
            foreach (var entry in root)
            {
                string name = entry.Key;
                float scaleF = 1f, freqC = 1f;

                if (entry.Value?.Type == JTokenType.Object)
                {
                    scaleF = SizeToScale(entry.Value["size"]?.ToString() ?? "big");
                    freqC = FreqToChance(entry.Value["frequency"]?.ToString() ?? "common");
                }
                _objectScaleFactors[name] = scaleF;
                _objectFreqChances[name] = freqC;

                string prefabPath = $"Assets/GeneratedAsset/{name}.prefab";
                string prefabDiskPath = Path.Combine(Application.dataPath, "GeneratedAsset", name + ".prefab");
#if UNITY_EDITOR
                if (File.Exists(prefabDiskPath))
                {
                    AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceSynchronousImport);
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                    if (prefab != null) _readyPrefabs[name] = prefab;
                    else _pendingObjectNames.Add(name);
                }
                else
                {
                    _pendingObjectNames.Add(name);
                }
#else
                _pendingObjectNames.Add(name);
#endif
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[WorldManager] Parsing current_objects.json failed: {e.Message}");
        }

        if (_readyPrefabs.Count == 0 && _pendingObjectNames.Count == 0 && defaultObjectPrefab != null)
            _readyPrefabs["__default__"] = defaultObjectPrefab;
    }

    float SizeToScale(string size) => size switch
    {
        "huge" => scaleHuge,
        "big" => scaleBig,
        "small" => scaleSmall,
        "tiny" => scaleTiny,
        _ => scaleDefault
    };

    float FreqToChance(string freq) => freq switch
    {
        "very_common" => 1.00f,
        "common" => 0.50f,
        "rare" => 0.10f,
        "super_rare" => 0.03f,
        "unique" => -1f,
        _ => 0.50f
    };

    public void RegenerateAllChunks()
    {
        foreach (var kvp in activeChunks)
            if (kvp.Value != null) Destroy(kvp.Value.gameObject);
        activeChunks.Clear();
        _uniqueSpawned.Clear();
        UpdateChunks();
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