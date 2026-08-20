using UnityEngine;
using System.Collections.Generic;
using System;
using Newtonsoft.Json;
using System.Collections; // Necessario per IEnumerator
using System.IO;

#if UNITY_EDITOR
using UnityEditor;
#endif

[Serializable]
public class WorldData
{
    public List<string> token_list;
    public List<List<List<int>>> grid;
}

public class WorldSpawner : MonoBehaviour
{
    class StreamedWorldData
    {
        public List<string> tokenList;
        public int[] flatGrid;
        public int sizeX;
        public int sizeY;
        public int sizeZ;

        public int GetBlockId(int x, int y, int z)
        {
            int index = ((z * sizeY) + y) * sizeX + x;
            return flatGrid[index];
        }
    }

    public string jsonFileName = "tile_data_from_world"; 
    public List<GameObject> blockPrefabs;

    [Header("Impostazioni Coroutine")]
    [Tooltip("Tempo di attesa tra un layer e l'altro (0 = un frame)")]
    public float delayBetweenLayers = 0.05f;

    [ContextMenu("Avvia Generazione Layer-by-Layer")]
    public void StartSpawnCoroutine()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("To use Coroutines and avoid freezing, start the game (Play Mode)!");
            return;
        }
        
        StopAllCoroutines(); // Ferma eventuali generazioni precedenti
        StartCoroutine(SpawnWorldLayerByLayer());
    }

    IEnumerator SpawnWorldLayerByLayer()
    {
        LoadPrefabList();

        // 1. Pulizia
        int deletedCount = 0;
        foreach (Transform child in transform) {
            deletedCount++;
            Destroy(child.gameObject);
        }
        Debug.Log($"Cleanup completed: removed {deletedCount} blocks.");
        yield return null; // Aspetta un frame per permettere la distruzione

        // 2. Caricamento JSON via streaming (evita la creazione di milioni di liste annidate)
        if (!TryLoadWorldDataStreamed(out StreamedWorldData worldData, out string loadError))
        {
            Debug.LogError(loadError);
            yield break;
        }

        // Calcolo dimensioni
        int sizeZ = worldData.sizeZ;
        int sizeY = worldData.sizeY;
        int sizeX = worldData.sizeX;

        Debug.Log($"Starting async generation. Size: X:{sizeX} Y:{sizeY} Z:{sizeZ}");

        int spawnedCount = 0;
        int culledCount = 0;

        // 3. Ciclo di generazione: Y è il ciclo ESTERNO per fare layer-by-layer
        for (int y = 0; y < sizeY; y++)
        {
            for (int z = 0; z < sizeZ; z++)
            {
                for (int x = 0; x < sizeX; x++)
                {
                    // Nota: l'ordine nel JSON è grid[z][y][x]
                    int blockID = worldData.GetBlockId(x, y, z);

                    if (blockID < 0 || blockID >= worldData.tokenList.Count) continue;

                    string tokenName = worldData.tokenList[blockID];

                    // Salta l'aria
                    if (IsAirToken(tokenName)) continue;

                    // Hidden Block Removal: istanzia solo i blocchi con almeno una faccia esposta.
                    if (!HasAnyExposedFace(worldData, x, y, z, sizeX, sizeY, sizeZ))
                    {
                        culledCount++;
                        continue;
                    }

                    GameObject prefabToSpawn = FindPrefabByName(tokenName);

                    if (prefabToSpawn != null)
                    {
                        Vector3 position = new Vector3(x, y, z);
                        Instantiate(prefabToSpawn, position, Quaternion.identity, transform);
                        spawnedCount++;
                    }
                }
            }

            // DOPO aver completato un intero layer Y, aspetta
            if (delayBetweenLayers > 0)
                yield return new WaitForSeconds(delayBetweenLayers);
            else
                yield return null; // Aspetta solo il frame successivo
        }

        Debug.Log($"OPERATION COMPLETED! Blocks created: {spawnedCount} | Hidden blocks discarded: {culledCount}");
    }

    // --- Resto del codice invariato (per compatibilità con la tua struttura) ---

    void LoadPrefabList()
    {
#if UNITY_EDITOR
        blockPrefabs ??= new List<GameObject>();
        blockPrefabs.Clear();

        string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Prefabs/Blocks" });
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab != null)
                blockPrefabs.Add(prefab);
        }
        Debug.Log($"Prefabs loaded: {blockPrefabs.Count}");
#endif
    }

    GameObject FindPrefabByName(string tokenName)
    {
        string cleanName = tokenName.Replace("minecraft:", "").Trim();
        foreach (var p in blockPrefabs)
        {
            if (p != null && p.name.Equals(cleanName, StringComparison.OrdinalIgnoreCase))
                return p;
        }
        return null;
    }

    bool HasAnyExposedFace(StreamedWorldData data, int x, int y, int z, int sizeX, int sizeY, int sizeZ)
    {
        // Se almeno un vicino non e' solido (oppure siamo sul bordo del mondo), il blocco e' visibile.
        return !IsSolidBlock(data, x + 1, y, z, sizeX, sizeY, sizeZ)
            || !IsSolidBlock(data, x - 1, y, z, sizeX, sizeY, sizeZ)
            || !IsSolidBlock(data, x, y + 1, z, sizeX, sizeY, sizeZ)
            || !IsSolidBlock(data, x, y - 1, z, sizeX, sizeY, sizeZ)
            || !IsSolidBlock(data, x, y, z + 1, sizeX, sizeY, sizeZ)
            || !IsSolidBlock(data, x, y, z - 1, sizeX, sizeY, sizeZ);
    }

    bool IsSolidBlock(StreamedWorldData data, int x, int y, int z, int sizeX, int sizeY, int sizeZ)
    {
        if (x < 0 || y < 0 || z < 0 || x >= sizeX || y >= sizeY || z >= sizeZ)
            return false;

        int blockID = data.GetBlockId(x, y, z);
        if (blockID < 0 || blockID >= data.tokenList.Count)
            return false;

        string tokenName = data.tokenList[blockID];
        return !IsAirToken(tokenName);
    }

    bool IsAirToken(string tokenName)
    {
        if (string.IsNullOrEmpty(tokenName))
            return true;

        return tokenName.Contains("air", StringComparison.OrdinalIgnoreCase);
    }

    // Manteniamo SpawnWorld per compatibilità (non coroutine)
    [ContextMenu("Genera Tutto Subito (Rischio Freeze)")]
    public void SpawnWorld()
    {
        StopAllCoroutines();
        LoadPrefabList();

        // 1. Pulizia immediata dei figli gia' presenti
        int deletedCount = 0;
        List<GameObject> childrenToDelete = new List<GameObject>();
        foreach (Transform child in transform)
            childrenToDelete.Add(child.gameObject);

        foreach (GameObject child in childrenToDelete)
        {
            deletedCount++;
            if (Application.isPlaying)
                Destroy(child);
#if UNITY_EDITOR
            else
                DestroyImmediate(child);
#endif
        }

        // 2. Caricamento JSON via streaming
        if (!TryLoadWorldDataStreamed(out StreamedWorldData worldData, out string loadError))
        {
            Debug.LogError(loadError);
            return;
        }

        // Calcolo dimensioni
        int sizeZ = worldData.sizeZ;
        int sizeY = worldData.sizeY;
        int sizeX = worldData.sizeX;

        Debug.Log($"Sync generation started. Cleanup: {deletedCount} blocks removed. Size: X:{sizeX} Y:{sizeY} Z:{sizeZ}");

        int spawnedCount = 0;
        int culledCount = 0;

        // 3. Generazione completa (istantanea)
        for (int y = 0; y < sizeY; y++)
        {
            for (int z = 0; z < sizeZ; z++)
            {
                for (int x = 0; x < sizeX; x++)
                {
                    int blockID = worldData.GetBlockId(x, y, z);
                    if (blockID < 0 || blockID >= worldData.tokenList.Count)
                        continue;

                    string tokenName = worldData.tokenList[blockID];
                    if (IsAirToken(tokenName))
                        continue;

                    if (!HasAnyExposedFace(worldData, x, y, z, sizeX, sizeY, sizeZ))
                    {
                        culledCount++;
                        continue;
                    }

                    GameObject prefabToSpawn = FindPrefabByName(tokenName);
                    if (prefabToSpawn == null)
                        continue;

                    Vector3 position = new Vector3(x, y, z);
                    Instantiate(prefabToSpawn, position, Quaternion.identity, transform);
                    spawnedCount++;
                }
            }
        }

        Debug.Log($"OPERATION COMPLETED! Blocks created: {spawnedCount} | Hidden blocks discarded: {culledCount}");
    }

    bool TryLoadWorldDataStreamed(out StreamedWorldData worldData, out string error)
    {
        worldData = null;
        error = string.Empty;

        if (!TryGetJsonReaderFactory(out Func<TextReader> readerFactory, out string sourceDescription, out string sourceError))
        {
            error = sourceError;
            return false;
        }

        if (!TryReadMetadata(readerFactory, out List<string> tokenList, out int sizeX, out int sizeY, out int sizeZ, out string metadataError))
        {
            error = metadataError;
            return false;
        }

        long totalCellsLong = (long)sizeX * sizeY * sizeZ;
        if (totalCellsLong <= 0 || totalCellsLong > int.MaxValue)
        {
            error = $"ERRORE: dimensioni non valide o troppo grandi: X:{sizeX} Y:{sizeY} Z:{sizeZ}";
            return false;
        }

        int[] flatGrid = new int[(int)totalCellsLong];
        Array.Fill(flatGrid, -1);
        if (!TryReadGridIntoFlatArray(readerFactory, flatGrid, sizeX, sizeY, sizeZ, out string gridError))
        {
            error = gridError;
            return false;
        }

        worldData = new StreamedWorldData
        {
            tokenList = tokenList,
            flatGrid = flatGrid,
            sizeX = sizeX,
            sizeY = sizeY,
            sizeZ = sizeZ
        };

        Debug.Log($"JSON streamed from {sourceDescription}. Total cells: {flatGrid.Length}");
        return true;
    }

    bool TryGetJsonReaderFactory(out Func<TextReader> readerFactory, out string sourceDescription, out string error)
    {
        readerFactory = null;
        sourceDescription = string.Empty;
        error = string.Empty;

        string[] fileCandidates =
        {
            Path.Combine(Application.streamingAssetsPath, jsonFileName + ".json"),
            Path.Combine(Application.streamingAssetsPath, jsonFileName),
            Path.Combine(Application.dataPath, "Resources", jsonFileName + ".json"),
            Path.Combine(Application.dataPath, "Resources", jsonFileName + ".txt")
        };

        foreach (string path in fileCandidates)
        {
            if (!File.Exists(path))
                continue;

            readerFactory = () => new StreamReader(path);
            sourceDescription = path;
            return true;
        }

        TextAsset jsonAsset = Resources.Load<TextAsset>(jsonFileName);
        if (jsonAsset != null)
        {
            string cachedText = jsonAsset.text;
            readerFactory = () => new StringReader(cachedText);
            sourceDescription = $"Resources/{jsonFileName}";
            return true;
        }

        error = $"ERRORE: file JSON '{jsonFileName}' non trovato ne' in StreamingAssets ne' in Resources.";
        return false;
    }

    bool TryReadMetadata(Func<TextReader> readerFactory, out List<string> tokenList, out int sizeX, out int sizeY, out int sizeZ, out string error)
    {
        tokenList = new List<string>();
        sizeX = 0;
        sizeY = 0;
        sizeZ = 0;
        error = string.Empty;

        try
        {
            using TextReader textReader = readerFactory();
            using JsonTextReader reader = new JsonTextReader(textReader);

            if (!reader.Read() || reader.TokenType != JsonToken.StartObject)
            {
                error = "ERRORE: JSON non valido (oggetto root mancante).";
                return false;
            }

            while (reader.Read())
            {
                if (reader.TokenType == JsonToken.EndObject)
                    break;

                if (reader.TokenType != JsonToken.PropertyName)
                    continue;

                string propertyName = (string)reader.Value;

                if (propertyName == "token_list")
                {
                    if (!TryReadTokenList(reader, tokenList, out error))
                        return false;
                }
                else if (propertyName == "grid")
                {
                    if (!TryReadGridDimensions(reader, out sizeX, out sizeY, out sizeZ, out error))
                        return false;
                }
                else
                {
                    if (!reader.Read())
                    {
                        error = $"ERRORE: fine inaspettata leggendo la proprieta' '{propertyName}'.";
                        return false;
                    }
                    reader.Skip();
                }
            }

            if (tokenList.Count == 0)
            {
                error = "ERRORE: token_list assente o vuota.";
                return false;
            }

            if (sizeX <= 0 || sizeY <= 0 || sizeZ <= 0)
            {
                error = "ERRORE: griglia assente o dimensioni non valide.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"ERRORE lettura metadata JSON: {ex.Message}";
            return false;
        }
    }

    bool TryReadTokenList(JsonTextReader reader, List<string> tokenList, out string error)
    {
        error = string.Empty;

        if (!reader.Read() || reader.TokenType != JsonToken.StartArray)
        {
            error = "ERRORE: token_list non e' un array valido.";
            return false;
        }

        while (reader.Read())
        {
            if (reader.TokenType == JsonToken.EndArray)
                return true;

            if (reader.TokenType == JsonToken.String)
            {
                tokenList.Add((string)reader.Value);
                continue;
            }

            if (reader.TokenType == JsonToken.Null)
            {
                tokenList.Add(string.Empty);
                continue;
            }

            error = $"ERRORE: token non valido in token_list ({reader.TokenType}).";
            return false;
        }

        error = "ERRORE: fine inaspettata leggendo token_list.";
        return false;
    }

    bool TryReadGridDimensions(JsonTextReader reader, out int sizeX, out int sizeY, out int sizeZ, out string error)
    {
        sizeX = 0;
        sizeY = 0;
        sizeZ = 0;
        error = string.Empty;

        if (!reader.Read() || reader.TokenType != JsonToken.StartArray)
        {
            error = "ERRORE: grid non e' un array valido.";
            return false;
        }

        while (reader.Read())
        {
            if (reader.TokenType == JsonToken.EndArray)
                break;

            if (reader.TokenType != JsonToken.StartArray)
            {
                error = "ERRORE: formato grid[z] non valido.";
                return false;
            }

            int currentY = 0;
            while (reader.Read())
            {
                if (reader.TokenType == JsonToken.EndArray)
                    break;

                if (reader.TokenType != JsonToken.StartArray)
                {
                    error = "ERRORE: formato grid[z][y] non valido.";
                    return false;
                }

                int currentX = 0;
                while (reader.Read())
                {
                    if (reader.TokenType == JsonToken.EndArray)
                        break;

                    if (reader.TokenType != JsonToken.Integer)
                    {
                        error = $"ERRORE: valore non intero in grid ({reader.TokenType}).";
                        return false;
                    }

                    currentX++;
                }

                if (currentX > sizeX)
                    sizeX = currentX;

                currentY++;
            }

            if (currentY > sizeY)
                sizeY = currentY;

            sizeZ++;
        }

        if (sizeX <= 0 || sizeY <= 0 || sizeZ <= 0)
        {
            error = "ERRORE: dimensioni grid non valide.";
            return false;
        }

        return true;
    }

    bool TryReadGridIntoFlatArray(Func<TextReader> readerFactory, int[] flatGrid, int sizeX, int sizeY, int sizeZ, out string error)
    {
        error = string.Empty;

        try
        {
            using TextReader textReader = readerFactory();
            using JsonTextReader reader = new JsonTextReader(textReader);

            if (!reader.Read() || reader.TokenType != JsonToken.StartObject)
            {
                error = "ERRORE: JSON non valido (oggetto root mancante).";
                return false;
            }

            while (reader.Read())
            {
                if (reader.TokenType == JsonToken.EndObject)
                    break;

                if (reader.TokenType != JsonToken.PropertyName)
                    continue;

                string propertyName = (string)reader.Value;
                if (propertyName != "grid")
                {
                    if (!reader.Read())
                    {
                        error = $"ERRORE: fine inaspettata leggendo la proprieta' '{propertyName}'.";
                        return false;
                    }
                    reader.Skip();
                    continue;
                }

                if (!reader.Read() || reader.TokenType != JsonToken.StartArray)
                {
                    error = "ERRORE: grid non e' un array valido.";
                    return false;
                }

                int z = 0;
                while (reader.Read())
                {
                    if (reader.TokenType == JsonToken.EndArray)
                        break;

                    if (reader.TokenType != JsonToken.StartArray)
                    {
                        error = "ERRORE: formato grid[z] non valido durante il riempimento.";
                        return false;
                    }

                    int y = 0;
                    while (reader.Read())
                    {
                        if (reader.TokenType == JsonToken.EndArray)
                            break;

                        if (reader.TokenType != JsonToken.StartArray)
                        {
                            error = "ERRORE: formato grid[z][y] non valido durante il riempimento.";
                            return false;
                        }

                        int x = 0;
                        while (reader.Read())
                        {
                            if (reader.TokenType == JsonToken.EndArray)
                                break;

                            if (reader.TokenType != JsonToken.Integer)
                            {
                                error = $"ERRORE: valore non intero in grid durante il riempimento ({reader.TokenType}).";
                                return false;
                            }

                            if (x >= sizeX || y >= sizeY || z >= sizeZ)
                            {
                                error = "ERRORE: dimensioni grid incoerenti durante il riempimento.";
                                return false;
                            }

                            int blockId = Convert.ToInt32(reader.Value);
                            int index = ((z * sizeY) + y) * sizeX + x;
                            flatGrid[index] = blockId;
                            x++;
                        }

                        y++;
                    }

                    z++;
                }

                if (z != sizeZ)
                {
                    error = "ERRORE: dimensione Z incoerente durante il riempimento.";
                    return false;
                }

                return true;
            }

            error = "ERRORE: proprieta' grid non trovata nel JSON.";
            return false;
        }
        catch (Exception ex)
        {
            error = $"ERRORE lettura grid JSON: {ex.Message}";
            return false;
        }
    }

    void Start()
    {
        StartSpawnCoroutine();
    }
}