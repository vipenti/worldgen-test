using UnityEngine;
using System.Collections.Generic;
using System.IO;
using System.Linq;

public class WorldManager : MonoBehaviour
{
    [Header("Riferimenti")]
    public Transform player;            
    public GameObject chunkPrefab;      

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

    void Start()
    {
        // Inizializzazione: all'inizio vecchio e nuovo bioma coincidono
        targetConfig = LoadLatestConfig();
        oldConfig = targetConfig;
        
        UpdateChunks();
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
        // Cerchiamo nella cartella Json (allineato con LLMWorldDirector)
        string path = Path.Combine(Application.dataPath, "Json");
        if (!Directory.Exists(path)) Directory.CreateDirectory(path);

        var directory = new DirectoryInfo(path);
        FileInfo latestFile = directory.GetFiles("*.json")
                                     .OrderByDescending(f => f.LastWriteTime)
                                     .FirstOrDefault();

        if (latestFile != null)
        {
            string jsonContent = File.ReadAllText(latestFile.FullName);
            return JsonUtility.FromJson<BiomeConfig>(jsonContent);
        }

        // Fallback se non esiste nessun file
        return new BiomeConfig { 
            perlinScale = 0.1f, chunkHeight = 64, chunkWidth = 16, 
            surfaceTileID = 1, middleTileID = 2, deepTileID = 3,
            liquidTileID = 13, waterLevel = 5 
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
            // Quando non c'è transizione, centro lontanissimo → t=1 ovunque (puro nuovo bioma)
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