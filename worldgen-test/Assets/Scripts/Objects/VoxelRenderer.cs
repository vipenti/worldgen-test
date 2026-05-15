using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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

    [Header("Rendering")]
    public Mesh     cubeMesh;
    public Material voxelMaterial;

    [Header("Export")]
    public string assetFolder = "Assets/GeneratedAsset";

    private Matrix4x4[][]          _batches;
    private MaterialPropertyBlock[] _propBlocks;
    private VoxelData               _data;

    const int BATCH = 1023;

    void OnEnable()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeChanged;
#endif
    }

    void OnDisable()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.playModeStateChanged -= OnPlayModeChanged;
#endif
    }

    public void Reload()
    {
        _data = ParseJson(File.ReadAllText(JsonPath));
        BuildBatches(_data);
#if UNITY_EDITOR
        SaveAsAsset();
#endif
    }

    public void LoadFromJson(string json, string assetName = "object")
    {
        jsonFileName = assetName + ".voxjson";
        _data = ParseJson(json);
        BuildBatches(_data);
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

    void BuildBatches(VoxelData data)
    {
        int total      = data.count;
        int batchCount = Mathf.CeilToInt((float)total / BATCH);
        _batches    = new Matrix4x4[batchCount][];
        _propBlocks = new MaterialPropertyBlock[batchCount];

        var scale = Vector3.one * data.voxelSize;

        for (int b = 0; b < batchCount; b++)
        {
            int start = b * BATCH;
            int n     = Mathf.Min(BATCH, total - start);

            _batches[b] = new Matrix4x4[n];
            var cols    = new Vector4[n];

            for (int i = 0; i < n; i++)
            {
                int vi  = start + i;
                var pos = new Vector3(
                    data.positions[vi * 3],
                    data.positions[vi * 3 + 1],
                    data.positions[vi * 3 + 2]);
                _batches[b][i] = Matrix4x4.TRS(pos, Quaternion.identity, scale);

                cols[i] = new Vector4(
                    data.colors[vi * 3],
                    data.colors[vi * 3 + 1],
                    data.colors[vi * 3 + 2],
                    1f);
            }

            _propBlocks[b] = new MaterialPropertyBlock();
            _propBlocks[b].SetVectorArray("_BaseColor", cols);
        }

        Debug.Log($"VoxelRenderer: {total} voxels in {batchCount} batches");
    }

    void Update()
    {
        if (_batches == null) return;
        for (int b = 0; b < _batches.Length; b++)
            Graphics.DrawMeshInstanced(cubeMesh, 0, voxelMaterial, _batches[b], _batches[b].Length, _propBlocks[b]);
    }

#if UNITY_EDITOR
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

        // Materiale condiviso (uno solo per tutti gli oggetti)
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

        // Crea il prefab con mesh embeddatа come sub-asset
        var mesh = BuildOptimizedMesh(_data);
        mesh.name = assetName;

        // Rimuovi prefab vecchio per evitare sub-asset duplicati
        AssetDatabase.DeleteAsset(prefabPath);

        var go = new GameObject(assetName);
        go.AddComponent<MeshFilter>().sharedMesh   = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = vcMat;
        var savedPrefab = PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
        DestroyImmediate(go);

        // Embeda la mesh nel file .prefab (nessun .asset separato)
        AssetDatabase.AddObjectToAsset(mesh, prefabPath);
        savedPrefab.GetComponent<MeshFilter>().sharedMesh = mesh;
        PrefabUtility.SavePrefabAsset(savedPrefab);

        string objPath = $"{assetFolder}/{assetName}.obj";
        ExportOBJ(mesh, objPath);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"VoxelRenderer: prefab → {prefabPath}  obj → {objPath}");
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

    static void ExportOBJ(Mesh mesh, string path)
    {
        var verts   = mesh.vertices;
        var normals = mesh.normals;
        var colors  = mesh.colors;
        var tris    = mesh.triangles;

        var sb = new StringBuilder();
        sb.AppendLine("# VoxelRenderer OBJ export");

        for (int i = 0; i < verts.Length; i++)
        {
            var v = verts[i];
            if (colors != null && i < colors.Length)
            {
                var c = colors[i];
                sb.AppendLine(FormattableString.Invariant($"v {v.x} {v.y} {v.z} {c.r} {c.g} {c.b}"));
            }
            else
            {
                sb.AppendLine(FormattableString.Invariant($"v {v.x} {v.y} {v.z}"));
            }
        }

        for (int i = 0; i < normals.Length; i++)
        {
            var n = normals[i];
            sb.AppendLine(FormattableString.Invariant($"vn {n.x} {n.y} {n.z}"));
        }

        for (int i = 0; i < tris.Length; i += 3)
        {
            int a = tris[i] + 1, b = tris[i + 1] + 1, c = tris[i + 2] + 1;
            sb.AppendLine($"f {a}//{a} {b}//{b} {c}//{c}");
        }

        File.WriteAllText(path, sb.ToString());
    }

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
