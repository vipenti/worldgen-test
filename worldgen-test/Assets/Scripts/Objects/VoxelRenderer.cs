using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Newtonsoft.Json.Linq;
using System.Linq;

#if UNITY_EDITOR
using UnityEditor;
#endif

[Serializable]
class VoxelData
{
    public float voxelSize;
    public int   count;
    public float[] positions;  
    public float[] colors;     
}

public class VoxelRenderer : MonoBehaviour
{
    public static event Action<string, GameObject> OnPrefabSaved;
    public static Dictionary<string, Mesh> RuntimeMeshCache = new Dictionary<string, Mesh>();
    public static Material RuntimeMaterialInstance;

    [Header("Data")]
    public string jsonFolder   = "Assets/Json/objects";
    public string jsonFileName = "output.voxjson";

    [Header("Fallback (grid format)")]
    public float defaultVoxelSize = 1f;
    public Color defaultColor     = Color.white;

    string JsonPath => Path.Combine(PathSettings.GetObjectsFolder(), jsonFileName).Replace('\\', '/');

    [Header("Export & Build Stability")]
    public string assetFolder = "Assets/GeneratedAsset";
    public Material voxelMaterialTemplate; 
    [Tooltip("Rimuove questo numero di colonne di voxel a partire dalla sinistra nativa (asse Z di Trellis) per eliminare la pedana.")]
    public int baseBlocksToRemove = 5; 

    private VoxelData _data;

    public void Reload()
    {
        string assetName = Path.GetFileNameWithoutExtension(jsonFileName);
        string jsonPath = JsonPath;
        if (File.Exists(jsonPath))
        {
            string rawJson = File.ReadAllText(jsonPath);
            ProcessVoxelModel(rawJson, assetName);
        }
        else
        {
            Debug.LogError($"[VoxelRenderer] File JSON originale non trovato per il reload in: {jsonPath}");
        }
    }

    public void LoadFromJson(string json, string assetName = "object")
    {
        jsonFileName = assetName + ".voxjson";
        ProcessVoxelModel(json, assetName);
    }

    private void ProcessVoxelModel(string rawJson, string assetName)
    {
        if (string.IsNullOrEmpty(rawJson)) return;

        // Check if this is already an LOD name (ends with _low, _medium, or _high)
        bool isLOD = assetName.EndsWith("_low") || assetName.EndsWith("_medium") || assetName.EndsWith("_high");

        if (isLOD)
        {
            VoxelData data = ParseJson(rawJson);
            _data = data;
            GenerateRuntimeMeshForData(assetName, data);
#if UNITY_EDITOR
            SaveAsAssetForData(assetName, data);
#endif
            return;
        }

        // For original/base model:
        VoxelData dataOriginal = ParseJson(rawJson);
        _data = dataOriginal;
        GenerateRuntimeMeshForData(assetName, dataOriginal);
#if UNITY_EDITOR
        SaveAsAssetForData(assetName, dataOriginal);
#endif

        // Generate/save LOD files only in the Editor and only if they do not exist.
        // In the built player, LODs are loaded from individual pre-existing JSONs.
#if UNITY_EDITOR
        string baseFolder = PathSettings.GetObjectsFolder();
        string lowPath = Path.Combine(baseFolder, assetName + "_low.voxjson");
        string medPath = Path.Combine(baseFolder, assetName + "_medium.voxjson");
        string highPath = Path.Combine(baseFolder, assetName + "_high.voxjson");

        if (!File.Exists(lowPath) || !File.Exists(medPath) || !File.Exists(highPath))
        {
            Debug.Log($"[VoxelRenderer] Generating LODs for '{assetName}' because they do not exist on disk.");
            string jsonLow = GenerateResolutions.UniformVoxJson(rawJson, ModelResolution.Low);
            string jsonMedium = GenerateResolutions.UniformVoxJson(rawJson, ModelResolution.Medium);

            // Small/tiny objects (small rocks, pebbles, flowers...) never need the full "high"
            // voxel density — cap their top LOD tier at medium so we don't waste cubes on detail
            // nobody will notice at that scale.
            ModelResolution highResolution = IsSmallOrTinySize(assetName) ? ModelResolution.Medium : ModelResolution.High;
            string jsonHigh = GenerateResolutions.UniformVoxJson(rawJson, highResolution);

            try
            {
                if (!Directory.Exists(baseFolder)) Directory.CreateDirectory(baseFolder);
                File.WriteAllText(lowPath, jsonLow);
                File.WriteAllText(medPath, jsonMedium);
                File.WriteAllText(highPath, jsonHigh);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VoxelRenderer] Errore nel salvataggio dei file voxjson su disco: {ex.Message}");
            }

            VoxelData dataLow = ParseJson(jsonLow);
            VoxelData dataMedium = ParseJson(jsonMedium);
            VoxelData dataHigh = ParseJson(jsonHigh);

            GenerateRuntimeMeshForData(assetName + "_low", dataLow);
            GenerateRuntimeMeshForData(assetName + "_medium", dataMedium);
            GenerateRuntimeMeshForData(assetName + "_high", dataHigh);

            SaveAsAssetForData(assetName + "_low", dataLow);
            SaveAsAssetForData(assetName + "_medium", dataMedium);
            SaveAsAssetForData(assetName + "_high", dataHigh);
        }
#endif
    }

    static bool IsSmallOrTinySize(string assetName)
    {
        try
        {
            string path = Path.Combine(PathSettings.GetJsonFolder(), "current_objects.json");
            if (!File.Exists(path)) return false;

            var root = JObject.Parse(File.ReadAllText(path));
            foreach (var entry in root)
            {
                if (!string.Equals(entry.Key, assetName, StringComparison.OrdinalIgnoreCase)) continue;
                string size = entry.Value?["size"]?.ToString();
                return size == "small" || size == "tiny";
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[VoxelRenderer] Impossibile leggere la size di '{assetName}' da current_objects.json: {ex.Message}");
        }
        return false;
    }

    void GenerateRuntimeMeshForData(string assetName, VoxelData data)
    {
        if (data == null) return;

        Mesh mesh = BuildOptimizedMesh(data, baseBlocksToRemove);
        mesh.name = assetName;

        Mesh runtimeClone = Instantiate(mesh);
        runtimeClone.name = assetName;

        if (RuntimeMeshCache.TryGetValue(assetName, out Mesh oldMesh) && oldMesh != null)
        {
            DestroyImmediate(oldMesh);
        }
        RuntimeMeshCache[assetName] = runtimeClone;

        if (RuntimeMaterialInstance == null)
        {
            if (voxelMaterialTemplate != null)
                RuntimeMaterialInstance = voxelMaterialTemplate;
            else
            {
                var vcShader = Shader.Find("Custom/VoxelVertexColor");
                RuntimeMaterialInstance = vcShader != null ? new Material(vcShader) : new Material(Shader.Find("Standard"));
            }
        }
    }

    VoxelData ParseJson(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;

        // Try standard JSON format using Unity's fast native JsonUtility first to avoid Newtonsoft overhead.
        if (json.Contains("\"positions\"") && json.Contains("\"colors\""))
        {
            try
            {
                var data = JsonUtility.FromJson<VoxelData>(json);
                if (data != null && data.positions != null && data.positions.Length > 0)
                {
                    return data;
                }
            }
            catch
            {
                // Fallback to JObject if JsonUtility fails
            }
        }

        try
        {
            var root = JObject.Parse(json);
            if (root.ContainsKey("gridSize") || root.ContainsKey("voxels"))
                return ParseGridFormat(root);
            return JsonUtility.FromJson<VoxelData>(json);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[VoxelRenderer] Failed to parse voxel JSON: {ex.Message}");
            return null;
        }
    }

    VoxelData ParseGridFormat(JObject root)
    {
        float size = defaultVoxelSize;
        var   jVoxels = root["voxels"] as JArray;
        int   count = jVoxels?.Count ?? 0;

        var positions = new float[count * 3];
        var colors    = new float[count * 3];

        for (int i = 0; i < count; i++)
        {
            var v = jVoxels[i] as JArray;
            positions[i * 3]     = (float)v[0] * size;
            positions[i * 3 + 1] = (float)v[1] * size;
            positions[i * 3 + 2] = (float)v[2] * size;
            colors[i * 3]        = defaultColor.r;
            colors[i * 3 + 1]    = defaultColor.g;
            colors[i * 3 + 2]    = defaultColor.b;
        }

        return new VoxelData { voxelSize = size, count = count, positions = positions, colors = colors };
    }

#if UNITY_EDITOR
    void OnEnable()  => UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeChanged;
    void OnDisable() => UnityEditor.EditorApplication.playModeStateChanged -= OnPlayModeChanged;

    void OnPlayModeChanged(UnityEditor.PlayModeStateChange state)
    {
        if (state != UnityEditor.PlayModeStateChange.ExitingEditMode) return;
        Reload();
    }

    void SaveAsAssetForData(string assetName, VoxelData data)
    {
        if (data == null) return;
        EnsureFolder(assetFolder);

        string prefabPath = $"{assetFolder}/{assetName}.prefab";
        string vcMatPath  = $"{assetFolder}/VoxelVertexColor.mat";
        
        var vcMat = AssetDatabase.LoadAssetAtPath<Material>(vcMatPath);
        if (vcMat == null)
        {
            vcMat = new Material(Shader.Find("Custom/VoxelVertexColor")) { name = "VoxelVertexColor" };
            AssetDatabase.CreateAsset(vcMat, vcMatPath);
        }

        if (!RuntimeMeshCache.TryGetValue(assetName, out Mesh mesh) || mesh == null)
        {
            mesh = BuildOptimizedMesh(data, baseBlocksToRemove);
            mesh.name = assetName;
        }

        // Clone the mesh for the persistent asset, so the cached mesh remains a runtime-only object.
        Mesh assetMesh = Instantiate(mesh);
        assetMesh.name = assetName;

        AssetDatabase.DeleteAsset(prefabPath);
        var go = new GameObject(assetName);
        go.AddComponent<MeshFilter>().sharedMesh      = assetMesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = vcMat;
        go.AddComponent<MeshCollider>().sharedMesh     = assetMesh;
        var savedPrefab = PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
        DestroyImmediate(go);

        AssetDatabase.AddObjectToAsset(assetMesh, prefabPath);
        savedPrefab.GetComponent<MeshFilter>().sharedMesh = assetMesh;
        if (savedPrefab.GetComponent<MeshCollider>() != null)
        {
            savedPrefab.GetComponent<MeshCollider>().sharedMesh = assetMesh;
        }
        PrefabUtility.SavePrefabAsset(savedPrefab);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        
        OnPrefabSaved?.Invoke(assetName, savedPrefab);
    }

    static void EnsureFolder(string path)
    {
        var parts = path.Split('/'); string cur = parts[0];
        for (int i = 1; i < parts.Length; i++) {
            string next = cur + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, parts[i]);
            cur = next;
        }
    }
#endif

    static Mesh BuildOptimizedMesh(VoxelData originalData, int baseLayersToRemove = 0)
    {
        if (originalData == null || originalData.count == 0) return new Mesh();
        VoxelData data = originalData;

        // 1. FILTRAGGIO DELLA PEDANA SULLA Z NATIVA GREZZA
        if (baseLayersToRemove > 0)
        {
            var uniqueZKeys = new HashSet<int>();
            List<float> uniqueZ = new List<float>();
            for (int i = 0; i < originalData.count; i++)
            {
                float zVal = originalData.positions[i * 3 + 2]; 
                int key = Mathf.RoundToInt(zVal * 1000f);
                if (uniqueZKeys.Add(key))
                {
                    uniqueZ.Add(zVal);
                }
            }
            uniqueZ.Sort(); 

            if (uniqueZ.Count > baseLayersToRemove)
            {
                float cutoffZ = uniqueZ[baseLayersToRemove] - 0.001f;
                List<float> cleanPositions = new List<float>();
                List<float> cleanColors = new List<float>();

                for (int i = 0; i < originalData.count; i++)
                {
                    float wz = originalData.positions[i * 3 + 2];
                    if (wz < cutoffZ) continue; 

                    cleanPositions.Add(originalData.positions[i * 3]);
                    cleanPositions.Add(originalData.positions[i * 3 + 1]);
                    cleanPositions.Add(wz);
                    cleanColors.Add(originalData.colors[i * 3]);
                    cleanColors.Add(originalData.colors[i * 3 + 1]);
                    cleanColors.Add(originalData.colors[i * 3 + 2]);
                }

                data = new VoxelData {
                    voxelSize = originalData.voxelSize, count = cleanPositions.Count / 3,
                    positions = cleanPositions.ToArray(), colors = cleanColors.ToArray()
                };
            }
        }

        float s = data.voxelSize > 0.001f ? data.voxelSize : 1.0f;
        float h = s * 0.5f;

        var occupied = new HashSet<Vector3Int>(data.count);
        for (int i = 0; i < data.count; i++)
        {
            occupied.Add(new Vector3Int(
                Mathf.RoundToInt(data.positions[i * 3]     / s),
                Mathf.RoundToInt(data.positions[i * 3 + 1] / s),
                Mathf.RoundToInt(data.positions[i * 3 + 2] / s)));
        }

        // 2. REPERIMENTO DEL BOUNDING BOX SUI DATI ORIGINARI DISCRETI
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

        float normScale = maxSize > 0.001f ? (1.0f / maxSize) : 1.0f;
        float centerX = (minX + maxX) / 2f;
        float centerY = (minY + maxY) / 2f;

        // FIX CRITICO DEFINITIVO COORDINATE: Allineati i vettori e ripristinata l'integrità dei quad
        var faces = new (Vector3Int dir, Vector3[] verts)[]
        {
            (Vector3Int.right,         new[]{ new Vector3( h,-h,-h), new Vector3( h, h,-h), new Vector3( h, h, h), new Vector3( h,-h, h) }),
            (Vector3Int.left,          new[]{ new Vector3(-h,-h, h), new Vector3(-h, h, h), new Vector3(-h, h,-h), new Vector3(-h,-h,-h) }),
            (Vector3Int.up,            new[]{ new Vector3(-h, h,-h), new Vector3(-h, h, h), new Vector3( h, h, h), new Vector3( h, h,-h) }),
            (Vector3Int.down,          new[]{ new Vector3(-h,-h, h), new Vector3(-h,-h,-h), new Vector3( h,-h,-h), new Vector3( h,-h, h) }), 
            (new Vector3Int( 0, 0, 1), new[]{ new Vector3( h,-h, h), new Vector3( h, h, h), new Vector3(-h, h, h), new Vector3(-h,-h, h) }),
            (new Vector3Int( 0, 0,-1), new[]{ new Vector3(-h,-h,-h), new Vector3(-h, h,-h), new Vector3( h, h,-h), new Vector3( h,-h,-h) }), 
        };

        var verts3 = new List<Vector3>(); var tris = new List<int>(); var cols = new List<Color>();

        for (int i = 0; i < data.count; i++)
        {
            var grid = new Vector3Int(
                Mathf.RoundToInt(data.positions[i * 3]     / s),
                Mathf.RoundToInt(data.positions[i * 3 + 1] / s),
                Mathf.RoundToInt(data.positions[i * 3 + 2] / s));

            var worldRaw = new Vector3(data.positions[i * 3], data.positions[i * 3 + 1], data.positions[i * 3 + 2]);
            var col      = new Color(data.colors[i * 3], data.colors[i * 3 + 1], data.colors[i * 3 + 2]);

            foreach (var (dir, fv) in faces)
            {
                if (occupied.Contains(grid + dir)) continue;

                int b = verts3.Count;
                foreach (var v in fv) 
                { 
                    Vector3 rawVertex = worldRaw + v;

                    // Centratura e scalatura applicata unicamente nello spazio locale di emissione
                    Vector3 normalizedVertex = new Vector3(
                        (rawVertex.x - centerX) * normScale,
                        (rawVertex.y - centerY) * normScale,
                        (rawVertex.z - minZ) * normScale
                    );

                    verts3.Add(normalizedVertex); 
                    cols.Add(col); 
                }
                tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
                tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);
            }
        }

        var mesh = new Mesh {
            name = "VoxelMesh",
            indexFormat = verts3.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16
        };
        mesh.SetVertices(verts3); mesh.SetTriangles(tris, 0); mesh.SetColors(cols);
        mesh.RecalculateNormals(); mesh.RecalculateBounds(); mesh.Optimize();
        return mesh;
    }
}