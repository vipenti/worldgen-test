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
    public float[] positions;  // flat: x0,y0,z0, x1,y1,z1, ...
    public float[] colors;     // flat: r0,g0,b0, r1,g1,b1, ...
}

public class VoxelRenderer : MonoBehaviour
{
    [Header("Data")]
    public string jsonFolder   = "Assets/Json/objects";
    public string jsonFileName = "output.voxjson";

    [Header("Fallback (grid format)")]
    public float defaultVoxelSize = 1f;
    public Color defaultColor     = Color.white;

    string JsonPath => Path.Combine(jsonFolder, jsonFileName).Replace('\\', '/');

    [Header("Export")]
    public string assetFolder = "Assets/GeneratedAsset";

    private VoxelData _data;

    public void Reload()
    {
        _data = ParseJson(File.ReadAllText(JsonPath));
#if UNITY_EDITOR
        SaveAsAsset();
#endif
    }

    public void LoadFromJson(string json, string assetName = "object")
    {
        jsonFileName = assetName + ".voxjson";
        _data = ParseJson(json);
#if UNITY_EDITOR
        SaveAsAsset();
#endif
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

    [ContextMenu("Save as 3D Asset")]
    void SaveAsAsset()
    {
        if (_data == null)
            _data = ParseJson(File.ReadAllText(JsonPath));

        EnsureFolder(assetFolder);

        var    assetName  = Path.GetFileNameWithoutExtension(jsonFileName);
        string prefabPath = $"{assetFolder}/{assetName}.prefab";

        string vcMatPath = $"{assetFolder}/VoxelVertexColor.mat";
        var    vcShader  = Shader.Find("Custom/VoxelVertexColor");
        if (vcShader == null)
            Debug.LogError("VoxelRenderer: shader 'Custom/VoxelVertexColor' non trovato!");
        var vcMat = AssetDatabase.LoadAssetAtPath<Material>(vcMatPath);
        if (vcMat == null)
        {
            vcMat = new Material(vcShader) { name = "VoxelVertexColor" };
            AssetDatabase.CreateAsset(vcMat, vcMatPath);
        }
        else
        {
            vcMat.shader = vcShader;
            EditorUtility.SetDirty(vcMat);
        }

        var mesh = BuildOptimizedMesh(_data);
        mesh.name = assetName;

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
        Debug.Log($"VoxelRenderer: prefab salvato → {prefabPath}");
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

    static Mesh BuildOptimizedMesh(VoxelData data)
    {
        float h = data.voxelSize * 0.5f;
        float s = data.voxelSize;

        var occupied = new HashSet<Vector3Int>(data.count);
        for (int i = 0; i < data.count; i++)
            occupied.Add(new Vector3Int(
                Mathf.RoundToInt(data.positions[i * 3]     / s),
                Mathf.RoundToInt(data.positions[i * 3 + 1] / s),
                Mathf.RoundToInt(data.positions[i * 3 + 2] / s)));

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
            var grid = new Vector3Int(
                Mathf.RoundToInt(data.positions[i * 3]     / s),
                Mathf.RoundToInt(data.positions[i * 3 + 1] / s),
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
