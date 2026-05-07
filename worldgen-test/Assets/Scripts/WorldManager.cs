using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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
        }
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