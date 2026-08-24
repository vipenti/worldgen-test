using UnityEngine;
using UnityEngine.Rendering;
using System;
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
    [Tooltip("Cuscinetto extra (in metri) oltre il cono visivo per attivare i chunk in anticipo (soglia di INGRESSO — vedi anche frustumExitMargin per l'isteresi in uscita).")]
    public float frustumPadding = 24f;
    [Tooltip("Margine AGGIUNTIVO (oltre frustumPadding) richiesto perché un chunk già visibile venga disattivato. Crea una fascia di isteresi tra soglia di ingresso e di uscita: senza questo, un chunk esattamente sul bordo del cono visivo può attivarsi/disattivarsi ad ogni piccola rotazione della camera, ritriggerando ad ogni flip la cascata OnEnable/OnDisable su tutte le sue decorazioni (vedi ObjectDistanceCuller).")]
    public float frustumExitMargin = 40f;
    [Tooltip("Intervallo minimo (secondi) tra due scansioni frustum consecutive scatenate SOLO dalla rotazione della camera (non dal movimento del giocatore, sempre istantaneo). Throttling per evitare di ricalcolare il frustum — e potenzialmente attivare/disattivare decine di chunk — quasi ad ogni frame durante una rotazione continua.")]
    public float rotationUpdateInterval = 0.1f;

    [Header("Chunk Creation Throttling")]
    [Tooltip("I chunk nell'anello 3x3 attorno al giocatore vengono sempre creati subito (serve terreno/collider immediati). Oltre quel raggio, se ne creano al massimo questi per frame invece di crearli tutti insieme — con viewDistance alto o quando il giocatore sprinta e più chunk mancano contemporaneamente, crearli tutti in un frame è un picco di lavoro sincrono (rumore Perlin + meshing + spawn decorazioni) che si sente come uno stutter.")]
    public int maxChunksCreatedPerFrame = 2;
    [Tooltip("Distanza dal giocatore entro cui il MeshCollider di un oggetto big/huge appena piazzato viene cucinato SUBITO (serve collisione immediata vicino al giocatore). Oltre questa distanza il bake viene messo in coda e spalmato su più frame — vedi maxCollidersBakedPerFrame.")]
    public float immediateColliderBakeDistance = 40f;
    [Tooltip("Quanti MeshCollider big/huge in coda (oltre immediateColliderBakeDistance) vengono cucinati al massimo per frame. Cuocere un MeshCollider è un'operazione fisica non banale — se un chunk nuovo piazza più alberi/edifici grandi insieme, farli tutti nello stesso frame del meshing del terreno è un altro picco.")]
    public int maxCollidersBakedPerFrame = 2;
    [Tooltip("Logga in console ogni scelta di edificio/lotto/prop nei biomi strutturati (città/villaggi) — utile per debug della distribuzione, ma genera molte allocazioni di stringa ad ogni chunk urbano creato. OFF di default.")]
    public bool verboseUrbanDecorLogging = false;
    [Tooltip("Logga in console diagnostica dettagliata per OGNI oggetto piazzato/risolto ([ScaleDiag], mesh/scala) e per ogni cambio di chunk del giocatore ([Decor]) — utile per debug, ma con decine di oggetti per chunk è una riga di console (e un'allocazione di stringa) per ciascuno. OFF di default.")]
    public bool verboseSpawnDiagnostics = false;

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

    [Header("Opzioni runtime (sovrascritte dal pannello Opzioni via GameSettings)")]
    [Tooltip("Se false, il percorso/strada centrale del bioma non viene generato/renderizzato.")]
    public bool enablePath = true;

    // GameSettings scala le dimensioni cella (densità oggetti), le renderDist* (render distance),
    // la scala per categoria di size e pathWidth/pathBlendWidth, senza toccare i campi Inspector
    // sopra, che restano i valori "base" — se GameSettings.Instance è null (es. scena di test
    // isolata) i moltiplicatori sono 1 e il comportamento è invariato.
    float DensityMultiplier => GameSettings.Instance != null ? GameSettings.Instance.Data.objectDensityMultiplier : 1f;
    float RenderDistMultiplier => GameSettings.Instance != null ? GameSettings.Instance.Data.renderDistanceMultiplier : 1f;
    float PathWidthMultiplier => GameSettings.Instance != null ? GameSettings.Instance.Data.pathWidthMultiplier : 1f;
    float PathBlendWidthMultiplier => GameSettings.Instance != null ? GameSettings.Instance.Data.pathBlendWidthMultiplier : 1f;

    int ScaledCellSize(int baseCellSize) => Mathf.Max(1, Mathf.RoundToInt(baseCellSize / DensityMultiplier));

    // Moltiplicatore di scala per categoria di size, uno per huge/big/normal/small/tiny (vedi
    // GameSettingsData). Come DensityMultiplier: 1 se GameSettings.Instance è null.
    float ScaleMultiplierFor(string size)
    {
        if (GameSettings.Instance == null) return 1f;
        var d = GameSettings.Instance.Data;
        return size switch
        {
            "huge" => d.scaleHugeMultiplier,
            "big" => d.scaleBigMultiplier,
            "normal" => d.scaleNormalMultiplier,
            "small" => d.scaleSmallMultiplier,
            "tiny" => d.scaleTinyMultiplier,
            _ => 1f
        };
    }

    void ApplyGameSettingsWorldParams()
    {
        if (GameSettings.Instance == null) return;
        enablePath = GameSettings.Instance.Data.enablePath;
        transitionLengthInChunks = GameSettings.Instance.Data.transitionLengthInChunks;
    }

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

    /// <summary>Un oggetto "unique" del bioma è stato piazzato — nome e posizione mondo, usato da
    /// CompassUI per mostrare un punto di interesse sulla bussola. Invocato solo la prima volta che
    /// un dato nome unique viene piazzato (stesso momento in cui entra in _uniqueSpawned).</summary>
    public static event Action<string, Vector3> OnUniqueObjectSpawned;
    /// <summary>Il registro degli oggetti unique è stato azzerato (nuovo bioma applicato, o
    /// rigenerazione forzata di tutti i chunk) — CompassUI deve dimenticare i punti di interesse
    /// del bioma precedente.</summary>
    public static event Action OnUniqueObjectsCleared;

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

    // Evita di ristampare lo stesso log "chunk tenuto in vita" ad ogni singola chiamata di
    // RemoveFarChunks (può scattare più volte al secondo mentre si ruota la camera) finché quel
    // chunk resta in attesa — un log solo quando inizia l'attesa, uno quando lo si molla davvero.
    private HashSet<Vector2Int> _loggedKeepAliveChunks = new HashSet<Vector2Int>();

    // Time.time dell'ultimo UpdateChunks() scatenato dalla rotazione — vedi rotationUpdateInterval.
    private float lastRotationUpdateTime = -999f;

    // Buffer riutilizzati per evitare allocazioni GC ad ogni UpdateChunks()/RemoveFarChunks(), che
    // con il vecchio codice giravano quasi ogni frame durante una rotazione della camera (vedi
    // rotationUpdateInterval): GeometryUtility.CalculateFrustumPlanes(Camera) alloca un nuovo
    // Plane[6] ad ogni chiamata se non gli si passa un array — usiamo invece l'overload non
    // allocante con questo array cachato. Stesso discorso per la List<Vector2Int> di
    // RemoveFarChunks, riusata con Clear() invece di essere ricreata ogni volta.
    private readonly Plane[] _cachedFrustumPlanes = new Plane[6];
    private readonly List<Vector2Int> _coordsToRemoveBuffer = new List<Vector2Int>();

    // Coda dei chunk mancanti oltre l'anello "vicino" (vedi maxChunksCreatedPerFrame): UpdateChunks
    // ci accoda le coordinate invece di creare tutto subito, DrainPendingChunkCreations() (chiamata
    // ogni frame da Update, non gated dal throttle di rotazione) ne smaltisce un numero limitato per
    // frame. _queuedChunkCoords evita doppioni in coda con un controllo O(1) invece di scandire la
    // Queue.
    private readonly Queue<Vector2Int> _pendingChunkCreations = new Queue<Vector2Int>();
    private readonly HashSet<Vector2Int> _queuedChunkCoords = new HashSet<Vector2Int>();

    // Coda dei bake MeshCollider per oggetti big/huge piazzati lontano dal giocatore (vedi
    // immediateColliderBakeDistance/maxCollidersBakedPerFrame in ApplySizeCollider).
    private readonly Queue<(GameObject obj, Mesh mesh)> _pendingColliderBakes = new Queue<(GameObject, Mesh)>();

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

        ApplyGameSettingsWorldParams();
        if (GameSettings.Instance != null)
            GameSettings.Instance.OnWorldParamsChanged += ApplyGameSettingsWorldParams;

        // Diagnostica temporanea: stampa i valori Inspector realmente in uso su questa
        // piattaforma/scena. Serve a verificare in modo inequivocabile — confrontando due
        // Player.log, uno da Editor Play e uno dalla build — se editor e build stanno davvero
        // leggendo la stessa configurazione (stessa scena, stessi campi WorldManager) oppure no,
        // invece di doverlo dedurre a occhio dal file .unity. Da rimuovere una volta chiuso il caso.
        Debug.Log($"<color=cyan>[ScaleDiag]</color> isEditor={Application.isEditor} platform={Application.platform} scene='{gameObject.scene.name}' | " +
            $"scaleHuge={scaleHuge} scaleBig={scaleBig} scaleNormal={scaleNormal} scaleSmall={scaleSmall} scaleTiny={scaleTiny} scaleDefault={scaleDefault} | " +
            $"viewDistance={viewDistance} objectGroundOffset={objectGroundOffset} urbanBlockSize={urbanBlockSize} urbanStreetWidth={urbanStreetWidth}");

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
        GenerateAsset.OnBiomeAssetsReady += HandleBiomeAssetsReady;

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
        GenerateAsset.OnBiomeAssetsReady -= HandleBiomeAssetsReady;
        if (GameSettings.Instance != null)
            GameSettings.Instance.OnWorldParamsChanged -= ApplyGameSettingsWorldParams;
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

        bool positionChanged = currentPlayerChunk != lastPlayerChunk;
        bool rotationChanged = Quaternion.Angle(currentCamRot, lastCameraRotation) > 4f;

        // Il movimento resta istantaneo (serve subito per creare/rimuovere chunk e collider), ma la
        // sola rotazione della camera NON scatena più UpdateChunks() ad ogni frame in cui supera i
        // 4°: con mouseSensitivity senza deltaTime (vedi MouseLook.cs) quella soglia viene superata
        // quasi ogni frame durante una rotazione, e UpdateChunks() ricalcola il frustum e può
        // attivare/disattivare decine di chunk (con relativa cascata OnEnable/OnDisable sui figli —
        // vedi ObjectDistanceCuller) ad ogni singola chiamata. Un throttle a tempo capa quante volte
        // al secondo scatta tutto questo, senza ritardi percepibili sulla comparsa/scomparsa dei
        // chunk ai bordi del cono visivo (rotationUpdateInterval è pensato per restare impercettibile).
        // lastCameraRotation NON viene aggiornato quando l'update è saltato per throttling: il delta
        // angolare accumulato resta quindi visibile al frame successivo, quindi nessuna rotazione
        // viene mai "persa", solo posticipata di al più rotationUpdateInterval secondi.
        bool rotationDue = rotationChanged && (Time.time - lastRotationUpdateTime >= rotationUpdateInterval);

        if (positionChanged || rotationDue)
        {
            if (positionChanged) LogPlayerChunkStatus(currentPlayerChunk);
            UpdateChunks();
            lastPlayerChunk = currentPlayerChunk;
            lastCameraRotation = currentCamRot;
            lastRotationUpdateTime = Time.time;
        }

        // Svuota le code di lavoro differito (creazione chunk oltre l'anello "vicino" e bake dei
        // MeshCollider big/huge lontani) SEMPRE, ogni frame, indipendentemente dal throttle sopra —
        // è proprio questo a spalmare il lavoro su più frame invece di farlo tutto insieme quando
        // UpdateChunks() scatta. Entrambi i metodi sono no-op economici (solo un controllo sulla
        // coda) quando non c'è nulla in attesa.
        DrainPendingChunkCreations();
        DrainPendingColliderBakes();
    }

    // Diagnostica: ad ogni cambio di chunk del player, logga cosa contiene realmente il chunk in
    // cui è appena entrato — separa il caso "non è mai stato deciso di piazzare nulla qui" (childCount
    // basso per un chunk non ancora decorato/decorato con pochi hit RNG) da "è stato piazzato ma non
    // si vede" (childCount alto ma a schermo sembra vuoto: allora il problema è nel rendering, non nel
    // placement — cercare '[Decor]'/'[UrbanDecor]' per la coordinata di questo chunk per i dettagli).
    void LogPlayerChunkStatus(Vector2Int coord)
    {
        if (!verboseSpawnDiagnostics) return; // l'intero metodo esiste solo per loggare
        if (!activeChunks.TryGetValue(coord, out Chunk chunk) || chunk == null)
        {
            Debug.Log($"<color=magenta>[Decor]</color> Player entered chunk {coord}: not yet created/active.");
            return;
        }
        float t = GetChunkTransitionT(coord);
        Debug.Log($"<color=magenta>[Decor]</color> Player entered chunk {coord}: {chunk.transform.childCount} children (real decorations+placeholders), " +
            $"pending={chunk.PendingDecorCount}, isTransitioning={isTransitioning}, t={t:F2} ({(t < 0.5f ? "old biome" : "new biome")}), active={chunk.gameObject.activeSelf}.");
    }

    // Usata quando llm_generated.json non esiste ancora (primo avvio, nessun mondo pre-generato)
    // o è invalido. perlinScale=0 fa collassare SampleNoise() a un valore costante ovunque (la
    // coordinata campionata diventa worldX*0, worldZ*0 per ogni punto/ottava) — quindi superficie
    // piatta invece di un terreno "roccioso" casuale, molto più leggibile come stato di attesa
    // prima che l'utente generi/carichi un bioma vero.
    BiomeConfig FallbackConfig() => new BiomeConfig {
        perlinScale = 0f, chunkHeight = 64, chunkWidth = 16,
        surfaceTileID = 1, middleTileID = 2, deepTileID = 3,
        liquidTileID = 13, waterLevel = 5,
        octaves = 4, ridgeFactor = 0f, warpStrength = 0.2f
    };

    BiomeConfig LoadLatestConfig()
    {
        string path = Path.Combine(PathSettings.GetJsonFolder(), "llm_generated.json");
        if (File.Exists(path))
        {
            var cfg = JsonUtility.FromJson<BiomeConfig>(File.ReadAllText(path));

            // BiomeConfig.chunkWidth/chunkHeight non hanno un field initializer, quindi se il
            // JSON dell'LLM arriva senza questi campi (risposta troncata/malformata — vedi
            // ExtractRawField in LLMWorldDirector, che semplicemente omette un campo se non lo
            // trova) JsonUtility li lascia a 0 senza segnalare nulla. chunkWidth=0 propaga in
            // divisioni (player.position.x / chunkWidth) che diventano Infinity, e
            // Mathf.FloorToInt(Infinity) restituisce int.MinValue — che poi manda in
            // OverflowException il primo Mathf.Abs a valle (es. UpdateTransitionProgress).
            // Meglio degradare a un bioma valido ma "sbagliato" piuttosto che un crash a runtime.
            if (cfg == null || cfg.chunkWidth <= 0 || cfg.chunkHeight <= 0)
            {
                Debug.LogWarning($"[WorldManager] llm_generated.json has invalid chunkWidth/chunkHeight (chunkWidth={cfg?.chunkWidth}, chunkHeight={cfg?.chunkHeight}) — using the fallback configuration to avoid a crash.");
                return FallbackConfig();
            }

            // Diagnostica: slopeMaxLimit e objectDensityThreshold sono proposti dall'LLM per ogni
            // bioma, senza clamp — se l'LLM propone un slopeMaxLimit insolitamente basso (fuori dal
            // range atteso [0.5-3]) quasi ogni cella di Chunk.ComputeLayoutMap() risulta "troppo
            // ripida" e LayoutMap crolla a ~0 ovunque, per cui NESSUN oggetto (di nessuna priorità)
            // viene piazzato — terreno presente, bioma visivamente vuoto, pur con asset generati
            // regolarmente (il conteggio new/cached in BenchmarkRunner non dipende da questo). Questo
            // log serve solo a confermare la causa se/quando ricapita — nessun clamp applicato qui.
            Debug.Log($"<color=cyan>[PlacementDiag]</color> Biome loaded: slopeMaxLimit={cfg.slopeMaxLimit}, objectDensityThreshold={cfg.objectDensityThreshold}, waterLevel={cfg.waterLevel}, isStructuredBiome={cfg.isStructuredBiome}");

            return cfg;
        }

        return FallbackConfig();
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

        // Overload non allocante: passando un array cachato invece di lasciare che Unity ne alloc
        // uno nuovo ad ogni chiamata evitiamo un Plane[6] di garbage ad ogni UpdateChunks() (che con
        // il vecchio codice girava quasi ogni frame durante una rotazione della camera).
        Plane[] cameraPlanes = null;
        if (Camera.main != null)
        {
            GeometryUtility.CalculateFrustumPlanes(Camera.main, _cachedFrustumPlanes);
            cameraPlanes = _cachedFrustumPlanes;
        }

        for (int x = -viewDistance; x <= viewDistance; x++)
        {
            for (int z = -viewDistance; z <= viewDistance; z++)
            {
                Vector2Int chunkCoord = new Vector2Int(pX + x, pZ + z);
                if (activeChunks.ContainsKey(chunkCoord)) continue;

                // L'anello 3x3 attorno al giocatore nasce sempre subito (serve terreno/collider
                // immediati sotto i piedi — stesso raggio "vicino" già usato altrove per i collider e
                // per lo spawn iniziale). Tutto il resto va in coda e viene creato un tot per frame da
                // DrainPendingChunkCreations(): evita che un intero anello di chunk mancanti (es.
                // giocatore che sprinta e attraversa più confini, o primo spawn) venga generato tutto
                // insieme nello stesso frame — vedi Chunk.GenerateMapDataInterpolated/GenerateMesh,
                // ognuno già non banale da solo.
                bool isCloseSafety = Mathf.Abs(x) <= 1 && Mathf.Abs(z) <= 1;
                if (isCloseSafety)
                {
                    CreateChunk(chunkCoord);
                }
                else if (_queuedChunkCoords.Add(chunkCoord))
                {
                    _pendingChunkCreations.Enqueue(chunkCoord);
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
                // Isteresi: un chunk già attivo resta tale finché non esce oltre il margine PIÙ
                // LARGO (frustumPadding + frustumExitMargin); uno già inattivo si riattiva col
                // margine normale (frustumPadding). Senza questa differenza, un chunk esattamente
                // sul bordo del cono visivo poteva attivarsi/disattivarsi ad ogni piccola rotazione
                // della camera, ritriggerando ad ogni flip la cascata OnEnable/OnDisable sui figli.
                float padding = kvp.Value.gameObject.activeSelf ? frustumPadding + frustumExitMargin : frustumPadding;
                chunkBounds.Expand(padding);
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

    // Smaltisce fino a maxChunksCreatedPerFrame coordinate dalla coda (vedi UpdateChunks). Chiamata
    // ogni frame da Update(), sempre — è questo, non UpdateChunks(), a garantire che il backlog si
    // svuoti entro pochi frame anche se il giocatore si ferma subito dopo aver attraversato un
    // confine di chunk (altrimenti, essendo la coda popolata solo dentro UpdateChunks, un giocatore
    // fermo non la farebbe mai avanzare).
    void DrainPendingChunkCreations()
    {
        if (_pendingChunkCreations.Count == 0) return;

        int pX = Mathf.FloorToInt(player.position.x / targetConfig.chunkWidth);
        int pZ = Mathf.FloorToInt(player.position.z / targetConfig.chunkWidth);

        int created = 0;
        while (created < maxChunksCreatedPerFrame && _pendingChunkCreations.Count > 0)
        {
            Vector2Int coord = _pendingChunkCreations.Dequeue();
            _queuedChunkCoords.Remove(coord);

            if (activeChunks.ContainsKey(coord)) continue; // creato nel frattempo per altra via
            // Il giocatore può essersi allontanato da quando la coordinata è stata accodata:
            // scartiamo silenziosamente quelle ormai fuori raggio invece di sprecare il budget di
            // questo frame su un chunk che RemoveFarChunks butterebbe via al prossimo giro comunque.
            if (Mathf.Abs(coord.x - pX) > viewDistance + 1 || Mathf.Abs(coord.y - pZ) > viewDistance + 1) continue;

            CreateChunk(coord);
            created++;
        }
    }

    // Smaltisce fino a maxCollidersBakedPerFrame bake MeshCollider in coda (vedi ApplySizeCollider).
    // Stesso motivo di DrainPendingChunkCreations: chiamata ogni frame da Update(), non solo quando
    // UpdateChunks() scatta.
    void DrainPendingColliderBakes()
    {
        if (_pendingColliderBakes.Count == 0) return;

        int baked = 0;
        while (baked < maxCollidersBakedPerFrame && _pendingColliderBakes.Count > 0)
        {
            var (obj, colliderMesh) = _pendingColliderBakes.Dequeue();
            if (obj == null) continue; // chunk/oggetto distrutto nel frattempo (es. troppo lontano)
            if (obj.GetComponent<MeshCollider>() != null) continue; // già gestito altrove

            obj.AddComponent<MeshCollider>().sharedMesh = colliderMesh;
            baked++;
        }
    }

    void RemoveFarChunks(Vector2Int playerChunkPos)
    {
        List<Vector2Int> coordsToRemove = _coordsToRemoveBuffer;
        coordsToRemove.Clear();
        foreach (var chunk in activeChunks)
        {
            int distX = Mathf.Abs(chunk.Key.x - playerChunkPos.x);
            int distZ = Mathf.Abs(chunk.Key.y - playerChunkPos.y);
            if (distX > viewDistance + 1 || distZ > viewDistance + 1)
            {
                // Non distruggere ancora un chunk con decorazioni in attesa di generazione IA: il
                // billboard placeholder è figlio suo, quindi morirebbe insieme a lui prima che
                // l'asset sia pronto — quando la generazione poi completa (vedi ReplacePlaceholders)
                // non ci sarebbe più nessun placeholder a cui agganciare la mesh, e la decorazione
                // andrebbe persa per sempre anche se generata con successo. Resta comunque fuori
                // vista/collider (il loop di stato in UpdateChunks lo salta già oltre viewDistance),
                // costa solo la memoria di un chunk in più finché non si svuota la sua coda.
                if (chunk.Value != null && chunk.Value.PendingDecorCount > 0)
                {
                    if (_loggedKeepAliveChunks.Add(chunk.Key) && verboseSpawnDiagnostics)
                        Debug.Log($"<color=yellow>[Decor]</color> Chunk {chunk.Key} kept alive beyond viewDistance: {chunk.Value.PendingDecorCount} decorations still waiting to be generated.");
                    continue;
                }
                if (_loggedKeepAliveChunks.Remove(chunk.Key) && verboseSpawnDiagnostics)
                    Debug.Log($"<color=yellow>[Decor]</color> Chunk {chunk.Key} freed, all pending decorations were resolved (or lost) — removed normally.");
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
                enablePath ? pathWidth * PathWidthMultiplier * 0.5f : 0f,
                enablePath ? pathBlendWidth * PathBlendWidthMultiplier : 0f,
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

        // Riusata ad ogni passata di priorità (Clear invece di un nuovo LINQ .Where().ToList() per
        // passata, 4 volte per chunk) — stesso risultato, senza le allocazioni del delegate/iteratore
        // LINQ né una nuova List per ciascuna delle 4 passate.
        var passObjects = new List<string>(allNames.Count);

        for (int currentPriority = 1; currentPriority <= 4; currentPriority++)
        {
            passObjects.Clear();
            foreach (string n in allNames)
            {
                int pr = data.objectPriorities.TryGetValue(n, out int pVal) ? pVal : 3;
                if (pr == currentPriority) passObjects.Add(n);
            }

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
                            if (isUnique)
                            {
                                _uniqueSpawned.Add(name);
                                OnUniqueObjectSpawned?.Invoke(name, new Vector3(propWorldPos.x, pGroundY, propWorldPos.z));
                            }
                        }
                    }
                    continue;
                }

                int cellSize = ScaledCellSize(isUnique ? cellUnique : (fc <= 0.03f ? cellSuperRare : (fc <= 0.10f ? cellRare : (fc <= 0.50f ? cellCommon : cellVeryCommon))));
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

                    // Cap relativo alla larghezza del chunk, non un valore assoluto fisso: con
                    // chunkWidth=16 (default LLM) un cap fisso di 8 celle produce un quadrato
                    // 17x17 — più grande dell'intero chunk 16x16. Il primo albero "big"/"huge"
                    // priority 1 che riesce a piazzarsi (footRadius=4-8 con gli scale di default
                    // 7-15) escludeva quindi l'INTERO chunk, impedendo per sempre a qualunque
                    // oggetto priority 2/3/4 — o persino altri priority 1 — di trovare una cella
                    // libera: da qui il mondo che sembrava mostrare sempre un solo tipo di
                    // decorazione per chunk (o nessuna). w/5 lascia comunque spazio per diverse
                    // decorazioni nello stesso chunk anche con gli scale factor più grandi.
                    int footRadius = Mathf.Min(Mathf.CeilToInt(scaleFactor * 0.5f), Mathf.Max(1, w / 5));
                    for (int dx = -footRadius; dx <= footRadius; dx++)
                    for (int dz = -footRadius; dz <= footRadius; dz++)
                    {
                        int nx = lx + dx, nz = lz + dz;
                        if ((uint)nx < (uint)w && (uint)nz < (uint)w) excluded[nx, nz] = true;
                    }

                    if (isUnique)
                    {
                        _uniqueSpawned.Add(name);
                        OnUniqueObjectSpawned?.Invoke(name, new Vector3(wx, groundY, wz));
                    }
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

        // Un solo passaggio su allNames invece di 4 LINQ .Where().ToList() separate (ognuna una
        // scansione completa + una List allocata) — stesso risultato, un quarto delle scansioni e
        // delle allocazioni per chunk urbano creato.
        var buildingNames = new List<string>();
        var streetPropNames = new List<string>();
        var decorNames3 = new List<string>();
        var decorNames4 = new List<string>();
        foreach (string n in allNames)
        {
            int p = data.objectPriorities.TryGetValue(n, out int pVal) ? pVal : 3;
            switch (p)
            {
                case 1: if (!LooksLikeGroundTile(n)) buildingNames.Add(n); break;
                case 2: streetPropNames.Add(n); break;
                case 3: decorNames3.Add(n); break;
                default: decorNames4.Add(n); break;
            }
        }

        if (buildingNames.Count == 0) return; // No real structures registered yet for this biome.

        var placedStructures = new List<StructureAnchor>();
        int blockSize = gridParams.blockSize;

        // Diagnostica: la selezione dell'edificio per lotto sotto è un hash uniforme su
        // buildingNames, SENZA distinguere frequenze ("unique"/"rare" pesano quanto "common") — un
        // edificio "unique" ha quindi solo 1/N di possibilità per lotto, nessuna garanzia di comparire
        // mai. Logghiamo la composizione del pool una volta per chunk per verificare sul campo se un
        // nome sparisce per RNG sfortunata su pochi lotti o perché non è mai nel pool. Dietro un flag
        // (OFF di default): con molti chunk urbani creati insieme, queste log a interpolazione di
        // stringa erano una sorgente non trascurabile di garbage GC proprio nei frame già più pesanti.
        if (verboseUrbanDecorLogging)
            Debug.Log($"<color=magenta>[UrbanDecor]</color> Chunk {chunk.ChunkCoord}: building pool ({buildingNames.Count}) = [{string.Join(", ", buildingNames)}], street props ({streetPropNames.Count}) = [{string.Join(", ", streetPropNames)}].");

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

            if (verboseUrbanDecorLogging)
                Debug.Log($"<color=magenta>[UrbanDecor]</color> Chunk {chunk.ChunkCoord}, lot ({gcx},{gcz}): chose building '{bName}' (index {Mathf.Abs(lotHash) % buildingNames.Count}/{buildingNames.Count}).");
            InstantiateAssetPrimitive(bName, lotCenterX, bGroundY, lotCenterZ, bScale, chunk.transform, data, bRotZ);
            placedStructures.Add(new StructureAnchor {
                position = new Vector3(lotCenterX, bGroundY, lotCenterZ), assetName = bName, scale = bScale
            });

            // Esclude solo l'ingombro reale dell'edificio piazzato (bScale, già ridotto per stare
            // nel lotto da GetBuildingScaleForLot), non l'intero lotto (blockSize) — prima di
            // questo fix, con scaleHuge grande (es. 60) il lotto forzato a blockSize >= scaleHuge+2
            // faceva escludere un raggio enorme attorno a ogni edificio, cancellando lo spazio per
            // fontane/carretti/alberi (priority 2/3/4) anche se generati correttamente: il piazzale
            // davanti alle case restava vuoto perché l'intera area era già "excluded".
            int footRadius = Mathf.CeilToInt(bScale * 0.5f) + 1;
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
                if (!isUnique && spawnRoll > fc)
                {
                    if (verboseUrbanDecorLogging)
                        Debug.Log($"<color=magenta>[UrbanDecor]</color> Chunk {chunk.ChunkCoord}: '{name}' discarded near '{structure.assetName}' — roll failed ({spawnRoll:F2} > {fc:F2}, freq chance).");
                    continue;
                }
                if (isUnique && _uniqueSpawned.Contains(name))
                {
                    if (verboseUrbanDecorLogging)
                        Debug.Log($"<color=magenta>[UrbanDecor]</color> Chunk {chunk.ChunkCoord}: '{name}' discarded near '{structure.assetName}' — already placed elsewhere (unique).");
                    continue;
                }

                int side = Mathf.Abs(propSeed) % 4;
                float offsetDistance = (blockSize * 0.5f) + (urbanStreetWidth * 0.5f);
                Vector3 offsetDir = side switch { 0 => Vector3.forward, 1 => Vector3.right, 2 => Vector3.back, _ => Vector3.left };
                Vector3 propWorldPos = structure.position + offsetDir * offsetDistance;

                int plx = Mathf.FloorToInt(propWorldPos.x - bx);
                int plz = Mathf.FloorToInt(propWorldPos.z - bz);
                if (plx < 0 || plx >= w || plz < 0 || plz >= w)
                {
                    if (verboseUrbanDecorLogging)
                        Debug.Log($"<color=magenta>[UrbanDecor]</color> Chunk {chunk.ChunkCoord}: '{name}' discarded near '{structure.assetName}' — offset ({plx},{plz}) outside the chunk.");
                    continue;
                }
                if (excluded[plx, plz] || chunk.HeightMap[plx, plz] <= cfg.waterLevel)
                {
                    if (verboseUrbanDecorLogging)
                        Debug.Log($"<color=magenta>[UrbanDecor]</color> Chunk {chunk.ChunkCoord}: '{name}' discarded near '{structure.assetName}' — cell ({plx},{plz}) {(excluded[plx, plz] ? "already occupied/excluded" : "underwater")}.");
                    continue;
                }

                float pScale = data.objectScaleFactors.TryGetValue(name, out float ps) ? ps : scaleSmall;
                float pGroundY = GetGroundY(propWorldPos.x, propWorldPos.z, chunk.HeightMap[plx, plz]);
                float rotZ = ((propSeed >> 16) & 0xFF) / 255f * 360f;

                if (verboseUrbanDecorLogging)
                    Debug.Log($"<color=magenta>[UrbanDecor]</color> Chunk {chunk.ChunkCoord}: '{name}' placed near '{structure.assetName}'.");
                InstantiateAssetPrimitive(name, propWorldPos.x, pGroundY, propWorldPos.z, pScale, chunk.transform, data, rotZ);
                excluded[plx, plz] = true;
                if (isUnique)
                {
                    _uniqueSpawned.Add(name);
                    OnUniqueObjectSpawned?.Invoke(name, new Vector3(propWorldPos.x, pGroundY, propWorldPos.z));
                }
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
            int cellSize = ScaledCellSize(isUnique ? cellUnique : (fc <= 0.03f ? cellSuperRare : (fc <= 0.10f ? cellRare : (fc <= 0.50f ? cellCommon : cellVeryCommon))));

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

                // Stesso cap relativo a w applicato in SpawnLayoutObjects: vedi commento lì per il motivo
                // (un cap assoluto come "8" può superare l'intero chunk quando chunkWidth è piccolo).
                int footRadius = Mathf.Min(Mathf.CeilToInt(scaleFactor * 0.5f), Mathf.Max(1, w / 5));
                for (int dx = -footRadius; dx <= footRadius; dx++)
                for (int dz = -footRadius; dz <= footRadius; dz++)
                {
                    int nx = lx + dx, nz = lz + dz;
                    if ((uint)nx < (uint)w && (uint)nz < (uint)w) excluded[nx, nz] = true;
                }

                if (isUnique)
                {
                    _uniqueSpawned.Add(name);
                    OnUniqueObjectSpawned?.Invoke(name, new Vector3(wx, groundY, wz));
                }
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
            var ownerChunk = parent != null ? parent.GetComponent<Chunk>() : null;
            ownerChunk?.NotePendingDecorAdded();
            if (verboseSpawnDiagnostics)
            {
                string coordLabel = ownerChunk != null ? ownerChunk.ChunkCoord.ToString() : "?";
                Debug.Log($"<color=yellow>[Decor]</color> Chunk {coordLabel}: '{name}' not ready yet, created a waiting placeholder (pending in chunk: {ownerChunk?.PendingDecorCount ?? -1}).");
            }
            SpawnBillboard(name, new Vector3(wx, groundY + scaleFactor * 0.5f, wz), parent, scaleFactor, visible: false);
            return;
        }

        if (obj != null)
        {
            obj.transform.localScale = Vector3.one * scaleFactor;
            if (!data.objectSizes.TryGetValue(name, out string sizeStr)) sizeStr = "big";

            // Diagnostica temporanea (vedi log gemello in InitRoutine): per gli oggetti huge/big,
            // che sono quelli comparsi come "pilastri" giganti, stampiamo mesh nativa, scaleFactor
            // e dimensione finale in world space — per capire se in build la MESH caricata ha
            // bounds diversi da quelli in Editor (es. normalizzazione mancata / mesh sbagliata)
            // oppure se il solo scaleFactor Inspector differisce (vedi log [ScaleDiag] a inizio sessione).
            // Dietro verboseSpawnDiagnostics (OFF di default): con più oggetti big/huge per chunk
            // (alberi, edifici...) era una riga di console — e un'allocazione di stringa — per
            // ciascuno, proprio nei frame in cui un chunk nuovo li piazza tutti insieme.
            if (verboseSpawnDiagnostics && (sizeStr == "huge" || sizeStr == "big"))
            {
                Vector3 nativeBounds = runtimeMesh != null ? runtimeMesh.bounds.size : Vector3.zero;
                Debug.Log($"<color=cyan>[ScaleDiag]</color> '{name}' (size={sizeStr}) native mesh={nativeBounds} scaleFactor={scaleFactor} → final world size≈{nativeBounds * scaleFactor} (mesh source: {(runtimeMesh != null ? "RuntimeMeshCache" : "prefab")})");
            }

            float currentOffset = (sizeStr == "huge" || sizeStr == "big") ? objectGroundOffset : 0f;
            float calculatedMinY = GetObjectMinY(obj, groundY);
            obj.transform.position += Vector3.up * (groundY - calculatedMinY + currentOffset);

            ApplyShadowCastingMode(obj, sizeStr);
            ApplySizeCollider(obj, sizeStr, name);
            obj.AddComponent<ObjectDistanceCuller>().Initialize(player, maxDist, name, lodHighPercent, lodMediumPercent, useSuperHighForClosest: sizeStr == "huge" || sizeStr == "big");
        }
    }

    // Gli oggetti "tiny"/"small" (fiori, funghi, sassolini, clutter minuto) vengono piazzati in
    // grandissima quantità (vedi cellVeryCommon/cellCommon — una ogni 3-5 unità) e il loro
    // contributo visivo all'ombra proiettata è trascurabile alla distanza a cui restano visibili
    // (renderDistTiny/renderDistSmall). Ogni MeshRenderer appena creato però nasce con
    // shadowCastingMode = On di default: farli comunque partecipare al render pass ombre moltiplica
    // inutilmente i draw call delle cascate della shadow map (una submission per oggetto per
    // cascata) senza nessun beneficio visivo apprezzabile — è il costo principale osservato dopo
    // l'aumento della shadow strength della luce (vedi commit "Lighting update"). normal/big/huge
    // restano shadow caster normali: hanno un impatto visivo reale sull'ombreggiatura della scena.
    static void ApplyShadowCastingMode(GameObject obj, string sizeStr)
    {
        var mode = (sizeStr == "tiny" || sizeStr == "small") ? ShadowCastingMode.Off : ShadowCastingMode.On;
        foreach (var rend in obj.GetComponentsInChildren<Renderer>())
            rend.shadowCastingMode = mode;
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

            // Bake fisico SEMPRE dalla tier "_high" a risoluzione capped (mai dalla tier
            // "_superhigh" usata come LOD visivo più vicino per big/huge — vedi
            // ObjectDistanceCuller.useSuperHighForClosest — né da qualunque LOD sia visivamente
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

            // Cuocere un MeshCollider è un'operazione fisica non banale (Unity ricalcola la hull di
            // collisione dalla mesh) — se un chunk nuovo piazza più alberi/edifici grandi insieme,
            // farlo per tutti nello stesso frame del meshing del terreno è un altro contributo allo
            // stutter quando si carica un chunk. Vicino al giocatore serve subito (potrebbe
            // interagirci nell'istante stesso), oltre immediateColliderBakeDistance lo mettiamo in
            // coda e lo spalmiamo su più frame — vedi DrainPendingColliderBakes.
            float distSqr = (obj.transform.position - player.position).sqrMagnitude;
            if (distSqr <= immediateColliderBakeDistance * immediateColliderBakeDistance)
            {
                obj.AddComponent<MeshCollider>().sharedMesh = colliderMesh;
            }
            else
            {
                _pendingColliderBakes.Enqueue((obj, colliderMesh));
            }
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
        float baseDist = size switch {
            "huge" => renderDistHuge, "big" => renderDistBig, "normal" => renderDistNormal,
            "small" => renderDistSmall, "tiny" => renderDistTiny, _ => renderDistBig
        };
        return baseDist * RenderDistMultiplier;
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

        if (!_placeholders.TryGetValue(objectName, out var list))
        {
            if (verboseSpawnDiagnostics)
                Debug.Log($"<color=cyan>[Decor]</color> '{objectName}' ready, but no chunk was waiting for it (no placeholder registered).");
            return;
        }

        var toSpawn = new List<(Vector3 pos, Transform parent)>();
        foreach (var p in list) if (p != null) toSpawn.Add((p.transform.position, p.transform.parent));
        foreach (var p in list)
        {
            if (p == null) continue;
            var ownerChunk = p.transform.parent != null ? p.transform.parent.GetComponent<Chunk>() : null;
            ownerChunk?.NotePendingDecorResolved();
            Destroy(p.gameObject);
        }
        _placeholders.Remove(objectName);

        if (verboseSpawnDiagnostics)
        {
            int lostCount = list.Count - toSpawn.Count;
            Debug.Log($"<color=cyan>[Decor]</color> '{objectName}' ready: replaced {toSpawn.Count} still-alive placeholders." +
                (lostCount > 0 ? $" {lostCount} had already been destroyed along with their chunk before generation finished — lost." : ""));
        }

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
            else
            {
                var missedChunk = parent != null ? parent.GetComponent<Chunk>() : null;
                Debug.LogWarning($"<color=orange>[Decor]</color> Chunk {(missedChunk != null ? missedChunk.ChunkCoord.ToString() : "?")}: '{objectName}' reported ready but no mesh/prefab found — decoration skipped.");
                continue;
            }

            obj.transform.localScale = Vector3.one * scaleFactor;

            // Diagnostica temporanea (vedi log gemelli in InitRoutine e InstantiateAssetPrimitive):
            // questo è il path di piazzamento più comune per huge/big (edifici, alberi), dato che
            // arrivano quasi sempre da un placeholder risolto in modo asincrono dopo la generazione
            // IA, non da InstantiateAssetPrimitive diretto.
            if (verboseSpawnDiagnostics && (sizeStr == "huge" || sizeStr == "big"))
            {
                Vector3 nativeBounds = runtimeMesh != null ? runtimeMesh.bounds.size : Vector3.zero;
                Debug.Log($"<color=cyan>[ScaleDiag]</color> '{objectName}' (size={sizeStr}) mesh nativa={nativeBounds} scaleFactor={scaleFactor} → world size finale≈{nativeBounds * scaleFactor} (fonte mesh: {(runtimeMesh != null ? "RuntimeMeshCache" : "prefab")})");
            }

            float currentOffset = (sizeStr == "huge" || sizeStr == "big") ? objectGroundOffset : 0f;
            float calculatedMinY = GetObjectMinY(obj, groundY);
            obj.transform.position += Vector3.up * (groundY - calculatedMinY + currentOffset);

            ApplySizeCollider(obj, sizeStr, objectName);
            obj.AddComponent<ObjectDistanceCuller>().Initialize(player, maxDist, objectName, lodHighPercent, lodMediumPercent, useSuperHighForClosest: sizeStr == "huge" || sizeStr == "big");

            if (verboseSpawnDiagnostics)
            {
                var placedChunk = parent != null ? parent.GetComponent<Chunk>() : null;
                Debug.Log($"<color=cyan>[Decor]</color> Chunk {(placedChunk != null ? placedChunk.ChunkCoord.ToString() : "?")}: '{objectName}' placed (placeholder resolved).");
            }
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
        Debug.Log("<color=orange>[WorldManager]</color> Backup of the old biome completed successfully.");
    }

    // Swap atomico tra bioma attivo e bioma appena generato, eseguito UNA SOLA VOLTA quando TUTTI gli
    // asset del nuovo bioma sono pronti (chiamato da LLMWorldDirector.TriggerTerrainTransition,
    // agganciato a GenerateAsset.OnBiomeAssetsReady). Prima di questo momento il mondo attivo
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

        Debug.Log("<color=orange>[WorldManager]</color> Global spatial interpolation started correctly.");
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
        _objectSizes.Clear(); _uniqueSpawned.Clear();
        OnUniqueObjectsCleared?.Invoke();
        // NON puliamo _placeholders qui. Questo metodo viene richiamato da ApplyNewConfig() ad ogni
        // cambio bioma, ormai solo quando TUTTI gli asset del nuovo bioma sono pronti — ma se
        // l'utente chiede un ALTRO cambio bioma mentre quella generazione successiva è ancora in
        // corso in background (specialmente in build, dove ogni oggetto nuovo passa sempre dalla
        // pipeline IA: vedi il ramo #else qui sotto), un billboard placeholder può già essere stato
        // piazzato per un suo oggetto. Un Clear() qui perderebbe il riferimento a quel placeholder:
        // quando la generazione completa poco dopo, VoxelRenderer.OnPrefabSaved / GenerateAsset chiamano
        // ReplacePlaceholders(name), che non trova più nulla da sostituire in _placeholders — il
        // billboard resta invisibile per sempre, anche se l'oggetto risulta correttamente generato
        // (motivo per cui il pannello Info lo mostrava "OK" ma non appariva mai nel mondo). La lista
        // è indicizzata per nome ed è già "auto-pulente" (ReplacePlaceholders rimuove la propria
        // chiave a fine sostituzione, e scarta da sé le entry i cui GameObject sono stati distrutti
        // nel frattempo), quindi non serve svuotarla qui.

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

                // Volutamente identico in Editor e in build (niente più #if UNITY_EDITOR qui):
                // prima leggevamo subito un eventuale Assets/GeneratedAsset/{name}.prefab già
                // presente su disco e lo mettevamo in _readyPrefabs come "pronto", saltando del
                // tutto EnsureMeshCached/RuntimeMeshCache. Quel .prefab però è uno snapshot cotto
                // in una sessione Editor passata, con qualunque logica di normalizzazione/scala/
                // pedana-cut fosse in vigore ALLORA — non viene mai invalidato quando la logica di
                // BuildOptimizedMesh cambia. Risultato: a seconda che un dato oggetto avesse o
                // meno un .prefab residuo sul disco, in Editor comparivano oggetti con dimensioni
                // sbagliate/affondati nel terreno in modo apparentemente casuale, mentre il build
                // (che non ha mai avuto questo ramo: vedi InstantiateAssetPrimitive) era sempre
                // coerente perché ricostruisce sempre la mesh da zero dal .voxjson grezzo con il
                // codice attuale. Marcando tutto come pending, ogni oggetto passa sempre da
                // EnsureMeshCached/GetBestAvailableMesh anche in Editor — se il .voxjson esiste
                // già su disco la mesh viene ricalcolata sincrona nello stesso frame (nessuna
                // regressione di velocità), altrimenti passa dal placeholder in attesa, esattamente
                // come in build.
                _pendingObjectNames.Add(name);
            }
        }
        catch (System.Exception e) { Debug.LogError($"Parsing current_objects failed: {e.Message}"); }
    }
    
    float SizeToScale(string size) => (size switch {
        "huge" => scaleHuge, "big" => scaleBig, "normal" => scaleNormal, "small" => scaleSmall, "tiny" => scaleTiny, _ => scaleDefault
    }) * ScaleMultiplierFor(size);

    float FreqToChance(string freq) => freq switch {
        "very_common" => 1.00f, "common" => 0.50f, "rare" => 0.10f, "super_rare" => 0.03f, "unique" => -1f, _ => 0.50f
    };

    public void RegenerateAllChunks()
    {
        foreach (var kvp in activeChunks) if (kvp.Value != null) Destroy(kvp.Value.gameObject);
        activeChunks.Clear();
        _uniqueSpawned.Clear();
        OnUniqueObjectsCleared?.Invoke();
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

    void HandleBiomeAssetsReady()
    {
        logPanel?.LogEvent("[Transition] All biome assets loaded! Interpolation starting.");
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
            Debug.Log($"[WorldManager] Initialized persistent JSON file: {targetDiskPath}");
        }
        else
        {
            Debug.LogWarning($"[WorldManager] Baseline resource file not found: {resourcePath}");
        }
    }

    void CopyResourceBinaryToPersistent(string resourcePath, string targetDiskPath)
    {
        if (File.Exists(targetDiskPath)) return;

        var binaryAsset = Resources.Load<TextAsset>(resourcePath);
        if (binaryAsset != null && binaryAsset.bytes != null)
        {
            File.WriteAllBytes(targetDiskPath, binaryAsset.bytes);
            Debug.Log($"[WorldManager] Initialized persistent binary file: {targetDiskPath}");
        }
        else
        {
            Debug.LogWarning($"[WorldManager] Baseline binary resource not found: {resourcePath}");
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
                Debug.Log($"[WorldManager] Dynamically loading meshes at runtime for '{assetName}'...");
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

        // Tier "_superhigh" (solo per big/huge, vedi VoxelRenderer/ObjectDistanceCuller). Non entra
        // nella guardia hasBase/hasHigh/hasMedium/hasLow qui sopra apposta: per tiny/small/normal
        // questo file non esiste mai, quindi se lo mettessimo in quella guardia il controllo
        // "già tutto pronto, esci subito" non scatterebbe mai per loro e questa funzione rifarebbe
        // il lavoro sopra ad OGNI chiamata (vedi commento in cima sul loop di reimport). Qui invece
        // il tentativo silenzioso avviene solo quando il corpo gira comunque (prima volta per
        // quell'asset), e per big/huge la mesh finisce comunque cache-ata per le chiamate successive.
        if (!VoxelRenderer.RuntimeMeshCache.ContainsKey(assetName + "_superhigh"))
        {
            string superHighJson = LoadVoxJsonString(assetName + "_superhigh");
            if (!string.IsNullOrEmpty(superHighJson)) renderer.LoadFromJson(superHighJson, assetName + "_superhigh");
        }
    }

    // Ritorna la prima mesh disponibile in cache per un asset, provando prima il base poi le tier
    // LOD in ordine di dettaglio decrescente (_high, _medium, _low). Serve agli spawn-site
    // (InstantiateAssetPrimitive, ReplacePlaceholders) perché per molti asset — quelli della
    // baseline di build impacchettata da BuildAssetPacker, o generati prima del fix che salva anche
    // il voxjson base — la tier "base" senza suffisso non esiste mai, solo le tre LOD.
    // Ordine invertito rispetto a prima: proviamo prima le tier LOD già ridotte (_high/_medium/
    // _low, quella "_high" comunque limitata a HighGridSize — vedi VoxelRenderer/GenerateResolutions,
    // mai il pieno dettaglio nativo) e solo se NESSUNA esiste ancora (asset appena generato, LOD non
    // ancora calcolate) ripieghiamo sulla mesh base non compressa. Prima, preferendo sempre la base
    // quando presente, il primo piazzamento di ogni oggetto (InstantiateAssetPrimitive,
    // ReplacePlaceholders) mostrava la mesh a piena risoluzione nativa finché ObjectDistanceCuller
    // non la sostituiva — per oggetti "tiny"/"small" (dove _high è comunque limitato a una
    // risoluzione bassa) questo si vedeva come un lampo, o restava visibile stabilmente se lo
    // scambio di ObjectDistanceCuller non scattava per qualche motivo.
    Mesh GetBestAvailableMesh(string name)
    {
        foreach (string tier in new[] { "_high", "_medium", "_low" })
            if (VoxelRenderer.RuntimeMeshCache.TryGetValue(name + tier, out Mesh tierMesh) && tierMesh != null) return tierMesh;
        if (VoxelRenderer.RuntimeMeshCache.TryGetValue(name, out Mesh baseMesh) && baseMesh != null) return baseMesh;
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