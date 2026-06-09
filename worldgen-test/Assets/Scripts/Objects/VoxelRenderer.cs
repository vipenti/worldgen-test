using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Newtonsoft.Json.Linq;
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

    // Global runtime volatile memory caches accessible by standalone builds
    public static Dictionary<string, Mesh> RuntimeMeshCache = new Dictionary<string, Mesh>();
    public static Material RuntimeMaterialInstance;

    [Header("Data")]
    public string jsonFolder   = "Assets/Json/objects";
    public string jsonFileName = "output.voxjson";

    [Header("Fallback (grid format)")]
    public float defaultVoxelSize = 1f;
    public Color defaultColor     = Color.white;

    string JsonPath => Path.Combine(jsonFolder, jsonFileName).Replace('\\', '/');

    [Header("Export & Build Stability")]
    public string assetFolder = "Assets/GeneratedAsset";
    [Tooltip("Assign your VoxelVertexColor material here to guarantee the shader is compiled into standalone builds.")]
    public Material voxelMaterialTemplate; 
    [Tooltip("Strip this many voxel layers from the bottom of every generated model (hides the base/plinth).")]
    public int baseBlocksToRemove = 3;

    private VoxelData _data;

    public void Reload()
    {
        _data = ParseJson(File.ReadAllText(JsonPath));
        
        // Step 1: Generates the structural mesh directly into RAM (Runs everywhere, Editor & Standalone Build)
        GenerateRuntimeMesh(Path.GetFileNameWithoutExtension(jsonFileName));

#if UNITY_EDITOR
        // Step 2: Serializes the file asset to disk (Runs ONLY inside the Unity Editor layout)
        SaveAsAsset();
#endif
    }

    public void LoadFromJson(string json, string assetName = "object")
    {
        jsonFileName = assetName + ".voxjson";
        _data = ParseJson(json);
        
        // Generates mesh memory pointers natively during active play sessions
        GenerateRuntimeMesh(assetName);

#if UNITY_EDITOR
        SaveAsAsset();
#endif
    }

    /// <summary>
    /// Builds the optimized Voxel Mesh data layout directly inside volatile RAM.
    /// This bypasses disk read/write blocks, allowing standalone builds to stream meshes in real-time.
    /// </summary>
    void GenerateRuntimeMesh(string assetName)
    {
        if (_data == null) return;

        Mesh mesh = BuildOptimizedMesh(_data, baseBlocksToRemove);
        mesh.name = assetName;
        
        // Pushes the live mesh instance into the global thread safe runtime dictionary
        RuntimeMeshCache[assetName] = mesh;

        // Binds or instantiates the vertex shader coloring material safely cross-platform
        if (RuntimeMaterialInstance == null)
        {
            if (voxelMaterialTemplate != null)
            {
                RuntimeMaterialInstance = voxelMaterialTemplate;
            }
            else
            {
                var vcShader = Shader.Find("Custom/VoxelVertexColor");
                RuntimeMaterialInstance = vcShader != null ? new Material(vcShader) : new Material(Shader.Find("Standard"));
            }
        }
    }

    VoxelData ParseJson(string json)
    {
        var root = JObject.Parse(json);
        if (root.ContainsKey("gridSize") || root.ContainsKey("voxels"))
            return ParseGridFormat(root);
        return JsonUtility.FromJson<VoxelData>(json);
    }

    VoxelData ParseGridFormat(JObject root)
    {
        float size    = defaultVoxelSize;
        var   jVoxels = root["voxels"] as JArray;
        int   count   = jVoxels?.Count ?? 0;

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
        _data = ParseJson(File.ReadAllText(JsonPath));
        SaveAsAsset();
    }

    void SaveAsAsset()
    {
        if (_data == null) _data = ParseJson(File.ReadAllText(JsonPath));

        EnsureFolder(assetFolder);

        var    assetName  = Path.GetFileNameWithoutExtension(jsonFileName);
        string prefabPath = $"{assetFolder}/{assetName}.prefab";

        string vcMatPath = $"{assetFolder}/VoxelVertexColor.mat";
        var    vcShader  = Shader.Find("Custom/VoxelVertexColor");
        
        var vcMat = AssetDatabase.LoadAssetAtPath<Material>(vcMatPath);
        if (vcMat == null)
        {
            vcMat = new Material(vcShader) { name = "VoxelVertexColor" };
            AssetDatabase.CreateAsset(vcMat, vcMatPath);
        }

        // Retrieves the pre-calculated mesh reference safely
        if (!RuntimeMeshCache.TryGetValue(assetName, out Mesh mesh))
        {
            mesh = BuildOptimizedMesh(_data, baseBlocksToRemove);
            mesh.name = assetName;
        }

        AssetDatabase.DeleteAsset(prefabPath);

        var go = new GameObject(assetName);
        go.AddComponent<MeshFilter>().sharedMesh      = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = vcMat;
        go.AddComponent<MeshCollider>().sharedMesh     = mesh;
        var savedPrefab = PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
        DestroyImmediate(go);

        AssetDatabase.AddObjectToAsset(mesh, prefabPath);
        savedPrefab.GetComponent<MeshFilter>().sharedMesh = mesh;
        PrefabUtility.SavePrefabAsset(savedPrefab);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        
        OnPrefabSaved?.Invoke(assetName, savedPrefab);
    }

    static void EnsureFolder(string path)
    {
        var parts  = path.Split('/');
        string cur = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = cur + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(cur, parts[i]);
            cur = next;
        }
    }
#endif

    static Mesh BuildOptimizedMesh(VoxelData data, int baseLayersToRemove = 0)
    {
        float h = data.voxelSize * 0.5f;
        float s = data.voxelSize;

        int minGridY = int.MaxValue;
        for (int i = 0; i < data.count; i++)
        {
            int gy = Mathf.RoundToInt(data.positions[i * 3 + 1] / s);
            if (gy < minGridY) minGridY = gy;
        }
        int cutoffY = (minGridY == int.MaxValue) ? int.MinValue : minGridY + baseLayersToRemove;

        var occupied = new HashSet<Vector3Int>(data.count);
        for (int i = 0; i < data.count; i++)
        {
            int gy = Mathf.RoundToInt(data.positions[i * 3 + 1] / s);
            if (gy < cutoffY) continue;
            occupied.Add(new Vector3Int(
                Mathf.RoundToInt(data.positions[i * 3]     / s),
                gy,
                Mathf.RoundToInt(data.positions[i * 3 + 2] / s)));
        }

        var faces = new (Vector3Int dir, Vector3[] verts)[]
        {
            (Vector3Int.right,         new[]{ new Vector3( h,-h,-h), new Vector3( h, h,-h), new Vector3( h, h, h), new Vector3( h,-h, h) }),
            (Vector3Int.left,          new[]{ new Vector3(-h,-h, h), new Vector3(-h, h, h), new Vector3(-h, h,-h), new Vector3(-h,-h,-h) }),
            (Vector3Int.up,            new[]{ new Vector3(-h, h,-h), new Vector3(-h, h, h), new Vector3( h, h, h), new Vector3( h, h,-h) }),
            (Vector3Int.down,          new[]{ new Vector3(-h,-h, h), new Vector3(-h,-h,-h), new Vector3( h,-h,-h), new Vector3( h,-h, h) }),
            (new Vector3Int( 0, 0, 1), new[]{ new Vector3( h,-h, h), new Vector3( h, h, h), new Vector3(-h, h, h), new Vector3(-h,-h, h) }),
            (new Vector3Int( 0, 0,-1), new[]{ new Vector3(-h,-h,-h), new Vector3(-h, h,-h), new Vector3( h, h,-h), new Vector3( h,-h,-h) }),
        };

        var verts3 = new List<Vector3>();
        var tris   = new List<int>();
        var cols   = new List<Color>();

        for (int i = 0; i < data.count; i++)
        {
            int gyI = Mathf.RoundToInt(data.positions[i * 3 + 1] / s);
            if (gyI < cutoffY) continue;

            var grid = new Vector3Int(
                Mathf.RoundToInt(data.positions[i * 3]     / s),
                gyI,
                Mathf.RoundToInt(data.positions[i * 3 + 2] / s));

            var world = new Vector3(data.positions[i * 3], data.positions[i * 3 + 1], data.positions[i * 3 + 2]);
            var col   = new Color(data.colors[i * 3], data.colors[i * 3 + 1], data.colors[i * 3 + 2]);

            foreach (var (dir, fv) in faces)
            {
                if (occupied.Contains(grid + dir)) continue;

                int b = verts3.Count;
                foreach (var v in fv) { verts3.Add(world + v); cols.Add(col); }
                tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
                tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);
            }
        }

        var mesh = new Mesh
        {
            name        = "VoxelMesh",
            indexFormat = verts3.Count > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16,
        };
        mesh.SetVertices(verts3);
        mesh.SetTriangles(tris, 0);
        mesh.SetColors(cols);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mesh.Optimize();
        return mesh;
    }
}