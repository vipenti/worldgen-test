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

    [Header("Texture Atlas Settings")]
    public FilterMode atlasFilterMode = FilterMode.Bilinear;
    public bool enableAtlasMipmaps = true;
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

    [Header("Urban Layout (isStructuredBiome)")]
    [Tooltip("Side length (world units) of a buildable lot inside a city block.")]
    public int urbanBlockSize = 8;
    [Tooltip("Width (world units) of the minor street corridor separating adjacent lots.")]
    public int urbanStreetWidth = 3;
    [Tooltip("Width (world units) of a main avenue, inserted every urbanMajorRoadInterval lots instead of a minor street.")]
    public int urbanMajorStreetWidth = 10;
    [Tooltip("Number of lots between two main avenues along each axis (1 = every boundary is a main avenue).")]
    public int urbanMajorRoadInterval = 4;
    [Range(0f, 1f)]
    [Tooltip("Fraction of lots left empty as a small park/plaza instead of holding a building.")]
    public float urbanParkChance = 0.12f;

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
    public LogPanelUI logPanel;

    private Dictionary<Vector2Int, Chunk> activeChunks = new Dictionary<Vector2Int, Chunk>();
    private Vector2Int lastPlayerChunk;
    private BiomeConfig oldConfig;
    private BiomeConfig targetConfig;
    private bool isTransitioning = false;
    private bool isPlayerInTransitionHUD = false;
    // Centro (mondo, XZ) della transizione: il punto in cui si trovava il player quando gli asset
    // critici sono diventati pronti. Il blend è radiale (distanza dal centro), non più direzionale
    // — così qualunque direzione il player esplori dopo il trigger, prima o poi entra ed esce dalla
    // fascia di blend, invece di dipendere dal camminare dritto in un'unica direzione catturata
    // nell'istante del trigger.
    private Vector2 transitionCenter2D;
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

    // Bundle di SOLI RIFERIMENTI ai dati "oggetti disponibili" di un bioma — quello corrente (vivo,
    // mutato in continuazione man mano che gli asset finiscono di generarsi) o quello vecchio
    // (istantanea congelata da BackupOldBiome). Passato esplicitamente a SpawnLayoutObjects e a
    // tutta la catena sottostante (InstantiateAssetPrimitive, GetRenderDistance,
    // GetBuildingScaleForLot) invece di scambiare temporaneamente i campi di istanza
    // _readyPrefabs/_pendingObjectNames/ecc. con i loro equivalenti _old... — quello scambio, senza
    // un try/finally a protezione, lasciava lo stato globale permanentemente agganciato ai dati del
    // vecchio bioma se una qualsiasi eccezione veniva sollevata a metà del piazzamento di un chunk,
    // svuotando il mondo per il resto della sessione (anche su chunk mai visti prima). Con i dati
    // passati per parametro invece che scambiati sui campi, quella classe di bug è impossibile per
    // costruzione: non c'è più nessuno stato condiviso da corrompere.
    private struct BiomeObjectSet
    {
        public Dictionary<string, GameObject> readyPrefabs;
        public List<string> pendingObjectNames;
        public Dictionary<string, float> objectScaleFactors;
        public Dictionary<string, float> objectFreqChances;
        public Dictionary<string, string> objectTypes;
        public Dictionary<string, int> objectPriorities;
        public Dictionary<string, string> objectSizes;
    }

    private BiomeObjectSet CurrentObjectSet => new BiomeObjectSet
    {
        readyPrefabs = _readyPrefabs,
        pendingObjectNames = _pendingObjectNames,
        objectScaleFactors = _objectScaleFactors,
        objectFreqChances = _objectFreqChances,
        objectTypes = _objectTypes,
        objectPriorities = _objectPriorities,
        objectSizes = _objectSizes
    };

    private BiomeObjectSet OldObjectSet => new BiomeObjectSet
    {
        readyPrefabs = _oldReadyPrefabs,
        pendingObjectNames = _oldPendingObjectNames,
        objectScaleFactors = _oldObjectScaleFactors,
        objectFreqChances = _oldObjectFreqChances,
        objectTypes = _oldObjectTypes,
        objectPriorities = _oldObjectPriorities,
        objectSizes = _oldObjectSizes
    };

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
        InitializePersistentDirectory();

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
        string dir = PathSettings.GetTextureFolder();

        string albedoPath = Path.Combine(dir, "TextureAtlas.png");
        if (File.Exists(albedoPath))
        {
            atlasTexture ??= new Texture2D(2, 2, TextureFormat.RGBA32, enableAtlasMipmaps);
            atlasTexture.LoadImage(File.ReadAllBytes(albedoPath));
            atlasTexture.filterMode = atlasFilterMode;
            atlasTexture.wrapMode = TextureWrapMode.Clamp;
            atlasTexture.Apply(enableAtlasMipmaps);
            sharedChunkMaterial?.SetTexture("_BaseMap", atlasTexture);
        }

        // Remove the application of normal maps to the terrain atlas material
        sharedChunkMaterial?.SetTexture("_BumpMap", null);
        sharedChunkMaterial?.DisableKeyword("_NORMALMAP");
    }

    void OnValidate()
    {
        if (atlasTexture != null)
        {
            atlasTexture.filterMode = atlasFilterMode;
            atlasTexture.Apply(enableAtlasMipmaps);
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
        // Gestione transizione basata sul movimento del giocatore
        if (isPlayerInTransitionHUD)
        {
            Vector3 currentPos = player.position;
            lastPlayerPositionForTransition = currentPos;

            // Calcola la distanza radiale del giocatore dal centro della transizione (non più una
            // proiezione direzionale: il blend si espande verso l'esterno in ogni direzione).
            Vector2 playerPos2D = new Vector2(player.position.x, player.position.z);
            float distFromCenter = Vector2.Distance(playerPos2D, transitionCenter2D);
            float totalDist = transitionLengthInChunks * targetConfig.chunkWidth;
            float playerProgress = Mathf.Clamp01(distFromCenter / totalDist);

            // Aggiorna la transizione e il testo se il progresso è avanzato dello 0.5%
            // o se il giocatore ha completato la transizione
            if (playerProgress - lastUpdatedProgress >= 0.005f || distFromCenter >= totalDist)
            {
                lastUpdatedProgress = playerProgress;
                UpdateTransitionProgress();
            }
        }

        int pX = Mathf.FloorToInt(player.position.x / targetConfig.chunkWidth);
        int pZ = Mathf.FloorToInt(player.position.z / targetConfig.chunkWidth);
        Vector2Int currentPlayerChunk = new Vector2Int(pX, pZ);

        // Safety net anti-sprofondamento: il collider dei chunk viene normalmente (ri)attivato
        // solo dentro UpdateChunks(), che scatta solo al cambio di chunk/rotazione camera — c'era
        // quindi una finestra di uno o più frame in cui il player poteva già trovarsi fisicamente
        // dentro un chunk il cui collider non era ancora stato attivato. Questo garantisce ogni
        // frame, senza gating, che i 9 chunk attorno al player abbiano sempre il collider pronto.
        EnsureCloseChunkCollidersActive(pX, pZ);

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
        string path = Path.Combine(PathSettings.GetJsonFolder(), "llm_generated.json");
        if (File.Exists(path))
            return JsonUtility.FromJson<BiomeConfig>(File.ReadAllText(path));

        return new BiomeConfig {
            perlinScale = 0.1f, chunkHeight = 64, chunkWidth = 16,
            surfaceTileID = 1, middleTileID = 2, deepTileID = 3,
            liquidTileID = 13, waterLevel = 5,
            octaves = 4, ridgeFactor = 0f, warpStrength = 0.2f
        };
    }

    // Chiamato ogni frame da Update() senza alcun gating (a differenza di UpdateChunks). Attiva
    // solo (mai disattiva: quello resta gestito dal pass più costoso in UpdateChunks) il collider
    // dei chunk nella zona 3x3 attorno al player. Costa poco (9 lookup in dizionario, quasi sempre
    // no-op grazie al controllo hasColliderBuilt dentro EnsureColliderState) ma chiude la finestra
    // di race condition che causava lo sprofondamento nel terreno.
    void EnsureCloseChunkCollidersActive(int pX, int pZ)
    {
        for (int x = -1; x <= 1; x++)
        {
            for (int z = -1; z <= 1; z++)
            {
                if (activeChunks.TryGetValue(new Vector2Int(pX + x, pZ + z), out Chunk chunk) && chunk != null)
                    chunk.EnsureColliderState(true);
            }
        }
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

        // Blend radiale: quanto un chunk è "dentro" la nuova biome dipende solo dalla sua distanza
        // dal centro della transizione, non dalla direzione in cui il player stava guardando nel
        // momento del trigger. innerRadius = raggio già coperto dalla vista (nessun cambiamento
        // immediato lì, quei chunk sono già caricati); oltre, cresce linearmente fino a 1 su una
        // fascia larga transitionLengthInChunks chunk.
        float innerRadius = viewDistance * targetConfig.chunkWidth;
        float blendWidth = transitionLengthInChunks * targetConfig.chunkWidth;
        float dist = Vector2.Distance(chunkWorldPos, transitionCenter2D);
        return Mathf.Clamp01((dist - innerRadius) / blendWidth);
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
                transitionCenter2D,
                viewDistance * targetConfig.chunkWidth,
                transitionLengthInChunks * targetConfig.chunkWidth,
                pathWidth * 0.5f,
                pathBlendWidth,
                pathLevel,
                GetUrbanGridParams(),
                closeToPlayer
            );
            activeChunks.Add(coord, chunkScript);

            // Durante la generazione (prima che gli asset critici del nuovo bioma siano pronti)
            // isTransitioning è ancora false: i chunk vengono creati e decorati normalmente col
            // bioma correntemente attivo, che resta quello vecchio fino allo swap atomico fatto da
            // ApplyNewConfig() — nessuna finestra "mista" da gestire qui.
            if (isTransitioning)
            {
                // Niente più "corridoio vuoto": ogni chunk viene sempre decorato, scegliendo il
                // lato (vecchio/nuovo bioma) in base a quale parte del blend ricade — il terreno
                // sotto continua comunque a sfumare gradualmente via voxelT in Chunk.
                if (t < 0.5f)
                {
                    SpawnOldLayoutObjects(chunkScript, worldPos, oldConfig);
                    chunkIsNewBiome[coord] = false;
                }
                else
                {
                    SpawnLayoutObjects(chunkScript, worldPos, targetConfig, CurrentObjectSet);
                    chunkIsNewBiome[coord] = true;
                }
            }
            else
            {
                SpawnLayoutObjects(chunkScript, worldPos, targetConfig, CurrentObjectSet);
                chunkIsNewBiome[coord] = true;
            }
        }
    }

    void SpawnLayoutObjects(Chunk chunk, Vector3 worldPos, BiomeConfig cfg, BiomeObjectSet data)
    {
        // Settlements (city/town/village) get a grid/street layout instead of the organic
        // noise-scatter used below for natural biomes.
        if (cfg.isStructuredBiome)
        {
            SpawnStructuredLayoutObjects(chunk, worldPos, cfg, data);
            return;
        }

        var allNames = new List<string>(data.readyPrefabs.Keys);
        allNames.AddRange(data.pendingObjectNames);
        if (allNames.Count == 0) return;

        int w = cfg.chunkWidth;
        int bx = Mathf.RoundToInt(worldPos.x);
        int bz = Mathf.RoundToInt(worldPos.z);

        bool[,] excluded = new bool[w, w];
        List<StructureAnchor> placedStructures = new List<StructureAnchor>();

        for (int currentPriority = 1; currentPriority <= 4; currentPriority++)
        {
            var passObjects = allNames.Where(n => {
                int pr = data.objectPriorities.TryGetValue(n, out int pVal) ? pVal : 3;
                return pr == currentPriority;
            }).ToList();

            if (passObjects.Count == 0) continue;

            foreach (string name in passObjects)
            {
                float fc = data.objectFreqChances.TryGetValue(name, out float f) ? f : 0.5f;
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

                            float pScale = data.objectScaleFactors.TryGetValue(name, out float ps) ? ps : scaleSmall;
                            float pGroundY = GetGroundY(propWorldPos.x, propWorldPos.z, chunk.HeightMap[plx, plz]);

                            float rotZ = (propSeed >> 16 & 0xFF) / 255f * 360f;
                            InstantiateAssetPrimitive(name, propWorldPos.x, pGroundY, propWorldPos.z, pScale, chunk.transform, data, rotZ);
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

                    float scaleFactor = data.objectScaleFactors.TryGetValue(name, out float sf) ? sf : scaleDefault;
                    float groundY = GetGroundY(wx, wz, chunk.HeightMap[lx, lz]);

                    float rotZ = ((cellHash >> 16) & 0xFF) / 255f * 360f;
                    InstantiateAssetPrimitive(name, wx, groundY, wz, scaleFactor, chunk.transform, data, rotZ);

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

    // Grid/street layout used for settlements (isStructuredBiome == true). Buildings (priority 1)
    // are snapped to lot centers on a world-aligned grid of blocks separated by street corridors,
    // facing a cardinal direction instead of a random angle. Street-facing props (priority 2)
    // ring each building's sidewalk edge. A small fraction of lots become parks that keep the
    // organic scatter (priority 3/4) for greenery instead of holding a building.
    void SpawnStructuredLayoutObjects(Chunk chunk, Vector3 worldPos, BiomeConfig cfg, BiomeObjectSet data)
    {
        var allNames = new List<string>(data.readyPrefabs.Keys);
        allNames.AddRange(data.pendingObjectNames);
        if (allNames.Count == 0) return;

        int w = cfg.chunkWidth;
        int bx = Mathf.RoundToInt(worldPos.x);
        int bz = Mathf.RoundToInt(worldPos.z);

        UrbanGrid.Params gridParams = GetUrbanGridParams();
        bool[,] excluded = new bool[w, w];

        // Mark street cells (minor + main avenues) so nothing gets placed on the road.
        for (int x = 0; x < w; x++)
        for (int z = 0; z < w; z++)
        {
            var sx = UrbanGrid.SampleAxis(bx + x, gridParams);
            var sz = UrbanGrid.SampleAxis(bz + z, gridParams);
            if (sx.isStreet || sz.isStreet) excluded[x, z] = true;
        }

        var buildingNames = allNames.Where(n => (data.objectPriorities.TryGetValue(n, out int p) ? p : 3) == 1 && !LooksLikeGroundTile(n)).ToList();
        var streetPropNames = allNames.Where(n => (data.objectPriorities.TryGetValue(n, out int p) ? p : 3) == 2).ToList();
        var decorNames3 = allNames.Where(n => (data.objectPriorities.TryGetValue(n, out int p) ? p : 3) == 3).ToList();
        var decorNames4 = allNames.Where(n => (data.objectPriorities.TryGetValue(n, out int p) ? p : 3) == 4).ToList();

        if (buildingNames.Count == 0) return; // No real structures registered yet for this biome.

        var placedStructures = new List<StructureAnchor>();
        int blockSize = gridParams.blockSize;

        foreach (var (gcx, lotCenterX) in UrbanGrid.LotsInRange(bx, w, gridParams))
        foreach (var (gcz, lotCenterZ) in UrbanGrid.LotsInRange(bz, w, gridParams))
        {
            int lotHash = PlacementHash(gcx, gcz, 0x1337);
            bool isPark = ((lotHash & 0xFF) / 255f) < urbanParkChance;

            int lcx = Mathf.FloorToInt(lotCenterX - bx);
            int lcz = Mathf.FloorToInt(lotCenterZ - bz);
            if (lcx < 0 || lcx >= w || lcz < 0 || lcz >= w) continue; // Lot centered in a neighboring chunk.
            if (chunk.HeightMap[lcx, lcz] <= cfg.waterLevel) continue;

            // Park lots stay open (no building, no footprint exclusion) so the general decor
            // scatter below is free to fill them with trees/props like any other open ground.
            if (isPark) continue;

            string bName = buildingNames[Mathf.Abs(lotHash) % buildingNames.Count];
            float bScale = GetBuildingScaleForLot(bName, blockSize, data);
            float bGroundY = GetGroundY(lotCenterX, lotCenterZ, chunk.HeightMap[lcx, lcz]);
            float bRotZ = (Mathf.Abs(lotHash) % 4) * 90f; // Cardinal facing, not an arbitrary angle.

            InstantiateAssetPrimitive(bName, lotCenterX, bGroundY, lotCenterZ, bScale, chunk.transform, data, bRotZ);
            placedStructures.Add(new StructureAnchor {
                position = new Vector3(lotCenterX, bGroundY, lotCenterZ), assetName = bName, scale = bScale
            });

            int footRadius = Mathf.CeilToInt(blockSize * 0.5f);
            for (int dx = -footRadius; dx <= footRadius; dx++)
            for (int dz = -footRadius; dz <= footRadius; dz++)
            {
                int nx = lcx + dx, nz = lcz + dz;
                if ((uint)nx < (uint)w && (uint)nz < (uint)w) excluded[nx, nz] = true;
            }
        }

        // Street-facing props (lamps, benches, stalls...) placed at each building's curb.
        if (streetPropNames.Count > 0)
        {
            foreach (var structure in placedStructures)
            foreach (string name in streetPropNames)
            {
                float fc = data.objectFreqChances.TryGetValue(name, out float f) ? f : 0.5f;
                bool isUnique = fc < 0f;
                int propSeed = PlacementHash(Mathf.RoundToInt(structure.position.x), Mathf.RoundToInt(structure.position.z), name.GetHashCode());
                float spawnRoll = (propSeed & 0xFFFF) / 65535f;
                if (!isUnique && spawnRoll > fc) continue;
                if (isUnique && _uniqueSpawned.Contains(name)) continue;

                int side = Mathf.Abs(propSeed) % 4;
                float offsetDistance = (blockSize * 0.5f) + (urbanStreetWidth * 0.5f);
                Vector3 offsetDir = side switch { 0 => Vector3.forward, 1 => Vector3.right, 2 => Vector3.back, _ => Vector3.left };
                Vector3 propWorldPos = structure.position + offsetDir * offsetDistance;

                int plx = Mathf.FloorToInt(propWorldPos.x - bx);
                int plz = Mathf.FloorToInt(propWorldPos.z - bz);
                if (plx < 0 || plx >= w || plz < 0 || plz >= w) continue;
                if (excluded[plx, plz] || chunk.HeightMap[plx, plz] <= cfg.waterLevel) continue;

                float pScale = data.objectScaleFactors.TryGetValue(name, out float ps) ? ps : scaleSmall;
                float pGroundY = GetGroundY(propWorldPos.x, propWorldPos.z, chunk.HeightMap[plx, plz]);
                float rotZ = ((propSeed >> 16) & 0xFF) / 255f * 360f;

                InstantiateAssetPrimitive(name, propWorldPos.x, pGroundY, propWorldPos.z, pScale, chunk.transform, data, rotZ);
                excluded[plx, plz] = true;
                if (isUnique) _uniqueSpawned.Add(name);
            }
        }

        // General decor scatter (trees, benches, clutter — priority 3/4) fills whatever ground is
        // still free: park lots, sidewalks, building yards. Same jittered-grid algorithm as the
        // organic biome path, just constrained to cells that aren't a street or a building
        // footprint via the shared `excluded` mask.
        ScatterDecorObjects(chunk, worldPos, cfg, decorNames3, 3, excluded, data);
        ScatterDecorObjects(chunk, worldPos, cfg, decorNames4, 4, excluded, data);
    }

    // Jittered-grid scatter shared by the urban decor pass above and mirroring the organic
    // biome's priority 3/4 loop in the block below: one deterministic roll per grid cell (sized
    // by rarity), filtered by the chunk's LayoutMap band, skipping anything already excluded.
    void ScatterDecorObjects(Chunk chunk, Vector3 worldPos, BiomeConfig cfg, List<string> names, int priority, bool[,] excluded, BiomeObjectSet data)
    {
        if (names.Count == 0) return;

        int w = cfg.chunkWidth;
        int bx = Mathf.RoundToInt(worldPos.x);
        int bz = Mathf.RoundToInt(worldPos.z);
        float minLayout = priority == 4 ? 0.10f : 0.15f;
        float maxLayout = priority == 4 ? 0.90f : 0.85f;

        foreach (string name in names)
        {
            float fc = data.objectFreqChances.TryGetValue(name, out float f) ? f : 0.5f;
            bool isUnique = fc < 0f;
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
                if (layoutVal <= minLayout || layoutVal > maxLayout) continue;
                if (chunk.HeightMap[lx, lz] <= cfg.waterLevel) continue;

                float scaleFactor = data.objectScaleFactors.TryGetValue(name, out float sf) ? sf : scaleDefault;
                float groundY = GetGroundY(wx, wz, chunk.HeightMap[lx, lz]);
                float rotZ = ((cellHash >> 16) & 0xFF) / 255f * 360f;
                InstantiateAssetPrimitive(name, wx, groundY, wz, scaleFactor, chunk.transform, data, rotZ);

                int footRadius = Mathf.CeilToInt(scaleFactor * 0.5f);
                for (int dx = -footRadius; dx <= footRadius; dx++)
                for (int dz = -footRadius; dz <= footRadius; dz++)
                {
                    int nx = lx + dx, nz = lz + dz;
                    if ((uint)nx < (uint)w && (uint)nz < (uint)w) excluded[nx, nz] = true;
                }

                if (isUnique) _uniqueSpawned.Add(name);
            }
        }
    }

    // Effective urban grid parameters, shared by placement (here) and Chunk's terrain/texture
    // pass so lots and streets always agree. Lot size is never smaller than the biggest building
    // (scaleHuge) plus a small margin, regardless of what urbanBlockSize is set to in the
    // inspector — buildings are instantiated at scaleHuge/scaleBig/etc. (see
    // _objectScaleFactors), so a lot smaller than scaleHuge would let the biggest structures
    // overflow into the street or a neighboring lot.
    UrbanGrid.Params GetUrbanGridParams() => new UrbanGrid.Params {
        blockSize = Mathf.Max(urbanBlockSize, Mathf.CeilToInt(scaleHuge) + 2),
        minorStreetWidth = Mathf.Max(1, urbanStreetWidth),
        majorStreetWidth = Mathf.Max(urbanStreetWidth, urbanMajorStreetWidth),
        majorInterval = Mathf.Max(1, urbanMajorRoadInterval)
    };

    // Caps a building's placement scale so it physically fits its lot, instead of blindly
    // applying the size-category multiplier (e.g. scaleHuge=15). Voxel meshes are built at
    // 1 world unit per voxel with no normalization (see VoxelRenderer.BuildOptimizedMesh) — a
    // richly detailed "huge" building can already be dozens of units across natively, so
    // multiplying that by 15 can produce a structure large enough to bury several lots and the
    // streets between them. Falls back to the plain category scale when the mesh isn't cached
    // yet (e.g. still pending generation), same as before this fix.
    float GetBuildingScaleForLot(string name, int blockSize, BiomeObjectSet data)
    {
        float categoryScale = data.objectScaleFactors.TryGetValue(name, out float sf) ? sf : scaleBig;

        EnsureMeshCached(name);
        Mesh mesh = GetBestAvailableMesh(name);
        if (mesh == null)
            return categoryScale;

        // Use the largest of the three local-space dimensions as a rotation-agnostic, worst-case
        // footprint estimate (the -90° X rotation applied at instantiation swaps which local axis
        // ends up horizontal, so no single axis pair is safe to assume).
        Vector3 size = mesh.bounds.size;
        float nativeSpan = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
        if (nativeSpan < 0.01f) return categoryScale;

        float maxFootprint = blockSize * 0.85f; // small margin so the building doesn't kiss the street
        float fittedScale = maxFootprint / nativeSpan;
        return Mathf.Min(categoryScale, fittedScale);
    }

    // Safety net against LLM mislabeling: the asset-list prompt explicitly forbids ground/
    // pavement tiles as assets, but a local model can still slip and tag one "structure"
    // (priority 1) — placing it at every lot would fill the city with flat tiles instead of
    // buildings. Keyword-filter those out of the building pool rather than trust the label alone.
    static readonly string[] GroundTileKeywords = { "tile", "slab", "pavement", "sidewalk", "road", "asphalt", "path" };

    static bool LooksLikeGroundTile(string name)
    {
        string n = name.ToLowerInvariant();
        foreach (var kw in GroundTileKeywords)
            if (n.Contains(kw)) return true;
        return false;
    }

    void InstantiateAssetPrimitive(string name, float wx, float groundY, float wz, float scaleFactor, Transform parent, BiomeObjectSet data, float rotZ = 0f)
    {
        GameObject obj = null;
        float maxDist = GetRenderDistance(name, data);

        EnsureMeshCached(name);

        Mesh runtimeMesh = GetBestAvailableMesh(name);
        if (runtimeMesh != null)
        {
            obj = new GameObject(name);
            obj.transform.SetParent(parent);
            obj.transform.position = new Vector3(wx, groundY, wz);
            obj.transform.localRotation = Quaternion.Euler(-90f, 0f, rotZ);

            obj.AddComponent<MeshFilter>().sharedMesh = runtimeMesh;
            obj.AddComponent<MeshRenderer>().sharedMaterial = VoxelRenderer.RuntimeMaterialInstance;
        }
        else if (data.readyPrefabs.TryGetValue(name, out var prefab) && prefab != null)
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
            if (!data.objectSizes.TryGetValue(name, out string sizeStr)) sizeStr = "big";
            float currentOffset = (sizeStr == "huge" || sizeStr == "big") ? objectGroundOffset : 0f;
            float calculatedMinY = GetObjectMinY(obj, groundY);
            obj.transform.position += Vector3.up * (groundY - calculatedMinY + currentOffset);

            ApplySizeCollider(obj, sizeStr, name);
            obj.AddComponent<ObjectDistanceCuller>().Initialize(player, maxDist, name, lodHighPercent, lodMediumPercent, useOriginalMeshForHigh: sizeStr == "huge");
        }
    }

    // Assegna il collider giusto in base alla size, indipendentemente dal path di spawn
    // (mesh generata a runtime o prefab già pronto). tiny/small restano senza collider
    // (troppo numerosi/piccoli per giustificare il costo fisico). normal/big/huge ricevono
    // un BoxCollider approssimato sui bounds della mesh invece del MeshCollider completo,
    // molto più leggero da valutare e senza bisogno di ribake ad ogni cambio LOD. Rimuove
    // anche un eventuale MeshCollider ereditato da un prefab generato prima di questa modifica.
    void ApplySizeCollider(GameObject obj, string sizeStr, string assetName)
    {
        // big/huge (alberi, edifici, strutture irregolari): il BoxCollider era troppo approssimato
        // — il bounding box include tutto lo spazio vuoto tra i rami/sporgenze e impediva di
        // passare tra gli alberi. Torniamo al MeshCollider, più preciso (nessun Rigidbody non
        // kinematico su questi oggetti quindi non serve marcarlo convex).
        // normal (fontane, carretti, oggetti più semplici/compatti): resta il BoxCollider, più
        // economico e comunque una buona approssimazione per forme meno irregolari.
        // tiny/small: nessun collider.
        if (sizeStr == "huge" || sizeStr == "big")
        {
            if (obj.GetComponent<BoxCollider>() is BoxCollider oldBox) Destroy(oldBox);
            if (obj.GetComponent<MeshCollider>() != null) return; // già presente

            // Bake fisico SEMPRE dalla tier "_high" a risoluzione capped (mai dalla mesh originale
            // non decimata usata come LOD visivo più vicino per gli huge — vedi
            // ObjectDistanceCuller.useOriginalMeshForHigh — né da qualunque LOD sia visivamente
            // attivo in questo istante). Il collider non ha bisogno della stessa fedeltà del
            // rendering, e viene bakato una volta sola qui: ObjectDistanceCuller non lo tocca più
            // ai cambi di LOD, quindi niente più ribake fisico sincrono ogni volta che il player si
            // avvicina/allontana da un edificio.
            Mesh colliderMesh = null;
            if (!string.IsNullOrEmpty(assetName) && VoxelRenderer.RuntimeMeshCache.TryGetValue(assetName + "_high", out Mesh highMesh) && highMesh != null)
                colliderMesh = highMesh;
            else
            {
                var fallbackMf = obj.GetComponent<MeshFilter>();
                colliderMesh = fallbackMf != null ? fallbackMf.sharedMesh : null;
            }
            if (colliderMesh == null) return;

            obj.AddComponent<MeshCollider>().sharedMesh = colliderMesh;
            return;
        }

        var oldMc = obj.GetComponent<MeshCollider>();
        if (oldMc != null) Destroy(oldMc);

        if (sizeStr != "normal") return;
        // Nota: Destroy() è differito a fine frame, quindi subito dopo il vecchio MeshCollider
        // esiste ancora — controlliamo solo se un BoxCollider (quello che aggiungiamo noi) è già
        // presente, non un generico Collider, altrimenti il MeshCollider "in coda per la distruzione"
        // farebbe uscire la funzione prima di aggiungere il BoxCollider e l'oggetto resterebbe senza.
        if (obj.GetComponent<BoxCollider>() != null) return; // già presente

        var mf2 = obj.GetComponent<MeshFilter>();
        if (mf2 == null || mf2.sharedMesh == null) return;

        var box = obj.AddComponent<BoxCollider>();
        box.center = mf2.sharedMesh.bounds.center;
        box.size = mf2.sharedMesh.bounds.size;
    }

    float GetRenderDistance(string assetName, BiomeObjectSet data)
    {
        if (!data.objectSizes.TryGetValue(assetName, out string size)) size = "big";
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

    // fallbackY è sempre chunk.HeightMap[lx,lz] — l'altezza esatta della colonna di superficie,
    // già calcolata analiticamente in Chunk.GenerateMapDataInterpolated (stessa fonte usata da
    // GetSurfaceTileIdAtWorld per i passi). Prima qui c'era un Physics.Raycast per "ritrovarla" via
    // fisica: una query fisica in più per ogni singolo oggetto piazzato (decine per chunk), del
    // tutto ridondante dato che il valore corretto era già disponibile come parametro. wx/wz restano
    // nella firma per compatibilità con i chiamanti, anche se non servono più al calcolo.
    float GetGroundY(float wx, float wz, int fallbackY) => fallbackY + 1.0f;

    // Usato da FootstepAudio: trova il chunk sotto una posizione mondo (stessa formula di
    // Update()/CreateChunk per la conversione posizione→coordinata chunk) e risale dal tile ID
    // di superficie al tipo di suono passi ("grass"/"hard"/"soft"/"fluid"/"snow") tramite
    // TextureAtlasManager. Ritorna null se il chunk non è ancora caricato o il tile non è mappato.
    public string GetFootstepTypeAtWorldPos(Vector3 worldPos)
    {
        if (targetConfig == null || targetConfig.chunkWidth <= 0 || atlasManager == null) return null;

        int cx = Mathf.FloorToInt(worldPos.x / targetConfig.chunkWidth);
        int cz = Mathf.FloorToInt(worldPos.z / targetConfig.chunkWidth);
        if (!activeChunks.TryGetValue(new Vector2Int(cx, cz), out Chunk chunk) || chunk == null) return null;

        byte tileId = chunk.GetSurfaceTileIdAtWorld(worldPos.x, worldPos.z);
        if (tileId == 0) return null;

        return atlasManager.GetFootstepTypeForTileId(tileId);
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
        // In build VoxelRenderer invoca questo evento con prefab == null (non esiste un asset
        // .prefab fuori editor) solo per far scattare ReplacePlaceholders — lo spawn vero passa
        // per RuntimeMeshCache via GetBestAvailableMesh, non serve un prefab qui.
        if (prefab != null) _readyPrefabs[name] = prefab;
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
        float maxDist = GetRenderDistance(objectName, CurrentObjectSet);

        if (!_placeholders.TryGetValue(objectName, out var list)) return;

        var toSpawn = new List<(Vector3 pos, Transform parent)>();
        foreach (var p in list) if (p != null) toSpawn.Add((p.transform.position, p.transform.parent));
        foreach (var p in list) if (p != null) Destroy(p.gameObject);
        _placeholders.Remove(objectName);

        EnsureMeshCached(objectName);

        foreach (var (pos, parent) in toSpawn)
        {
            if (!_objectSizes.TryGetValue(objectName, out string sizeStr)) sizeStr = "big";

            float groundY = pos.y - scaleFactor * 0.5f;
            int posHash = PlacementHash(Mathf.RoundToInt(pos.x), Mathf.RoundToInt(pos.z), objectName.GetHashCode());
            float rotZ = ((posHash >> 16) & 0xFF) / 255f * 360f;
            GameObject obj = null;

            Mesh runtimeMesh = GetBestAvailableMesh(objectName);
            if (runtimeMesh != null)
            {
                obj = new GameObject(objectName);
                obj.transform.SetParent(parent);
                obj.transform.position = new Vector3(pos.x, groundY, pos.z);
                obj.transform.localRotation = Quaternion.Euler(-90f, 0f, rotZ);

                obj.AddComponent<MeshFilter>().sharedMesh = runtimeMesh;
                obj.AddComponent<MeshRenderer>().sharedMaterial = VoxelRenderer.RuntimeMaterialInstance;
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

            ApplySizeCollider(obj, sizeStr, objectName);
            obj.AddComponent<ObjectDistanceCuller>().Initialize(player, maxDist, objectName, lodHighPercent, lodMediumPercent, useOriginalMeshForHigh: sizeStr == "huge");
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

    // Swap atomico tra bioma attivo e bioma appena generato, eseguito UNA SOLA VOLTA quando gli
    // asset critici del nuovo bioma sono pronti (chiamato da LLMWorldDirector.TriggerTerrainTransition,
    // agganciato a GenerateAsset.OnCriticalAssetsReady). Prima di questo momento il mondo attivo
    // (_readyPrefabs/_pendingObjectNames/ecc., targetConfig) resta quello vecchio, invariato, per
    // tutta la durata della generazione in background — niente più stato "misto" da gestire chunk
    // per chunk mentre gli asset non sono ancora pronti (era la causa di lag e oggetti che
    // apparivano/sparivano vicino al giocatore).
    public void ApplyNewConfig()
    {
        // 1. Congela lo stato attivo corrente (ancora il vecchio bioma) in _old... PRIMA di
        //    ricaricare — l'ordine è essenziale: se lo scambiassimo dopo, il "vecchio" salvato
        //    sarebbe già il nuovo.
        BackupOldBiome();

        // 2. Ricarica _readyPrefabs/_pendingObjectNames/ecc. dal registro del nuovo bioma
        //    (current_objects.json, ormai completo) e il config del terreno.
        ReloadBiomePrefabs();
        targetConfig = LoadLatestConfig();

        isTransitioning = true;
        isPlayerInTransitionHUD = true;

        // Centro del blend radiale: la posizione del player nell'istante in cui gli asset critici
        // sono pronti. Non serve più registrare una direzione — il blend si espande verso l'esterno
        // in ogni direzione da questo punto, quindi qualunque percorso il player prenda dopo il
        // trigger raggiunge comunque il nuovo bioma.
        transitionCenter2D = new Vector2(player.position.x, player.position.z);

        lastPlayerPositionForTransition = player.position;
        lastUpdatedProgress = 0f;

        chunkIsNewBiome.Clear(); // Pulisce i tracciamenti dei biomi dei chunk per la nuova transizione

        Debug.Log("<color=orange>[WorldManager]</color> Interpolazione globale spaziale avviata correttamente.");
        mapVisualizer?.GenerateMaps(targetConfig);

        float startOffset = viewDistance * targetConfig.chunkWidth;
        float totalDist = transitionLengthInChunks * targetConfig.chunkWidth;
        float totalMeters = startOffset + totalDist;
        logPanel?.LogEvent($"[Transition] Biome transition started!\nWalk {viewDistance} chunks (~{startOffset:F0}m) to reach the transition zone.\nOverall: 0% complete");
    }

    void UpdateTransitionProgress()
    {
        Vector2 playerPos2D = new Vector2(player.position.x, player.position.z);
        float distFromCenter = Vector2.Distance(playerPos2D, transitionCenter2D);
        float totalDist = transitionLengthInChunks * targetConfig.chunkWidth;
        float startOffset = viewDistance * targetConfig.chunkWidth;
        float playerProgress = Mathf.Clamp01((distFromCenter - startOffset) / totalDist);

        if (distFromCenter >= startOffset + totalDist)
        {
            isPlayerInTransitionHUD = false;
            Debug.Log("<color=orange>[WorldManager]</color> Terrain transition completed successfully.");
            logPanel?.LogEvent("[Transition] Biome transition completed successfully!");
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

        float metersRemaining = (startOffset + totalDist) - distFromCenter;
        float chunksRemaining = metersRemaining / targetConfig.chunkWidth;
        float percent = playerProgress * 100f;

        string statusMsg = "";
        if (distFromCenter < startOffset)
        {
            float metersToStart = startOffset - distFromCenter;
            float chunksToStart = metersToStart / targetConfig.chunkWidth;
            statusMsg = $"[Transition] Walking to transition zone: {chunksToStart:F1} chunks (~{metersToStart:F0}m) remaining.\n" +
                        $"No terrain changes or asset deletions in the current area.";
        }
        else
        {
            statusMsg = $"[Transition] Blending terrain: {percent:F0}% complete\n" +
                        $"Walk {chunksRemaining:F1} more chunks (~{metersRemaining:F0}m) to complete.\n" +
                        $"Assets already blend to the new biome across the corridor.";
        }
        
        SetStatusText(statusMsg);
    }

    // Decora un chunk con gli oggetti del VECCHIO bioma (istantanea congelata da BackupOldBiome).
    // Passiamo semplicemente OldObjectSet a SpawnLayoutObjects invece di scambiare temporaneamente
    // i campi di istanza _readyPrefabs/_pendingObjectNames/ecc. con i loro equivalenti _old...: uno
    // scambio senza try/finally a protezione lasciava lo stato globale permanentemente corrotto se
    // una qualsiasi eccezione veniva sollevata a metà del piazzamento — vedi il commento su
    // BiomeObjectSet più sopra.
    void SpawnOldLayoutObjects(Chunk chunk, Vector3 worldPos, BiomeConfig cfg)
        => SpawnLayoutObjects(chunk, worldPos, cfg, OldObjectSet);

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
                SpawnLayoutObjects(chunk, worldPos, cfg, CurrentObjectSet);
            }
        }
        else
        {
            SpawnLayoutObjects(chunk, worldPos, cfg, CurrentObjectSet);
        }
    }

    void LoadBiomePrefabs()
    {
        _readyPrefabs.Clear(); _pendingObjectNames.Clear(); _objectScaleFactors.Clear();
        _objectFreqChances.Clear(); _objectTypes.Clear(); _objectPriorities.Clear();  
        _objectSizes.Clear(); _uniqueSpawned.Clear(); _placeholders.Clear();

        string path = Path.Combine(PathSettings.GetJsonFolder(), "current_objects.json");
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
        logPanel?.LogProgress(text);
    }

    void HandleGenerationProgress(string status, float progress)
    {
        SetStatusText($"[Pipeline] {status} ({Mathf.RoundToInt(progress * 100)}%)");
    }

    void HandleTerrainPipelineDone()
    {
        logPanel?.LogEvent("[Pipeline] World design complete! Preparing assets...");
    }

    void HandleGenerationFailed(string reason)
    {
        logPanel?.LogError($"[Pipeline Error] {reason}");
    }

    void HandleAssetProgress(int done, int total)
    {
        if (total <= 0) return;
        float percent = (float)done / total * 100f;
        SetStatusText($"[Assets] Creating 3D models: {done}/{total} ({percent:F0}%)");
    }

    void HandleCriticalAssetsReady()
    {
        logPanel?.LogEvent("[Transition] Critical assets loaded! Interpolation starting.");
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

    void InitializePersistentDirectory()
    {
        // Solo in build (fuori dall'Editor), o se vogliamo testarlo, copiamo le baseline se non esistono
#if !UNITY_EDITOR
        string targetJsonDir = PathSettings.GetJsonFolder();
        string targetTextureDir = PathSettings.GetTextureFolder();
        string targetObjectsDir = PathSettings.GetObjectsFolder();

        Directory.CreateDirectory(targetJsonDir);
        Directory.CreateDirectory(targetTextureDir);
        Directory.CreateDirectory(targetObjectsDir);

        // Copiamo i file JSON di configurazione baseline da Resources
        CopyResourceJsonToPersistent("Baseline/all_objects", Path.Combine(targetJsonDir, "all_objects.json"));
        CopyResourceJsonToPersistent("Baseline/current_objects", Path.Combine(targetJsonDir, "current_objects.json"));
        CopyResourceJsonToPersistent("Baseline/materials", Path.Combine(targetJsonDir, "materials.json"));
        CopyResourceJsonToPersistent("Baseline/llm_generated", Path.Combine(targetJsonDir, "llm_generated.json"));

        // Copiamo le texture dell'atlas baseline da Resources (come file binari .bytes per aggirare le restrizioni di lettura/scrittura)
        CopyResourceBinaryToPersistent("Baseline/TextureAtlas", Path.Combine(targetTextureDir, "TextureAtlas.png"));
        CopyResourceBinaryToPersistent("Baseline/TextureAtlas_Normal", Path.Combine(targetTextureDir, "TextureAtlas_Normal.png"));
#endif
    }

    void CopyResourceJsonToPersistent(string resourcePath, string targetDiskPath)
    {
        if (File.Exists(targetDiskPath)) return;

        var textAsset = Resources.Load<TextAsset>(resourcePath);
        if (textAsset != null)
        {
            File.WriteAllText(targetDiskPath, textAsset.text);
            Debug.Log($"[WorldManager] Inizializzato file JSON persistente: {targetDiskPath}");
        }
        else
        {
            Debug.LogWarning($"[WorldManager] File di risorsa baseline non trovato: {resourcePath}");
        }
    }

    void CopyResourceBinaryToPersistent(string resourcePath, string targetDiskPath)
    {
        if (File.Exists(targetDiskPath)) return;

        var binaryAsset = Resources.Load<TextAsset>(resourcePath);
        if (binaryAsset != null && binaryAsset.bytes != null)
        {
            File.WriteAllBytes(targetDiskPath, binaryAsset.bytes);
            Debug.Log($"[WorldManager] Inizializzato file binario persistente: {targetDiskPath}");
        }
        else
        {
            Debug.LogWarning($"[WorldManager] Risorsa binaria baseline non trovata: {resourcePath}");
        }
    }

    public void EnsureMeshCached(string assetName)
    {
        if (string.IsNullOrEmpty(assetName)) return;

        // Controlliamo OGNI tier singolarmente, non solo il base: per gli oggetti che hanno solo
        // le tre varianti LOD e mai un voxjson base (la maggioranza degli asset pre-esistenti, e
        // qualunque baseline di build impacchettata da BuildAssetPacker) RuntimeMeshCache[assetName]
        // (senza suffisso) non esiste MAI — quindi un controllo solo su quello ripeteva l'intero
        // lavoro sotto (compresa la riscrittura degli asset .prefab in Editor via SaveAsAssetForData)
        // ad OGNI singola chiamata invece che una volta sola, causando un loop di reimport continuo
        // dell'AssetDatabase e mandando in stallo l'Editor non appena ne venivano piazzate molte
        // istanze (es. un prop comune come "small rock" ripetuto in tanti chunk).
        bool hasBase = VoxelRenderer.RuntimeMeshCache.ContainsKey(assetName);
        bool hasHigh = VoxelRenderer.RuntimeMeshCache.ContainsKey(assetName + "_high");
        bool hasMedium = VoxelRenderer.RuntimeMeshCache.ContainsKey(assetName + "_medium");
        bool hasLow = VoxelRenderer.RuntimeMeshCache.ContainsKey(assetName + "_low");
        if (hasBase && hasHigh && hasMedium && hasLow) return;

        var renderer = GetComponentInChildren<VoxelRenderer>();
        if (renderer == null) renderer = FindFirstObjectByType<VoxelRenderer>();
        if (renderer == null) return;

        if (!hasBase)
        {
            string baseJson = LoadVoxJsonString(assetName);
            if (!string.IsNullOrEmpty(baseJson))
            {
                Debug.Log($"[WorldManager] Caricamento dinamico a runtime delle mesh per '{assetName}'...");
                renderer.LoadFromJson(baseJson, assetName);
            }
        }

        // Carichiamo le tier LOD mancanti indipendentemente dal fatto che il base esista: per gli
        // asset di cui esiste solo il pacchetto LOD (build baseline impacchettata da
        // BuildAssetPacker, o oggetti generati prima che venisse salvato anche il voxjson base) il
        // file base non c'è mai, ma vogliamo comunque poter renderizzare l'oggetto dalle sue tier.
        if (!hasHigh)
        {
            string highJson = LoadVoxJsonString(assetName + "_high");
            if (!string.IsNullOrEmpty(highJson)) renderer.LoadFromJson(highJson, assetName + "_high");
        }

        if (!hasMedium)
        {
            string medJson = LoadVoxJsonString(assetName + "_medium");
            if (!string.IsNullOrEmpty(medJson)) renderer.LoadFromJson(medJson, assetName + "_medium");
        }

        if (!hasLow)
        {
            string lowJson = LoadVoxJsonString(assetName + "_low");
            if (!string.IsNullOrEmpty(lowJson)) renderer.LoadFromJson(lowJson, assetName + "_low");
        }
    }

    // Ritorna la prima mesh disponibile in cache per un asset, provando prima il base poi le tier
    // LOD in ordine di dettaglio decrescente (_high, _medium, _low). Serve agli spawn-site
    // (InstantiateAssetPrimitive, ReplacePlaceholders) perché per molti asset — quelli della
    // baseline di build impacchettata da BuildAssetPacker, o generati prima del fix che salva anche
    // il voxjson base — la tier "base" senza suffisso non esiste mai, solo le tre LOD.
    Mesh GetBestAvailableMesh(string name)
    {
        if (VoxelRenderer.RuntimeMeshCache.TryGetValue(name, out Mesh baseMesh) && baseMesh != null) return baseMesh;
        foreach (string tier in new[] { "_high", "_medium", "_low" })
            if (VoxelRenderer.RuntimeMeshCache.TryGetValue(name + tier, out Mesh tierMesh) && tierMesh != null) return tierMesh;
        return null;
    }

    private string LoadVoxJsonString(string objectName)
    {
        // 1. Prova a caricare dalla cartella persistente
        string localPath = Path.Combine(PathSettings.GetObjectsFolder(), objectName + ".voxjson");
        if (File.Exists(localPath))
        {
            return File.ReadAllText(localPath);
        }

        // 2. Prova a caricare dalle risorse baseline incorporate
        var textAsset = Resources.Load<TextAsset>($"Baseline/objects/{objectName}");
        if (textAsset != null)
        {
            return textAsset.text;
        }

        return null;
    }
}