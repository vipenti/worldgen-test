using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Newtonsoft.Json.Linq;

public enum ModelResolution
{
    Low,     // Bassa risoluzione (es. max 32 voxel sul lato più lungo)
    Medium,  // Media risoluzione (es. max 64 voxel sul lato più lungo)
    High     // Alta risoluzione (es. max 128 voxel sul lato più lungo)
}

[Serializable]
public class ResolutionVoxelData
{
    public float voxelSize;
    public int count;
    public float[] positions;
    public float[] colors;
}

public class GenerateResolutions : MonoBehaviour
{
    [Header("Impostazioni Test")]
    public ModelResolution testResolution = ModelResolution.Medium;
    [TextArea(5, 15)]
    public string inputJson;
    [TextArea(5, 15)]
    public string outputJson;

    [Header("Impostazioni File")]
    public string testInputFilePath = "Assets/Json/objects/output.voxjson";
    public string testOutputFilePath = "Assets/Json/objects/output_low.voxjson";

    // Risoluzioni di griglia configurabili
    public static int LowGridSize = 8;
    public static int MediumGridSize = 16;
    public static int HighGridSize = 32;

    /// <summary>
    /// Uniforma il voxjson fornito in base al parametro di risoluzione specificato.
    /// </summary>
    public static string UniformVoxJson(string json, ModelResolution resolution)
    {
        int targetGridSize = GetTargetGridSize(resolution);
        return UniformVoxJson(json, targetGridSize);
    }

    /// <summary>
    /// Uniforma il voxjson fornito in base a una dimensione di griglia target sul lato più lungo.
    /// </summary>
    public static string UniformVoxJson(string json, int targetGridSize)
    {
        if (string.IsNullOrEmpty(json)) return json;

        try
        {
            ResolutionVoxelData data = ParseJson(json);
            if (data == null || data.count == 0) return json;

            // 1. Calcolo del Bounding Box
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;

            for (int i = 0; i < data.count; i++)
            {
                float x = data.positions[i * 3];
                float y = data.positions[i * 3 + 1];
                float z = data.positions[i * 3 + 2];

                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
                if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
            }

            float sizeX = maxX - minX;
            float sizeY = maxY - minY;
            float sizeZ = maxZ - minZ;
            float maxSize = Mathf.Max(sizeX, Mathf.Max(sizeY, sizeZ));

            if (maxSize < 0.001f) return json; // Modello vuoto o punto singolo

            // Rileviamo la risoluzione originale della griglia
            float originalVoxelSize = data.voxelSize > 0.001f ? data.voxelSize : 1.0f;
            int originalGridSize = Mathf.RoundToInt(maxSize / originalVoxelSize);

            // Ottimizzazione: se la risoluzione originale è già inferiore o uguale a quella target,
            // restituiamo il json originale senza alterarlo per evitare degradazioni
            if (originalGridSize <= targetGridSize)
            {
                return json;
            }

            // Calcolo della nuova dimensione dei voxel per la griglia target
            float newVoxelSize = maxSize / targetGridSize;

            // Raggruppamento e media dei colori sui nuovi nodi della griglia
            var cells = new Dictionary<Vector3Int, CellData>();

            int positionsLength = data.positions.Length;
            int colorsLength = data.colors != null ? data.colors.Length : 0;

            for (int i = 0; i < data.count; i++)
            {
                if (i * 3 + 2 >= positionsLength) break;

                float px = data.positions[i * 3];
                float py = data.positions[i * 3 + 1];
                float pz = data.positions[i * 3 + 2];

                float cx = 1f, cy = 1f, cz = 1f;
                if (colorsLength > i * 3 + 2)
                {
                    cx = data.colors[i * 3];
                    cy = data.colors[i * 3 + 1];
                    cz = data.colors[i * 3 + 2];
                }

                // Troviamo le coordinate assolute della cella sulla nuova griglia
                // Utilizziamo FloorToInt(x + 0.5f) anziché RoundToInt per prevenire il Banker's Rounding
                // (round half-to-even) che causerebbe la perdita sistematica di interi dispari.
                int ix = Mathf.FloorToInt(px / newVoxelSize + 0.5f);
                int iy = Mathf.FloorToInt(py / newVoxelSize + 0.5f);
                int iz = Mathf.FloorToInt(pz / newVoxelSize + 0.5f);

                var key = new Vector3Int(ix, iy, iz);

                if (!cells.TryGetValue(key, out var cell))
                {
                    cell = new CellData();
                    cells[key] = cell;
                }

                cell.sumR += cx;
                cell.sumG += cy;
                cell.sumB += cz;
                cell.count++;
            }

            // Costruiamo i dati della griglia uniformata
            int newCount = cells.Count;
            float[] newPositions = new float[newCount * 3];
            float[] newColors = new float[newCount * 3];

            int idx = 0;
            foreach (var kvp in cells)
            {
                var cell = kvp.Value;
                Vector3Int coord = kvp.Key;

                // Calcolo della posizione del voxel allineata perfettamente alla griglia
                newPositions[idx * 3]     = coord.x * newVoxelSize;
                newPositions[idx * 3 + 1] = coord.y * newVoxelSize;
                newPositions[idx * 3 + 2] = coord.z * newVoxelSize;

                // Calcolo del colore medio (effetto anti-aliasing cromatico)
                newColors[idx * 3]     = cell.sumR / cell.count;
                newColors[idx * 3 + 1] = cell.sumG / cell.count;
                newColors[idx * 3 + 2] = cell.sumB / cell.count;

                idx++;
            }

            var downsampled = new ResolutionVoxelData
            {
                voxelSize = newVoxelSize,
                count = newCount,
                positions = newPositions,
                colors = newColors
            };

            return JsonUtility.ToJson(downsampled);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[GenerateResolutions] Errore nell'uniformare il voxjson: {ex.Message}");
            return json;
        }
    }

    /// <summary>
    /// Uniforma un file voxjson su disco salvando il risultato in un altro percorso.
    /// </summary>
    public static void UniformFile(string inputFilePath, string outputFilePath, ModelResolution resolution)
    {
        try
        {
            if (!File.Exists(inputFilePath))
            {
                Debug.LogError($"[GenerateResolutions] File di input non trovato: {inputFilePath}");
                return;
            }

            string inputJsonContent = File.ReadAllText(inputFilePath);
            string outputJsonContent = UniformVoxJson(inputJsonContent, resolution);

            string directory = Path.GetDirectoryName(outputFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(outputFilePath, outputJsonContent);
            Debug.Log($"[GenerateResolutions] File uniformato a {resolution} salvato in: {outputFilePath}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[GenerateResolutions] Errore nell'uniformare il file: {ex.Message}");
        }
    }

    private static int GetTargetGridSize(ModelResolution resolution)
    {
        switch (resolution)
        {
            case ModelResolution.Low:
                return LowGridSize;
            case ModelResolution.Medium:
                return MediumGridSize;
            case ModelResolution.High:
                return HighGridSize;
            default:
                return MediumGridSize;
        }
    }

    private static ResolutionVoxelData ParseJson(string json)
    {
        var root = JObject.Parse(json);
        if (root.ContainsKey("gridSize") || root.ContainsKey("voxels"))
        {
            return ParseGridFormat(root);
        }
        return JsonUtility.FromJson<ResolutionVoxelData>(json);
    }

    private static ResolutionVoxelData ParseGridFormat(JObject root)
    {
        float size = 1.0f; // Voxel size di fallback per il formato a griglia
        var jVoxels = root["voxels"] as JArray;
        int count = jVoxels?.Count ?? 0;

        var positions = new float[count * 3];
        var colors = new float[count * 3];

        for (int i = 0; i < count; i++)
        {
            var v = jVoxels[i] as JArray;
            positions[i * 3]     = (float)v[0] * size;
            positions[i * 3 + 1] = (float)v[1] * size;
            positions[i * 3 + 2] = (float)v[2] * size;

            // Colore di default bianco per formati senza colore
            colors[i * 3]     = 1.0f;
            colors[i * 3 + 1] = 1.0f;
            colors[i * 3 + 2] = 1.0f;
        }

        return new ResolutionVoxelData
        {
            voxelSize = size,
            count = count,
            positions = positions,
            colors = colors
        };
    }

    [ContextMenu("Test Downsample (Inspector Text)")]
    public void TestDownsampleInspectorText()
    {
        if (string.IsNullOrEmpty(inputJson))
        {
            Debug.LogWarning("[GenerateResolutions] Il campo inputJson è vuoto!");
            return;
        }
        outputJson = UniformVoxJson(inputJson, testResolution);
        Debug.Log("[GenerateResolutions] Downsampling completato per il testo dell'ispettore!");
    }

    [ContextMenu("Test Downsample (Files on Disk)")]
    public void TestDownsampleFilesOnDisk()
    {
        UniformFile(testInputFilePath, testOutputFilePath, testResolution);
    }

    private class CellData
    {
        public float sumR;
        public float sumG;
        public float sumB;
        public int count;
    }
}
