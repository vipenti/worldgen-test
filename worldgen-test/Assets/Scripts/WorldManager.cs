using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using TMPro;

#if UNITY_EDITOR
using UnityEditor;
#endif

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
    [Tooltip("Moltiplicatore di velocità per l'avanzamento della transizione basata sul cammino.")]
    public float transitionSpeedMultiplier = 1.0f;
    [Tooltip("Lunghezza in chunk del cammino necessario a completare la transizione globale.")]
    public int transitionLengthInChunks = 8;
    [Tooltip("Cuscinetto extra (in metri) oltre il cono visivo per attivare i chunk in anticipo.")]
    public float frustumPadding = 24f;

    [Header("Per-Size Rendering Distances (LOD)")]
    public float renderDistHuge = 250f;
    public float renderDistBig = 90f;
    public float renderDistNormal = 60f;
    public float renderDistSmall = 35f;
    public float renderDistTiny = 15f;

    [Header("LOD Distance Thresholds")]
    [Range(0f, 1f)] public float lodHighPercent = 0.25f;
    [Range(0f, 1f)] public float lodMediumPercent = 0.50f;

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
    public int cellVeryCommon = 3;
    public int cellCommon = 5;
    public int cellRare = 10;
    public int cellSuperRare = 20;
    public int cellUnique = 32;
    public int uniqueExclusionRadius = 12;

    // FLAG DI SICUREZZA: Blocca gli aggiornamenti grafici del mondo durante la generazione background
    [HideInInspector] public bool holdChunkUpdates = false;

    private Dictionary<string, GameObject> _readyPrefabs = new Dictionary<string, GameObject>();
    private List<string> _pendingObjectNames = new List<string>();
    private Dictionary<string, List<BillboardPlaceholder>> _placeholders = new Dictionary<string, List<BillboardPlaceholder>>();
    private Dictionary<string, float> _objectScaleFactors = new Dictionary<string, float>();
    private Dictionary<string, float> _objectFreqChances = new Dictionary<string, float>();
    private Dictionary<string, string> _objectTypes = new Dictionary<string, string>();
    private Dictionary<string, int> _objectPriorities = new Dictionary<string, int>();
    private Dictionary<string, string> _objectSizes = new Dictionary<string, string>(); 
    private HashSet<string> _uniqueSpawned = new HashSet<string>();
    private Quaternion lastCameraRotation; 

    [Header("Debug & Map Visualization")]
    public MapVisualizer mapVisualizer;
    public bool skipInitialPlayerSpawn = false;
    public TMP_Text infoText;

    private Dictionary<Vector2Int, Chunk> activeChunks = new Dictionary<Vector2Int, Chunk>();
    private Vector2Int lastPlayerChunk;
    private BiomeConfig oldConfig;
    private BiomeConfig targetConfig;
    private bool isTransitioning = false;
    private Vector2 transitionStartPos2D;
    private Vector2 transitionDir2D;
    private Vector3 lastPlayerPositionForTransition;
    private float lastUpdatedProgress;

    private Dictionary<string, GameObject> _oldReadyPrefabs = new Dictionary<string, GameObject>();
    private List<string> _oldPendingObjectNames = new List<string>();
    private Dictionary<string, float> _oldObjectScaleFactors = new Dictionary<string, float>();
    private Dictionary<string, float> _oldObjectFreqChances = new Dictionary<string, float>();
    private Dictionary<string, string> _oldObjectTypes = new Dictionary<string, string>();
    private Dictionary<string, int> _oldObjectPriorities = new Dictionary<string, int>();
    private Dictionary<string, string> _oldObjectSizes = new Dictionary<string, string>(); 
    private Dictionary<Vector2Int, bool> chunkIsNewBiome = new Dictionary<Vector2Int, bool>();

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

        LoadAtlasTexture();
        
        GenerateTexture.OnGenerationComplete += _ => LoadAtlasTexture();
        VoxelRenderer.OnPrefabSaved += HandlePrefabSaved;
        LLMWorldDirector.OnGenerationProgress += HandleGenerationProgress;
        LLMWorldDirector.OnTerrainPipelineDone += HandleTerrainPipelineDone;
        LLMWorldDirector.OnGenerationFailed += HandleGenerationFailed;
        GenerateAsset.OnAssetProgress += HandleAssetProgress;
        GenerateAsset.OnCriticalAssetsReady += HandleCriticalAssetsReady;

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
        LLMWorldDirector.OnGenerationProgress -= HandleGenerationProgress;
        LLMWorldDirector.OnTerrainPipelineDone -= HandleTerrainPipelineDone;
        LLMWorldDirector.OnGenerationFailed -= HandleGenerationFailed;
        GenerateAsset.OnAssetProgress -= HandleAssetProgress;
        GenerateAsset.OnCriticalAssetsReady -= HandleCriticalAssetsReady;
    }

    void LoadAtlasTexture()
    {
        string albedoPath = Path.Combine(Application.dataPath, "GeneratedTexture", "TextureAtlas.png");
        if (File.Exists(albedoPath))
        {
            atlasTexture ??= new Texture2D(2, 2);
            atlasTexture.LoadImage(File.ReadAllBytes(albedoPath));
            sharedChunkMaterial?.SetTexture("_BaseMap", atlasTexture);
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
        // Se la pipeline sta pre-allocando gli asset in RAM, congeliamo l'aggiornamento dei chunk
        if (holdChunkUpdates) return;

        // Gestione transizione basata sul movimento del giocatore
        if (isTransitioning)
        {
            Vector3 currentPos = player.position;
            lastPlayerPositionForTransition = currentPos;

            // Calcola la distanza proiettata del giocatore lungo la direzione di transizione
            Vector2 playerPos2D = new Vector2(player.position.x, player.position.z);
            float projectedWalk = Vector2.Dot(playerPos2D - transitionStartPos2D, transitionDir2D);
            float totalDist = transitionLengthInChunks * targetConfig.chunkWidth;
            float playerProgress = Mathf.Clamp01(projectedWalk / totalDist);

            // Aggiorna la transizione e il testo se il progresso è avanzato dello 0.5%
            // o se il giocatore ha completato la transizione
            if (playerProgress - lastUpdatedProgress >= 0.005f || projectedWalk >= totalDist)
            {
                lastUpdatedProgress = playerProgress;
                UpdateTransitionProgress();
            }
        }

        int pX = Mathf.FloorToInt(player.position.x / targetConfig.chunkWidth);
        int pZ = Mathf.FloorToInt(player.position.z / targetConfig.chunkWidth);
        Vector2Int currentPlayerChunk = new Vector2Int(pX, pZ);
        
        Quaternion currentCamRot = Camera.main != null ? Camera.main.transform.rotation : player.rotation;

        if (currentPlayerChunk != lastPlayerChunk || Quaternion.Angle(currentCamRot, lastCameraRotation) > 4f)
        {
            UpdateChunks();
            lastPlayerChunk = currentPlayerChunk;
            lastCameraRotation = currentCamRot;
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

    void UpdateChunks()
    {
        int pX = Mathf.FloorToInt(player.position.x / targetConfig.chunkWidth);
        int pZ = Mathf.FloorToInt(player.position.z / targetConfig.chunkWidth);
        float cSize = targetConfig.chunkWidth;

        Plane[] cameraPlanes = Camera.main != null ? GeometryUtility.CalculateFrustumPlanes(Camera.main) : null;

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

        foreach (var kvp in activeChunks)
        {
            if (kvp.Value == null) continue;

            Vector2Int chunkCoord = kvp.Key;
            int distX = Mathf.Abs(chunkCoord.x - pX);
            int distZ = Mathf.Abs(chunkCoord.y - pZ);
            if (distX > viewDistance || distZ > viewDistance) continue;

            bool isCloseSafety = distX <= 1 && distZ <= 1;
            kvp.Value.EnsureColliderState(isCloseSafety);

            bool isInsideFrustum = false;

            if (!isCloseSafety && cameraPlanes != null)
            {
                Vector3 center = new Vector3(chunkCoord.x * cSize + cSize * 0.5f, targetConfig.chunkHeight * 0.5f, chunkCoord.y * cSize + cSize * 0.5f);
                Vector3 size = new Vector3(cSize, targetConfig.chunkHeight, cSize);
                Bounds chunkBounds = new Bounds(center, size);
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

    void RemoveFarChunks(Vector2Int playerChunkPos)
    {
        List<Vector2Int> coordsToRemove = new List<Vector2Int>();
        foreach (var chunk in activeChunks)
        {
            int distX = Mathf.Abs(chunk.Key.x - playerChunkPos.x);
            int distZ = Mathf.Abs(chunk.Key.y - playerChunkPos.y);
            if (distX > viewDistance + 1 || distZ > viewDistance + 1)
                coordsToRemove.Add(chunk.Key);
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

    float GetChunkTransitionT(Vector2Int coord)
    {
        if (!isTransitioning) return 1f;

        Vector2 chunkWorldPos = new Vector2(
            coord.x * targetConfig.chunkWidth + targetConfig.chunkWidth * 0.5f,
            coord.y * targetConfig.chunkWidth + targetConfig.chunkWidth * 0.5f
        );

        float startOffset = viewDistance * targetConfig.chunkWidth;
        Vector2 startPos = transitionStartPos2D + transitionDir2D * startOffset;

        float projectedDist = Vector2.Dot(chunkWorldPos - startPos, transitionDir2D);
        float totalDist = transitionLengthInChunks * targetConfig.chunkWidth;
        return Mathf.Clamp01(projectedDist / totalDist);
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

            int pX = Mathf.FloorToInt(player.position.x / targetConfig.chunkWidth);
            int pZ = Mathf.FloorToInt(player.position.z / targetConfig.chunkWidth);
            bool closeToPlayer = Mathf.Abs(coord.x - pX) <= 1 && Mathf.Abs(coord.y - pZ) <= 1;

            float t = GetChunkTransitionT(coord);

            chunkScript.Initialize(
                coord, 
                oldConfig, 
                targetConfig, 
                isTransitioning, 
                transitionStartPos2D + transitionDir2D * (viewDistance * targetConfig.chunkWidth), 
                transitionDir2D, 
                transitionLengthInChunks * targetConfig.chunkWidth,
                pathWidth * 0.5f, 
                pathBlendWidth, 
                pathLevel, 
                closeToPlayer
            );
            activeChunks.Add(coord, chunkScript);

            if (isTransitioning)
            {
                if (t <= 0f)
                {
                    SpawnOldLayoutObjects(chunkScript, worldPos, oldConfig);
                    chunkIsNewBiome[coord] = false;
                }
                else if (t >= 1f)
                {
                    SpawnLayoutObjects(chunkScript, worldPos, targetConfig);
                    chunkIsNewBiome[coord] = true;
                }
                else
                {
                    // No assets spawned in the transition corridor
                    chunkIsNewBiome[coord] = false;
                }
            }
            else
            {
                SpawnLayoutObjects(chunkScript, worldPos, targetConfig);
                chunkIsNewBiome[coord] = true;
            }
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
                            position = new Vector3(wx, groundY, wz), assetName = name, scale = scaleFactor
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

        if (VoxelRenderer.RuntimeMeshCache.TryGetValue(name, out Mesh runtimeMesh) && runtimeMesh != null)
        {
            obj = new GameObject(name);
            obj.transform.SetParent(parent);
            obj.transform.position = new Vector3(wx, groundY, wz);
            obj.transform.localRotation = Quaternion.Euler(-90f, 0f, rotZ);

            obj.AddComponent<MeshFilter>().sharedMesh = runtimeMesh;
            obj.AddComponent<MeshRenderer>().sharedMaterial = VoxelRenderer.RuntimeMaterialInstance;

            if (!_objectSizes.TryGetValue(name, out string sizeStr)) sizeStr = "big";
            if (sizeStr == "huge" || sizeStr == "big")
            {
                obj.AddComponent<MeshCollider>().sharedMesh = runtimeMesh;
            }
        }
        else if (_readyPrefabs.TryGetValue(name, out var prefab) && prefab != null)
        {
            obj = Instantiate(prefab, new Vector3(wx, groundY, wz), Quaternion.Euler(-90f, 0f, rotZ), parent);
        }
        else
        {
            SpawnBillboard(name, new Vector3(wx, groundY + scaleFactor * 0.5f, wz), parent, scaleFactor, visible: false);
            return;
        }

        if (obj != null)
        {
            obj.transform.localScale = Vector3.one * scaleFactor;
            if (!_objectSizes.TryGetValue(name, out string sizeStr)) sizeStr = "big";
            float currentOffset = (sizeStr == "huge" || sizeStr == "big") ? objectGroundOffset : 0f;
            float calculatedMinY = GetObjectMinY(obj, groundY);
            obj.transform.position += Vector3.up * (groundY - calculatedMinY + currentOffset);

            obj.AddComponent<ObjectDistanceCuller>().Initialize(player, maxDist, name, lodHighPercent, lodMediumPercent);
        }
    }

    float GetRenderDistance(string assetName)
    {
        if (!_objectSizes.TryGetValue(assetName, out string size)) size = "big";
        return size switch {
            "huge" => renderDistHuge, "big" => renderDistBig, "normal" => renderDistNormal,
            "small" => renderDistSmall, "tiny" => renderDistTiny, _ => renderDistBig
        };
    }

    static int PlacementHash(int x, int z, int seed)
    {
        unchecked {
            int h = x * 374761393 + z * 668265263 + seed;
            h ^= h >> 13; h *= 1274126177; return h ^ (h >> 16);
        }
    }

    float GetGroundY(float wx, float wz, int fallbackY)
    {
        var origin = new Vector3(wx, fallbackY + 10f, wz);
        if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 30f)) return hit.point.y;
        return fallbackY + 1.0f;
    }

    void SpawnBillboard(string objName, Vector3 pos, Transform parent, float scaleFactor = 1f, bool visible = true)
    {
        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.transform.SetParent(parent);
        quad.transform.position = pos;
        quad.transform.localScale = Vector3.one * scaleFactor;
        Destroy(quad.GetComponent<MeshCollider>());
        quad.GetComponent<MeshRenderer>().enabled = visible;

        var placeholder = quad.AddComponent<BillboardPlaceholder>();
        placeholder.objectName = objName;

        if (!_placeholders.ContainsKey(objName)) _placeholders[objName] = new List<BillboardPlaceholder>();
        _placeholders[objName].Add(placeholder);
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
        foreach (var p in list) if (p != null) toSpawn.Add((p.transform.position, p.transform.parent));
        foreach (var p in list) if (p != null) Destroy(p.gameObject);
        _placeholders.Remove(objectName);

        foreach (var (pos, parent) in toSpawn)
        {
            if (!_objectSizes.TryGetValue(objectName, out string sizeStr)) sizeStr = "big";

            float groundY = pos.y - scaleFactor * 0.5f;
            int posHash = PlacementHash(Mathf.RoundToInt(pos.x), Mathf.RoundToInt(pos.z), objectName.GetHashCode());
            float rotZ = ((posHash >> 16) & 0xFF) / 255f * 360f;
            GameObject obj = null;

            if (VoxelRenderer.RuntimeMeshCache.TryGetValue(objectName, out Mesh runtimeMesh) && runtimeMesh != null)
            {
                obj = new GameObject(objectName);
                obj.transform.SetParent(parent);
                obj.transform.position = new Vector3(pos.x, groundY, pos.z);
                obj.transform.localRotation = Quaternion.Euler(-90f, 0f, rotZ);

                obj.AddComponent<MeshFilter>().sharedMesh = runtimeMesh;
                obj.AddComponent<MeshRenderer>().sharedMaterial = VoxelRenderer.RuntimeMaterialInstance;

                if (sizeStr == "huge" || sizeStr == "big")
                {
                    obj.AddComponent<MeshCollider>().sharedMesh = runtimeMesh;
                }
            }
            else if (prefab != null)
            {
                obj = Instantiate(prefab, new Vector3(pos.x, groundY, pos.z), Quaternion.Euler(-90f, 0f, rotZ), parent);
            }
            else continue;

            obj.transform.localScale = Vector3.one * scaleFactor;
            float currentOffset = (sizeStr == "huge" || sizeStr == "big") ? objectGroundOffset : 0f;
            float calculatedMinY = GetObjectMinY(obj, groundY);
            obj.transform.position += Vector3.up * (groundY - calculatedMinY + currentOffset);

            obj.AddComponent<ObjectDistanceCuller>().Initialize(player, maxDist, objectName, lodHighPercent, lodMediumPercent);
        }
    }

    public void ReloadBiomePrefabs() => LoadBiomePrefabs();

    public void BackupOldBiome()
    {
        _oldReadyPrefabs = new Dictionary<string, GameObject>(_readyPrefabs);
        _oldPendingObjectNames = new List<string>(_pendingObjectNames);
        _oldObjectScaleFactors = new Dictionary<string, float>(_objectScaleFactors);
        _oldObjectFreqChances = new Dictionary<string, float>(_objectFreqChances);
        _oldObjectTypes = new Dictionary<string, string>(_objectTypes);
        _oldObjectPriorities = new Dictionary<string, int>(_objectPriorities);
        _oldObjectSizes = new Dictionary<string, string>(_objectSizes);
        oldConfig = targetConfig;
        Debug.Log("<color=orange>[WorldManager]</color> Backup del vecchio bioma eseguito con successo.");
    }

    // FIX TOTALE TRANSITION: Rimossa la condizione bloccante per garantire l'avvio dell'interpolazione
    public void ApplyNewConfig()
    {
        // Se per qualche motivo il backup non è ancora stato effettuato, lo eseguiamo come fallback di emergenza
        if (_oldReadyPrefabs == null || _oldReadyPrefabs.Count == 0)
        {
            BackupOldBiome();
        }

        targetConfig = LoadLatestConfig();
        isTransitioning = true;
        
        // Record horizontal start pos and forward direction of player
        transitionStartPos2D = new Vector2(player.position.x, player.position.z);
        
        // Forward direction of player or camera
        Vector3 forward = Camera.main != null ? Camera.main.transform.forward : player.forward;
        transitionDir2D = new Vector2(forward.x, forward.z).normalized;
        if (transitionDir2D.sqrMagnitude < 0.01f) transitionDir2D = Vector2.up; // Fallback

        lastPlayerPositionForTransition = player.position;
        lastUpdatedProgress = 0f;

        chunkIsNewBiome.Clear(); // Pulisce i tracciamenti dei biomi dei chunk per la nuova transizione

        Debug.Log("<color=orange>[WorldManager]</color> Interpolazione globale spaziale avviata correttamente.");
        mapVisualizer?.GenerateMaps(targetConfig);

        float startOffset = viewDistance * targetConfig.chunkWidth;
        float totalDist = transitionLengthInChunks * targetConfig.chunkWidth;
        float totalMeters = startOffset + totalDist;
        SetStatusText($"[Transition] Biome transition started!\nWalk {viewDistance} chunks (~{startOffset:F0}m) to reach the transition zone.\nOverall: 0% complete");
    }

    void UpdateTransitionProgress()
    {
        Vector2 playerPos2D = new Vector2(player.position.x, player.position.z);
        float projectedWalk = Vector2.Dot(playerPos2D - transitionStartPos2D, transitionDir2D);
        float totalDist = transitionLengthInChunks * targetConfig.chunkWidth;
        float startOffset = viewDistance * targetConfig.chunkWidth;
        float playerProgress = Mathf.Clamp01((projectedWalk - startOffset) / totalDist);

        if (projectedWalk >= startOffset + totalDist)
        {
            isTransitioning = false;
            oldConfig = targetConfig;
            RegenerateAllChunks();
            Debug.Log("<color=orange>[WorldManager]</color> Terrain transition completed successfully.");
            SetStatusText("[Transition] Biome transition completed successfully!");
            return;
        }

        int pX = Mathf.FloorToInt(player.position.x / targetConfig.chunkWidth);
        int pZ = Mathf.FloorToInt(player.position.z / targetConfig.chunkWidth);

        // Aggiorna la fisica/collisore solo dei chunk adiacenti se necessario, senza rigenerare meshes per transizione
        foreach (var kvp in activeChunks)
        {
            if (kvp.Value != null)
            {
                Vector2Int coord = kvp.Key;
                int distX = Mathf.Abs(coord.x - pX);
                int distZ = Mathf.Abs(coord.y - pZ);
                bool closeToPlayer = distX <= 1 && distZ <= 1;

                kvp.Value.EnsureColliderState(closeToPlayer);
            }
        }

        float metersRemaining = (startOffset + totalDist) - projectedWalk;
        float chunksRemaining = metersRemaining / targetConfig.chunkWidth;
        float percent = playerProgress * 100f;

        string statusMsg = "";
        if (projectedWalk < startOffset)
        {
            float metersToStart = startOffset - projectedWalk;
            float chunksToStart = metersToStart / targetConfig.chunkWidth;
            statusMsg = $"[Transition] Walking to transition zone: {chunksToStart:F1} chunks (~{metersToStart:F0}m) remaining.\n" +
                        $"No terrain changes or asset deletions in the current area.";
        }
        else
        {
            statusMsg = $"[Transition] Blending terrain: {percent:F0}% complete\n" +
                        $"Walk {chunksRemaining:F1} more chunks (~{metersRemaining:F0}m) to complete.\n" +
                        $"No 3D assets spawned in the transition corridor.";
        }
        
        SetStatusText(statusMsg);
    }

    void SpawnOldLayoutObjects(Chunk chunk, Vector3 worldPos, BiomeConfig cfg)
    {
        var tempPrefabs = _readyPrefabs;
        var tempPending = _pendingObjectNames;
        var tempScaleFactors = _objectScaleFactors;
        var tempFreqChances = _objectFreqChances;
        var tempTypes = _objectTypes;
        var tempPriorities = _objectPriorities;
        var tempSizes = _objectSizes;

        _readyPrefabs = _oldReadyPrefabs;
        _pendingObjectNames = _oldPendingObjectNames;
        _objectScaleFactors = _oldObjectScaleFactors;
        _objectFreqChances = _oldObjectFreqChances;
        _objectTypes = _oldObjectTypes;
        _objectPriorities = _oldObjectPriorities;
        _objectSizes = _oldObjectSizes;

        SpawnLayoutObjects(chunk, worldPos, cfg);

        _readyPrefabs = tempPrefabs;
        _pendingObjectNames = tempPending;
        _objectScaleFactors = tempScaleFactors;
        _objectFreqChances = tempFreqChances;
        _objectTypes = tempTypes;
        _objectPriorities = tempPriorities;
        _objectSizes = tempSizes;
    }

    void RespawnObjectsForChunk(Chunk chunk, BiomeConfig cfg, bool useNewBiome)
    {
        // Cattura e distruggi tutti i vecchi prefabbricati posizionati nel chunk
        List<GameObject> childrenToDestroy = new List<GameObject>();
        foreach (Transform child in chunk.transform)
        {
            childrenToDestroy.Add(child.gameObject);
        }
        foreach (var child in childrenToDestroy)
        {
            Destroy(child);
        }

        Vector3 worldPos = new Vector3(chunk.transform.position.x, 0, chunk.transform.position.z);
        if (isTransitioning)
        {
            if (useNewBiome)
            {
                SpawnLayoutObjects(chunk, worldPos, cfg);
            }
        }
        else
        {
            SpawnLayoutObjects(chunk, worldPos, cfg);
        }
    }

    void LoadBiomePrefabs()
    {
        _readyPrefabs.Clear(); _pendingObjectNames.Clear(); _objectScaleFactors.Clear();
        _objectFreqChances.Clear(); _objectTypes.Clear(); _objectPriorities.Clear();  
        _objectSizes.Clear(); _uniqueSpawned.Clear(); _placeholders.Clear();

        string path = Path.Combine(Application.dataPath, "Json", "current_objects.json");
        if (!File.Exists(path)) return;

        try
        {
            var root = JObject.Parse(File.ReadAllText(path));
            foreach (var entry in root)
            {
                string name = entry.Key;
                string sizeStr = entry.Value["size"]?.ToString() ?? "big";
                _objectScaleFactors[name] = SizeToScale(sizeStr);
                _objectFreqChances[name] = FreqToChance(entry.Value["frequency"]?.ToString() ?? "common");
                _objectTypes[name] = entry.Value["type"]?.ToString() ?? "vegetation";
                _objectPriorities[name] = entry.Value["priority"]?.Value<int>() ?? 3;
                _objectSizes[name] = sizeStr;

                string prefabPath = $"Assets/GeneratedAsset/{name}.prefab";
                string prefabDiskPath = Path.Combine(Application.dataPath, "GeneratedAsset", name + ".prefab");
#if UNITY_EDITOR
                if (File.Exists(prefabDiskPath))
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                    if (prefab != null) _readyPrefabs[name] = prefab;
                    else _pendingObjectNames.Add(name);
                }
                else _pendingObjectNames.Add(name);
#else
                _pendingObjectNames.Add(name);
#endif
            }
        }
        catch (System.Exception e) { Debug.LogError($"Parsing current_objects failed: {e.Message}"); }
    }
    
    float SizeToScale(string size) => size switch {
        "huge" => scaleHuge, "big" => scaleBig, "normal" => scaleNormal, "small" => scaleSmall, "tiny" => scaleTiny, _ => scaleDefault
    };

    float FreqToChance(string freq) => freq switch {
        "very_common" => 1.00f, "common" => 0.50f, "rare" => 0.10f, "super_rare" => 0.03f, "unique" => -1f, _ => 0.50f
    };

    public void RegenerateAllChunks()
    {
        foreach (var kvp in activeChunks) if (kvp.Value != null) Destroy(kvp.Value.gameObject);
        activeChunks.Clear();
        _uniqueSpawned.Clear();
        UpdateChunks();
    }

    public void SetStatusText(string text)
    {
        if (infoText != null)
        {
            infoText.text = text;
        }
    }

    void HandleGenerationProgress(string status, float progress)
    {
        SetStatusText($"[Pipeline] {status} ({Mathf.RoundToInt(progress * 100)}%)");
    }

    void HandleTerrainPipelineDone()
    {
        SetStatusText("[Pipeline] World design complete! Preparing assets...");
    }

    void HandleGenerationFailed(string reason)
    {
        SetStatusText($"[Pipeline Error] {reason}");
    }

    void HandleAssetProgress(int done, int total)
    {
        if (total <= 0) return;
        float percent = (float)done / total * 100f;
        SetStatusText($"[Assets] Creating 3D models: {done}/{total} ({percent:F0}%)");
    }

    void HandleCriticalAssetsReady()
    {
        SetStatusText("[Transition] Critical assets loaded! Interpolation starting.");
    }

    private float GetObjectMinY(GameObject obj, float fallbackY)
    {
        var meshFilters = obj.GetComponentsInChildren<MeshFilter>();
        if (meshFilters == null || meshFilters.Length == 0)
        {
            var rend = obj.GetComponentInChildren<Renderer>();
            if (rend != null) return rend.bounds.min.y;
            return fallbackY;
        }

        float minY = float.MaxValue;
        bool found = false;

        foreach (var mf in meshFilters)
        {
            if (mf == null || mf.sharedMesh == null) continue;
            Mesh mesh = mf.sharedMesh;
            Bounds localBounds = mesh.bounds;
            Vector3 center = localBounds.center;
            Vector3 extents = localBounds.extents;

            for (int x = -1; x <= 1; x += 2)
            {
                for (int y = -1; y <= 1; y += 2)
                {
                    for (int z = -1; z <= 1; z += 2)
                    {
                        Vector3 localCorner = center + new Vector3(x * extents.x, y * extents.y, z * extents.z);
                        Vector3 worldCorner = mf.transform.TransformPoint(localCorner);
                        if (worldCorner.y < minY)
                        {
                            minY = worldCorner.y;
                            found = true;
                        }
                    }
                }
            }
        }

        return found ? minY : fallbackY;
    }
}