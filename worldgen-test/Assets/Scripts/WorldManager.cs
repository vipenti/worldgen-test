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
    public float scaleNormal = 5f;
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
    private Dictionary<string, string> _objectTypes = new Dictionary<string, string>();
    private Dictionary<string, int> _objectPriorities = new Dictionary<string, int>();
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

    // Cache structure used to track positioned primary buildings 
    // so secondary props can align themselves relative to them.
    private struct StructureAnchor
    {
        public Vector3 position;    // Global world position of the building
        public string assetName;    // Name of the asset (e.g., "village house")
        public float scale;         // Applied scale factor
    }

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
    /// Distribuisce gli oggetti sulla griglia rispettando rigorosamente l'ordine di priorità (1-4).
    /// FIX CORRETTIVO: La quantità e la densità di ogni singolo asset sono determinate esclusivamente
    /// dal rispettivo parametro di rarità (Rarity), isolando i cicli di spawn per ogni oggetto.
    /// </summary>
    void SpawnLayoutObjects(Chunk chunk, Vector3 worldPos, BiomeConfig cfg)
    {
        var allNames = new List<string>(_readyPrefabs.Keys);
        allNames.AddRange(_pendingObjectNames);
        if (allNames.Count == 0) return;

        int w = cfg.chunkWidth;
        int bx = Mathf.RoundToInt(worldPos.x);
        int bz = Mathf.RoundToInt(worldPos.z);

        // Matrice di occupazione dei pixel e cache delle strutture primarie
        bool[,] excluded = new bool[w, w];
        List<StructureAnchor> placedStructures = new List<StructureAnchor>();

        // Eseguiamo i 4 passaggi cronologici sequenziali (da Priorità 1 a Priorità 4)
        for (int currentPriority = 1; currentPriority <= 4; currentPriority++)
        {
            // Filtriamo gli oggetti che appartengono unicamente alla priorità corrente
            var passObjects = allNames.Where(n => {
                int pr = _objectPriorities.TryGetValue(n, out int pVal) ? pVal : 3;
                return pr == currentPriority;
            }).ToList();

            if (passObjects.Count == 0) continue;

            // Iteriamo INDIPENDENTEMENTE su ogni singolo oggetto del pass corrente
            foreach (string name in passObjects)
            {
                float fc = _objectFreqChances.TryGetValue(name, out float f) ? f : 0.5f;
                bool isUnique = fc < 0f;

                // =================================================================
                // LOGICA SPECIALE PER PASSAGGIO 2 (PROP RELAZIONALI AGGANCIATI)
                // =================================================================
                // Se siamo al Pass 2 e ci sono strutture (Pass 1) piazzate nel chunk,
                // i prop nascono in conseguenza ad esse, ma la quantità è regolata dalla rarità del prop.
                if (currentPriority == 2 && placedStructures.Count > 0)
                {
                    foreach (var structure in placedStructures)
                    {
                        // Roll probabilistico deterministico basato sul nome del prop e sulla casa
                        int propSeed = PlacementHash(Mathf.RoundToInt(structure.position.x), Mathf.RoundToInt(structure.position.z), name.GetHashCode());
                        float spawnRoll = (propSeed & 0xFFFF) / 65535f;
                        
                        // Il prop decide se nascere vicino a QUESTA casa solo in base alla PROPRIA rarità
                        if (!isUnique && spawnRoll > fc) continue;
                        if (isUnique && _uniqueSpawned.Contains(name)) continue;

                        // Scegliamo un lato cardinale adiacente alla struttura
                        int side = Mathf.Abs(propSeed) % 4;
                        float offsetDistance = (structure.scale * 0.5f) + 1.2f;
                        Vector3 offsetDir = side switch {
                            0 => Vector3.forward, 1 => Vector3.right, 2 => Vector3.back, _ => Vector3.left
                        };

                        Vector3 propWorldPos = structure.position + offsetDir * offsetDistance;
                        int plx = Mathf.FloorToInt(propWorldPos.x - bx);
                        int plz = Mathf.FloorToInt(propWorldPos.z - bz);

                        if (plx >= 0 && plx < w && plz >= 0 && plz < w)
                        {
                            if (excluded[plx, plz] || chunk.HeightMap[plx, plz] <= cfg.waterLevel) continue;

                            float pScale = _objectScaleFactors.TryGetValue(name, out float ps) ? ps : scaleSmall;
                            float pGroundY = GetGroundY(propWorldPos.x, propWorldPos.z, chunk.HeightMap[plx, plz]);

                            float rotZ = (propSeed >> 16 & 0xFF) / 255f * 360f;
                        InstantiateAssetPrimitive(name, propWorldPos.x, pGroundY, propWorldPos.z, pScale, chunk.transform, rotZ);
                            excluded[plx, plz] = true; // Blocca il punto
                            
                            if (isUnique) _uniqueSpawned.Add(name);
                        }
                    }
                    continue; // Passa al prossimo oggetto di priorità 2
                }

                // =================================================================
                // PASSO STANDARD SULLA GRIGLIA (PASS 1, 3, 4 E FALLBACK PASS 2)
                // =================================================================
                // Determiniamo la dimensione della cella UNICAMENTE in base alla rarità del singolo oggetto
                int cellSize = isUnique ? cellUnique : (fc <= 0.03f ? cellSuperRare : (fc <= 0.10f ? cellRare : (fc <= 0.50f ? cellCommon : cellVeryCommon)));

                int gcx0 = Mathf.FloorToInt((float)bx / cellSize);
                int gcz0 = Mathf.FloorToInt((float)bz / cellSize);
                int gcx1 = Mathf.FloorToInt((float)(bx + w - 1) / cellSize);
                int gcz1 = Mathf.FloorToInt((float)(bz + w - 1) / cellSize);

                for (int gcx = gcx0; gcx <= gcx1; gcx++)
                for (int gcz = gcz0; gcz <= gcz1; gcz++)
                {
                    if (isUnique && _uniqueSpawned.Contains(name)) continue;

                    // Seed deterministico basato sulla cella e sul nome dell'oggetto specifico
                    int cellHash = PlacementHash(gcx, gcz, name.GetHashCode());
                    
                    // CONTROLLO RARITÀ ASSOLUTO: Ogni oggetto lancia il proprio roll indipendente
                    float spawnRoll = (cellHash & 0xFFFF) / 65535f;
                    if (!isUnique && spawnRoll > fc) continue;

                    // Jitter di distorsione per rendere il posizionamento naturale
                    float jx = ((cellHash & 0xFF) / 255f) * cellSize;
                    float jz = (((cellHash >> 8) & 0xFF) / 255f) * cellSize;

                    float wx = gcx * cellSize + jx;
                    float wz = gcz * cellSize + jz;

                    int lx = Mathf.FloorToInt(wx - bx);
                    int lz = Mathf.FloorToInt(wz - bz);

                    if (lx < 0 || lx >= w || lz < 0 || lz >= w) continue;
                    if (excluded[lx, lz]) continue; // Salta se occupato da priorità superiori

                    float layoutVal = chunk.LayoutMap[lx, lz];
                    
                    // Le strutture (Pass 1) usano la soglia di densità, la natura (Pass 3 e 4) usa le bande sfumate
                    if (currentPriority == 1)
                    {
                        if (layoutVal <= cfg.objectDensityThreshold) continue;
                    }
                    else
                    {
                        float minLayout = currentPriority == 4 ? 0.10f : 0.15f;
                        float maxLayout = currentPriority == 4 ? 0.90f : 0.85f;
                        if (layoutVal <= minLayout || layoutVal > maxLayout) continue;
                    }
                    
                    if (chunk.HeightMap[lx, lz] <= cfg.waterLevel) continue;

                    float scaleFactor = _objectScaleFactors.TryGetValue(name, out float sf) ? sf : scaleDefault;
                    float groundY = GetGroundY(wx, wz, chunk.HeightMap[lx, lz]);

                    float rotZ = ((cellHash >> 16) & 0xFF) / 255f * 360f;
                    InstantiateAssetPrimitive(name, wx, groundY, wz, scaleFactor, chunk.transform, rotZ);

                    // Scaviamo l'impronta geometrica sulla matrice di esclusione per evitare compenetrazioni
                    int footRadius = Mathf.CeilToInt(scaleFactor * 0.5f);
                    for (int dx = -footRadius; dx <= footRadius; dx++)
                    for (int dz = -footRadius; dz <= footRadius; dz++)
                    {
                        int nx = lx + dx, nz = lz + dz;
                        if ((uint)nx < (uint)w && (uint)nz < (uint)w) excluded[nx, nz] = true;
                    }

                    if (isUnique) _uniqueSpawned.Add(name);

                    // Se abbiamo posizionato una struttura primaria (Pass 1), la registriamo come ancora per i prop (Pass 2)
                    if (currentPriority == 1)
                    {
                        placedStructures.Add(new StructureAnchor {
                            position = new Vector3(wx, groundY, wz),
                            assetName = name,
                            scale = scaleFactor
                        });
                    }
                }
            }
        }
    }/// <summary>
    /// Core abstraction helper managing dual hardware execution modes (RAM Mesh generation vs Prebuilt disk Prefabs).
    /// </summary>
    void InstantiateAssetPrimitive(string name, float wx, float groundY, float wz, float scaleFactor, Transform parent, float rotZ = 0f)
    {
        if (_readyPrefabs.TryGetValue(name, out var prefab) && prefab != null)
        {
            var obj = Instantiate(prefab, new Vector3(wx, groundY, wz), Quaternion.Euler(-90f, 0f, rotZ), parent);
            obj.transform.localScale = Vector3.one * scaleFactor;
            var rend = obj.GetComponentInChildren<Renderer>();
            if (rend != null)
                obj.transform.position += Vector3.up * (groundY - rend.bounds.min.y + objectGroundOffset);
        }
        else if (VoxelRenderer.RuntimeMeshCache.TryGetValue(name, out Mesh runtimeMesh))
        {
            GameObject obj = new GameObject(name);
            obj.transform.SetParent(parent);
            obj.transform.position = new Vector3(wx, groundY, wz);
            obj.transform.localRotation = Quaternion.Euler(-90f, 0f, rotZ);

            obj.AddComponent<MeshFilter>().sharedMesh = runtimeMesh;
            obj.AddComponent<MeshRenderer>().sharedMaterial = VoxelRenderer.RuntimeMaterialInstance;
            obj.AddComponent<MeshCollider>().sharedMesh = runtimeMesh;

            obj.transform.localScale = Vector3.one * scaleFactor;
            var rend = obj.GetComponentInChildren<Renderer>();
            if (rend != null)
                obj.transform.position += Vector3.up * (groundY - rend.bounds.min.y + objectGroundOffset);
        }
        else
        {
            float fc2 = _objectFreqChances.TryGetValue(name, out float fv) ? fv : 0.5f;
            bool showBillboard = fc2 > 0.10f; 
            SpawnBillboard(name, new Vector3(wx, groundY + scaleFactor * 0.5f, wz), parent, scaleFactor, showBillboard);
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
            int posHash = PlacementHash(Mathf.RoundToInt(pos.x), Mathf.RoundToInt(pos.z), objectName.GetHashCode());
            float rotZ = ((posHash >> 16) & 0xFF) / 255f * 360f;
            GameObject obj;

            // CANALE RUNTIME / COMPILATO: Se la mesh è presente nella cache volatile della RAM (Funziona ovunque!)
            if (VoxelRenderer.RuntimeMeshCache.TryGetValue(objectName, out Mesh runtimeMesh))
            {
                obj = new GameObject(objectName);
                obj.transform.SetParent(parent);
                obj.transform.position = new Vector3(pos.x, groundY, pos.z);
                obj.transform.localRotation = Quaternion.Euler(-90f, 0f, rotZ);

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
                obj = Instantiate(prefab, new Vector3(pos.x, groundY, pos.z), Quaternion.Euler(-90f, 0f, rotZ), parent);
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
    /// <summary>
    /// Reads environmental tracking parameters to sync current target objectives.
    /// Parses extended metadata hierarchy (size, frequency, type, priority) directly from JSON.
    /// </summary>
    void LoadBiomePrefabs()
    {
        // 1. RESET RUNTIME TRACKING COLLECTIONS
        _readyPrefabs.Clear();
        _pendingObjectNames.Clear();
        _objectScaleFactors.Clear();
        _objectFreqChances.Clear();
        _objectTypes.Clear();       // Reset structural behavior types
        _objectPriorities.Clear();  // Reset numeric hierarchy passes
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
            // 2. PARSE EXTENDED METADATA MATRIX
            var root = JObject.Parse(File.ReadAllText(path));
            foreach (var entry in root)
            {
                string name = entry.Key;
                float scaleF = 1f, freqC = 1f;
                string typeStr = "vegetation";
                int priorityVal = 3;

                if (entry.Value?.Type == JTokenType.Object)
                {
                    scaleF = SizeToScale(entry.Value["size"]?.ToString() ?? "big");
                    freqC = FreqToChance(entry.Value["frequency"]?.ToString() ?? "common");
                    typeStr = entry.Value["type"]?.ToString() ?? "vegetation";
                    priorityVal = entry.Value["priority"]?.Value<int>() ?? 3;
                }
                
                // Cache parsed metadata properties into global tracking registries
                _objectScaleFactors[name] = scaleF;
                _objectFreqChances[name] = freqC;
                _objectTypes[name] = typeStr;
                _objectPriorities[name] = priorityVal;

                // 3. CHECK DISK AVAILABILITY AND REGISTER ASSETS
                string prefabPath = $"Assets/GeneratedAsset/{name}.prefab";
                string prefabDiskPath = Path.Combine(Application.dataPath, "GeneratedAsset", name + ".prefab");
#if UNITY_EDITOR
                if (File.Exists(prefabDiskPath))
                {
                    AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceSynchronousImport);
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                    if (prefab != null) 
                        _readyPrefabs[name] = prefab;
                    else 
                        _pendingObjectNames.Add(name);
                }
                else
                {
                    _pendingObjectNames.Add(name);
                }
#else
                // Standalone builds dynamically rely on Runtime Volatile RAM Mesh Bridges
                _pendingObjectNames.Add(name);
#endif
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[WorldManager] Parsing current_objects.json failed: {e.Message}");
        }

        // Fallback boundary safety check to avoid complete world omission states
        if (_readyPrefabs.Count == 0 && _pendingObjectNames.Count == 0 && defaultObjectPrefab != null)
            _readyPrefabs["__default__"] = defaultObjectPrefab;
    }
    
    float SizeToScale(string size) => size switch
    {
        "huge" => scaleHuge,
        "big" => scaleBig,
        "normal" => scaleNormal,
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