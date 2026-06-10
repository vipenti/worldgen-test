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
    [Tooltip("Cuscinetto extra (in metri) oltre il cono visivo per attivare i chunk in anticipo ed eliminare il pop-in visivo.")]
    public float frustumPadding = 24f;

    [Header("Per-Size Rendering Distances")]
    [Tooltip("Distanza massima in metri a cui si vedono gli asset enormi (es. grattacieli).")]
    public float renderDistHuge = 250f;
    [Tooltip("Distanza massima in metri a cui si vedono gli asset grandi (es. case, grandi alberi).")]
    public float renderDistBig = 90f;
    [Tooltip("Distanza massima in metri a cui si vedono gli asset normali (es. alberi medi).")]
    public float renderDistNormal = 60f;
    [Tooltip("Distanza massima in metri a cui si vedono gli asset piccoli (es. cespugli, panchine).")]
    public float renderDistSmall = 35f;
    [Tooltip("Distanza massima in metri a cui si vedono gli asset minuscoli (es. fiori, funghi, sassi).")]
    public float renderDistTiny = 15f;

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
    private Dictionary<string, string> _objectSizes = new Dictionary<string, string>(); // Traccia la stringa di dimensione originaria
    private HashSet<string> _uniqueSpawned = new HashSet<string>();
    private Quaternion lastCameraRotation;

    [Header("Debug & Map Visualization")]
    public MapVisualizer mapVisualizer;
    public bool skipInitialPlayerSpawn = false;

    private Dictionary<Vector2Int, Chunk> activeChunks = new Dictionary<Vector2Int, Chunk>();
    private Vector2Int lastPlayerChunk;
    private BiomeConfig oldConfig;
    private BiomeConfig targetConfig;
    private bool isTransitioning = false;
    private Vector2Int transitionStartCoord;

    private Texture2D atlasTexture;
    private Material sharedChunkMaterial;

    private struct StructureAnchor
    {
        public Vector3 position;    
        public string assetName;    
        public float scale;         
    }

    void Start() => StartCoroutine(InitRoutine());

    IEnumerator InitRoutine()
    {
        player.gameObject.SetActive(false);

        var prefabRenderer = chunkPrefab.GetComponent<MeshRenderer>();
        if (prefabRenderer != null)
            sharedChunkMaterial = new Material(prefabRenderer.sharedMaterial);
        else
            Debug.LogError("[WorldManager] ChunkPrefab is missing a MeshRenderer component.");

        string atlasPath = Path.Combine(Application.dataPath, "GeneratedTexture", "TextureAtlas.png");
        if (!File.Exists(atlasPath) && atlasManager != null && atlasManager.HasMaterials)
        {
            Debug.Log("<color=cyan>[WorldManager]</color> Atlas file missing. Rebuilding from available source imagery...");
            atlasManager.RebuildAtlasFromFiles();
        }

        LoadAtlasTexture();
        
        GenerateTexture.OnGenerationComplete += _ => LoadAtlasTexture();
        VoxelRenderer.OnPrefabSaved += HandlePrefabSaved;

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

    public IEnumerator SpawnPlayer()
    {
        yield return null;

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
        
        Quaternion currentCamRot = Camera.main != null ? Camera.main.transform.rotation : player.rotation;

        if (currentPlayerChunk != lastPlayerChunk || Quaternion.Angle(currentCamRot, lastCameraRotation) > 4f)
        {
            CheckForConfigChange(currentPlayerChunk);
            UpdateChunks();
            lastPlayerChunk = currentPlayerChunk;
            lastCameraRotation = currentCamRot;
        }
    }

    void CheckForConfigChange(Vector2Int currentCoord)
    {
        if (GenerateAsset.IsGenerating) return;

        BiomeConfig freshConfig = LoadLatestConfig();

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
    /// Gestisce la scoperta e lo stato di attivazione dei chunk basandosi interamente
    /// sui piani di frustum della telecamera attiva, ottimizzando le performance.
    /// FIX: Allineata la logica di controllo quadrata per evitare il ciclo infinito di Creazione/Distruzione.
    /// </summary>
    void UpdateChunks()
    {
        int pX = Mathf.FloorToInt(player.position.x / targetConfig.chunkWidth);
        int pZ = Mathf.FloorToInt(player.position.z / targetConfig.chunkWidth);
        float cSize = targetConfig.chunkWidth;

        // Calcoliamo i 6 piani del cono visivo della telecamera
        Plane[] cameraPlanes = Camera.main != null ? GeometryUtility.CalculateFrustumPlanes(Camera.main) : null;

        // FASE 1: ASSICURIAMO LA CREAZIONE DI TUTTI I CHUNK NEL RAGGIO DI VISIONE QUADRATO
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

        // FASE 2: ATTIVAZIONE / DISATTIVAZIONE ULTRA-RAPIDA (CULLING DIREZIONALE)
        foreach (var kvp in activeChunks)
        {
            if (kvp.Value == null) continue;

            Vector2Int chunkCoord = kvp.Key;
            
            // Sicurezza: Se il chunk è già fuori dal perimetro quadrato a causa di un movimento, saltiamo il culling visivo
            int distX = Mathf.Abs(chunkCoord.x - pX);
            int distZ = Mathf.Abs(chunkCoord.y - pZ);
            if (distX > viewDistance || distZ > viewDistance)
                continue;

            // Area di totale sicurezza sotto i piedi (raggio stretto 1 chunk) sempre accesa
            bool isCloseSafety = distX <= 1 && distZ <= 1;
            bool isInsideFrustum = false;

            if (!isCloseSafety && cameraPlanes != null)
            {
                Vector3 center = new Vector3(chunkCoord.x * cSize + cSize * 0.5f, targetConfig.chunkHeight * 0.5f, chunkCoord.y * cSize + cSize * 0.5f);
                Vector3 size = new Vector3(cSize, targetConfig.chunkHeight, cSize);
                Bounds chunkBounds = new Bounds(center, size);

                // Espandiamo i confini virtuali del chunk per attivarlo in anticipo ed eliminare il pop-in
                chunkBounds.Expand(frustumPadding);
                isInsideFrustum = GeometryUtility.TestPlanesAABB(cameraPlanes, chunkBounds);
            }

            bool shouldBeActive = isCloseSafety || isInsideFrustum;
            if (kvp.Value.gameObject.activeSelf != shouldBeActive)
            {
                kvp.Value.gameObject.SetActive(shouldBeActive);
            }
        }
        
        RemoveFarChunks(new Vector2Int(pX, pZ));
    }

    /// <summary>
    /// Rimuove i chunk dalla memoria solo se escono rigorosamente dal quadrato di visione massimo (Chebyshev Distance).
    /// </summary>
    void RemoveFarChunks(Vector2Int playerChunkPos)
    {
        List<Vector2Int> coordsToRemove = new List<Vector2Int>();

        foreach (var chunk in activeChunks)
        {
            // Calcoliamo la distanza a griglia quadrata invece che sferica
            int distX = Mathf.Abs(chunk.Key.x - playerChunkPos.x);
            int distZ = Mathf.Abs(chunk.Key.y - playerChunkPos.y);

            // Il chunk viene rimosso solo se supera il raggio del quadrato di generazione (+ 1 di tolleranza per lo shifting)
            if (distX > viewDistance + 1 || distZ > viewDistance + 1)
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

    void SpawnLayoutObjects(Chunk chunk, Vector3 worldPos, BiomeConfig cfg)
    {
        var allNames = new List<string>(_readyPrefabs.Keys);
        allNames.AddRange(_pendingObjectNames);
        if (allNames.Count == 0) return;

        int w = cfg.chunkWidth;
        int bx = Mathf.RoundToInt(worldPos.x);
        int bz = Mathf.RoundToInt(worldPos.z);

        bool[,] excluded = new bool[w, w];
        List<StructureAnchor> placedStructures = new List<StructureAnchor>();

        for (int currentPriority = 1; currentPriority <= 4; currentPriority++)
        {
            var passObjects = allNames.Where(n => {
                int pr = _objectPriorities.TryGetValue(n, out int pVal) ? pVal : 3;
                return pr == currentPriority;
            }).ToList();

            if (passObjects.Count == 0) continue;

            foreach (string name in passObjects)
            {
                float fc = _objectFreqChances.TryGetValue(name, out float f) ? f : 0.5f;
                bool isUnique = fc < 0f;

                if (currentPriority == 2 && placedStructures.Count > 0)
                {
                    foreach (var structure in placedStructures)
                    {
                        int propSeed = PlacementHash(Mathf.RoundToInt(structure.position.x), Mathf.RoundToInt(structure.position.z), name.GetHashCode());
                        float spawnRoll = (propSeed & 0xFFFF) / 65535f;
                        
                        if (!isUnique && spawnRoll > fc) continue;
                        if (isUnique && _uniqueSpawned.Contains(name)) continue;

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
                            excluded[plx, plz] = true; 
                            
                            if (isUnique) _uniqueSpawned.Add(name);
                        }
                    }
                    continue; 
                }

                int cellSize = isUnique ? cellUnique : (fc <= 0.03f ? cellSuperRare : (fc <= 0.10f ? cellRare : (fc <= 0.50f ? cellCommon : cellVeryCommon)));

                int gcx0 = Mathf.FloorToInt((float)bx / cellSize);
                int gcz0 = Mathf.FloorToInt((float)bz / cellSize);
                int gcx1 = Mathf.FloorToInt((float)(bx + w - 1) / cellSize);
                int gcz1 = Mathf.FloorToInt((float)(bz + w - 1) / cellSize);

                for (int gcx = gcx0; gcx <= gcx1; gcx++)
                for (int gcz = gcz0; gcz <= gcz1; gcz++)
                {
                    if (isUnique && _uniqueSpawned.Contains(name)) continue;

                    int cellHash = PlacementHash(gcx, gcz, name.GetHashCode());
                    
                    float spawnRoll = (cellHash & 0xFFFF) / 65535f;
                    if (!isUnique && spawnRoll > fc) continue;

                    float jx = ((cellHash & 0xFF) / 255f) * cellSize;
                    float jz = (((cellHash >> 8) & 0xFF) / 255f) * cellSize;

                    float wx = gcx * cellSize + jx;
                    float wz = gcz * cellSize + jz;

                    int lx = Mathf.FloorToInt(wx - bx);
                    int lz = Mathf.FloorToInt(wz - bz);

                    if (lx < 0 || lx >= w || lz < 0 || lz >= w) continue;
                    if (excluded[lx, lz]) continue; 

                    float layoutVal = chunk.LayoutMap[lx, lz];
                    
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

                    int footRadius = Mathf.CeilToInt(scaleFactor * 0.5f);
                    for (int dx = -footRadius; dx <= footRadius; dx++)
                    for (int dz = -footRadius; dz <= footRadius; dz++)
                    {
                        int nx = lx + dx, nz = lz + dz;
                        if ((uint)nx < (uint)w && (uint)nz < (uint)w) excluded[nx, nz] = true;
                    }

                    if (isUnique) _uniqueSpawned.Add(name);

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
    }

    void InstantiateAssetPrimitive(string name, float wx, float groundY, float wz, float scaleFactor, Transform parent, float rotZ = 0f)
    {
        GameObject obj = null;
        float maxDist = GetRenderDistance(name);

        if (_readyPrefabs.TryGetValue(name, out var prefab) && prefab != null)
        {
            obj = Instantiate(prefab, new Vector3(wx, groundY, wz), Quaternion.Euler(-90f, 0f, rotZ), parent);
            obj.transform.localScale = Vector3.one * scaleFactor;
            var rend = obj.GetComponentInChildren<Renderer>();
            if (rend != null)
                obj.transform.position += Vector3.up * (groundY - rend.bounds.min.y + objectGroundOffset);
        }
        else if (VoxelRenderer.RuntimeMeshCache.TryGetValue(name, out Mesh runtimeMesh))
        {
            obj = new GameObject(name);
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
            return;
        }

        // Iniettiamo il culler se l'oggetto 3D reale è stato creato con successo
        if (obj != null)
        {
            obj.AddComponent<ObjectDistanceCuller>().Initialize(player, maxDist);
        }
    }

    float GetRenderDistance(string assetName)
    {
        if (!_objectSizes.TryGetValue(assetName, out string size)) size = "big";
        return size switch
        {
            "huge" => renderDistHuge,
            "big" => renderDistBig,
            "normal" => renderDistNormal,
            "small" => renderDistSmall,
            "tiny" => renderDistTiny,
            _ => renderDistBig
        };
    }

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

    void HandlePrefabSaved(string name, GameObject prefab)
    {
        _readyPrefabs[name] = prefab;
        ReplacePlaceholders(name);
    }

    public void ReplacePlaceholders(string objectName)
    {
        if (!_readyPrefabs.TryGetValue(objectName, out var prefab))
        {
#if UNITY_EDITOR
            string prefabPath = $"Assets/GeneratedAsset/{objectName}.prefab";
            AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceSynchronousImport);
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab != null) _readyPrefabs[objectName] = prefab;
#endif
        }

        _pendingObjectNames.Remove(objectName);
        float scaleFactor = _objectScaleFactors.TryGetValue(objectName, out float sf) ? sf : scaleDefault;
        float maxDist = GetRenderDistance(objectName);

        if (!_placeholders.TryGetValue(objectName, out var list)) return;

        var toSpawn = new List<(Vector3 pos, Transform parent)>();
        foreach (var p in list)
        {
            if (p != null) toSpawn.Add((p.transform.position, p.transform.parent));
        }

        foreach (var p in list)
        {
            if (p != null) Destroy(p.gameObject);
        }
        _placeholders.Remove(objectName);

        foreach (var (pos, parent) in toSpawn)
        {
            float groundY = pos.y - scaleFactor * 0.5f;
            int posHash = PlacementHash(Mathf.RoundToInt(pos.x), Mathf.RoundToInt(pos.z), objectName.GetHashCode());
            float rotZ = ((posHash >> 16) & 0xFF) / 255f * 360f;
            GameObject obj = null;

            if (VoxelRenderer.RuntimeMeshCache.TryGetValue(objectName, out Mesh runtimeMesh))
            {
                obj = new GameObject(objectName);
                obj.transform.SetParent(parent);
                obj.transform.position = new Vector3(pos.x, groundY, pos.z);
                obj.transform.localRotation = Quaternion.Euler(-90f, 0f, rotZ);

                var mf = obj.AddComponent<MeshFilter>();
                mf.sharedMesh = runtimeMesh;

                var mr = obj.AddComponent<MeshRenderer>();
                mr.sharedMaterial = VoxelRenderer.RuntimeMaterialInstance;

                var mc = obj.AddComponent<MeshCollider>();
                mc.sharedMesh = runtimeMesh;
            }
            else if (prefab != null)
            {
                obj = Instantiate(prefab, new Vector3(pos.x, groundY, pos.z), Quaternion.Euler(-90f, 0f, rotZ), parent);
            }
            else
            {
                continue;
            }

            obj.transform.localScale = Vector3.one * scaleFactor;

            var rend = obj.GetComponentInChildren<Renderer>();
            if (rend != null)
            {
                obj.transform.position += Vector3.up * (groundY - rend.bounds.min.y + objectGroundOffset);
            }

            // Iniettiamo il culler anche in fase di rimpiazzo runtime dei billboard
            if (obj != null)
            {
                obj.AddComponent<ObjectDistanceCuller>().Initialize(player, maxDist);
            }
        }
    }

    public void ReloadBiomePrefabs()
    {
#if UNITY_EDITOR
        AssetDatabase.Refresh();
#endif
        LoadBiomePrefabs();
    }

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

    void LoadBiomePrefabs()
    {
        _readyPrefabs.Clear();
        _pendingObjectNames.Clear();
        _objectScaleFactors.Clear();
        _objectFreqChances.Clear();
        _objectTypes.Clear();       
        _objectPriorities.Clear();  
        _objectSizes.Clear(); // Pulizia dizionario dimensioni
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
                string typeStr = "vegetation";
                string sizeStr = "big";
                int priorityVal = 3;

                if (entry.Value?.Type == JTokenType.Object)
                {
                    sizeStr = entry.Value["size"]?.ToString() ?? "big";
                    scaleF = SizeToScale(sizeStr);
                    freqC = FreqToChance(entry.Value["frequency"]?.ToString() ?? "common");
                    typeStr = entry.Value["type"]?.ToString() ?? "vegetation";
                    priorityVal = entry.Value["priority"]?.Value<int>() ?? 3;
                }
                
                _objectScaleFactors[name] = scaleF;
                _objectFreqChances[name] = freqC;
                _objectTypes[name] = typeStr;
                _objectPriorities[name] = priorityVal;
                _objectSizes[name] = sizeStr; // Registriamo il tag stringa

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
}