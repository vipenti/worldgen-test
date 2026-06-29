using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class TextureAtlasManager : MonoBehaviour
{
    [Header("Atlas")]
    public int maxTextures = 32;
    public int columns = 8;
    public int textureSize = 512;

    private Texture2D atlasAlbedo;
    private Texture2D atlasNormal;
    private int count = 0;
    private int Rows => Mathf.CeilToInt((float)maxTextures / columns);

    private Dictionary<string, int> registry = new Dictionary<string, int>();
    string MaterialsPath => Path.Combine(PathSettings.GetJsonFolder(), "materials.json");

    void Awake()
    {
        LoadRegistry();
        LoadAtlasFromDisk();
    }

    void LoadAtlasFromDisk()
    {
        string dir = PathSettings.GetTextureFolder();

        string albedoPath = Path.Combine(dir, "TextureAtlas.png");
        if (File.Exists(albedoPath))
        {
            atlasAlbedo = new Texture2D(2, 2);
            atlasAlbedo.LoadImage(File.ReadAllBytes(albedoPath));
            Debug.Log("[TextureAtlasManager] Atlas albedo caricata dal disco.");
        }

        string normalPath = Path.Combine(dir, "TextureAtlas_Normal.png");
        if (File.Exists(normalPath))
        {
            atlasNormal = new Texture2D(2, 2);
            atlasNormal.LoadImage(File.ReadAllBytes(normalPath));
            Debug.Log("[TextureAtlasManager] Atlas normal caricata dal disco.");
        }
    }

    void OnEnable()
    {
        GenerateTexture.OnTextureGenerated += AppendAlbedo;
        GenerateTexture.OnNormalGenerated  += AppendNormal;
    }

    void OnDisable()
    {
        GenerateTexture.OnTextureGenerated -= AppendAlbedo;
        GenerateTexture.OnNormalGenerated  -= AppendNormal;
    }

    void AppendAlbedo(Texture2D tex, string materialName)
    {
        int slot = AppendToAtlas(tex, ref atlasAlbedo, "TextureAtlas.png", trackCount: true);
        if (slot < 0) return;
        registry[materialName] = slot;
        SaveRegistry();
    }

    void AppendNormal(Texture2D tex) => AppendToAtlas(tex, ref atlasNormal, "TextureAtlas_Normal.png", trackCount: false);

    // Ritorna lo slot usato, oppure -1 se atlas piena
    int AppendToAtlas(Texture2D tex, ref Texture2D atlas, string fileName, bool trackCount)
    {
        int slot = trackCount ? count : count - 1;

        if (slot >= maxTextures)
        {
            Debug.LogWarning($"[TextureAtlasManager] Atlas piena, texture ignorata ({fileName}).");
            return -1;
        }

        atlas ??= CreateBlankAtlas();

        int col = slot % columns;
        int row = slot / columns;
        int x   = col * textureSize;
        int y   = (Rows - 1 - row) * textureSize;

        Color[] pixels = (tex.width == textureSize && tex.height == textureSize)
            ? tex.GetPixels()
            : Resize(tex).GetPixels();

        atlas.SetPixels(x, y, textureSize, textureSize, pixels);
        atlas.Apply();

        if (trackCount) count++;

        SaveAtlas(atlas, fileName);
        Debug.Log($"[TextureAtlasManager] Slot {slot + 1}/{maxTextures} → {fileName} (col={col} row={row}).");
        return slot;
    }

    Texture2D CreateBlankAtlas()
    {
        var tex = new Texture2D(columns * textureSize, Rows * textureSize, TextureFormat.RGBA32, false);
        tex.SetPixels(new Color[tex.width * tex.height]);
        tex.Apply();
        return tex;
    }

    void SaveAtlas(Texture2D tex, string fileName)
    {
        string dir = PathSettings.GetTextureFolder();
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, fileName), tex.EncodeToPNG());
#if UNITY_EDITOR
        AssetDatabase.ImportAsset($"Assets/GeneratedTexture/{fileName}");
#endif
    }

    void LoadRegistry()
    {
        if (!File.Exists(MaterialsPath)) return;

        string json = File.ReadAllText(MaterialsPath).Trim().Trim('{', '}');
        foreach (string line in json.Split(','))
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            int colon = trimmed.IndexOf(':');
            if (colon < 0) continue;
            string key = trimmed.Substring(0, colon).Trim().Trim('"');
            string val = trimmed.Substring(colon + 1).Trim();
            if (int.TryParse(val, out int id))
                registry[key] = id;
        }
        count = registry.Count;
        Debug.Log($"[TextureAtlasManager] Registry caricata: {count} materiali.");
    }

    void SaveRegistry()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MaterialsPath));
        var sb = new StringBuilder();
        sb.AppendLine("{");
        int i = 0;
        foreach (var kvp in registry)
        {
            sb.Append($"  \"{kvp.Key}\": {kvp.Value}");
            if (i < registry.Count - 1) sb.Append(",");
            sb.AppendLine();
            i++;
        }
        sb.Append("}");
        File.WriteAllText(MaterialsPath, sb.ToString());
#if UNITY_EDITOR
        AssetDatabase.ImportAsset("Assets/Json/materials.json");
#endif
        Debug.Log($"[TextureAtlasManager] materials.json aggiornato ({registry.Count} materiali).");
    }

    public bool HasMaterials => registry.Count > 0;

    // Ricostruisce l'atlas dai file PNG individuali già salvati — non chiama il server
    public void RebuildAtlasFromFiles()
    {
        string dir = PathSettings.GetTextureFolder();

        var sorted = new List<KeyValuePair<string, int>>(registry);
        sorted.Sort((a, b) => a.Value.CompareTo(b.Value));

        Texture2D newAlbedo = CreateBlankAtlas();
        Texture2D newNormal = CreateBlankAtlas();
        bool anyLoaded = false;

        foreach (var kvp in sorted)
        {
            int slot = kvp.Value;
            int col  = slot % columns;
            int row  = slot / columns;
            int x    = col * textureSize;
            int y    = (Rows - 1 - row) * textureSize;

            string albedoPath = Path.Combine(dir, $"{kvp.Key}.png");
            if (File.Exists(albedoPath))
            {
                Texture2D tex = new Texture2D(2, 2);
                tex.LoadImage(File.ReadAllBytes(albedoPath));
                newAlbedo.SetPixels(x, y, textureSize, textureSize,
                    tex.width == textureSize && tex.height == textureSize ? tex.GetPixels() : Resize(tex).GetPixels());
                anyLoaded = true;
            }

            string normalPath = Path.Combine(dir, $"{kvp.Key}_normal.png");
            if (File.Exists(normalPath))
            {
                Texture2D tex = new Texture2D(2, 2);
                tex.LoadImage(File.ReadAllBytes(normalPath));
                newNormal.SetPixels(x, y, textureSize, textureSize,
                    tex.width == textureSize && tex.height == textureSize ? tex.GetPixels() : Resize(tex).GetPixels());
            }
        }

        if (anyLoaded)
        {
            newAlbedo.Apply(); atlasAlbedo = newAlbedo;
            newNormal.Apply(); atlasNormal = newNormal;
            SaveAtlas(atlasAlbedo, "TextureAtlas.png");
            SaveAtlas(atlasNormal, "TextureAtlas_Normal.png");
            Debug.Log($"[TextureAtlasManager] Atlas ricostruita da {sorted.Count} file locali.");
        }
        else
        {
            Debug.LogWarning("[TextureAtlasManager] Nessun file PNG trovato in GeneratedTexture/.");
        }
    }

    [ContextMenu("Rigenera Atlas Completa")]
    public void RegenerateAtlas()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[TextureAtlasManager] Avvia il gioco prima di rigenerare.");
            return;
        }
        StartCoroutine(RegenerateAtlasRoutine());
    }

    public IEnumerator RegenerateAtlasRoutine()
    {
        var gt = FindObjectOfType<GenerateTexture>();
        if (gt == null)
        {
            Debug.LogError("[TextureAtlasManager] GenerateTexture non trovato in scena.");
            yield break;
        }

        // Ordina per ID per riempire gli slot nell'ordine corretto
        var sorted = new List<KeyValuePair<string, int>>(registry);
        sorted.Sort((a, b) => a.Value.CompareTo(b.Value));

        // Reset completo dello stato in-memory
        count       = 0;
        atlasAlbedo = null;
        atlasNormal = null;

        Debug.Log($"[TextureAtlasManager] Inizio rigenerazione di {sorted.Count} materiali...");

        foreach (var kvp in sorted)
        {
            bool done = false;
            Action<string> onDone = null;
            onDone = name =>
            {
                if (name == kvp.Key)
                {
                    done = true;
                    GenerateTexture.OnGenerationComplete -= onDone;
                }
            };
            GenerateTexture.OnGenerationComplete += onDone;
            gt.Generate(kvp.Key);
            yield return new WaitUntil(() => done);
        }

        Debug.Log("<color=cyan>[TextureAtlasManager]</color> Rigenerazione atlas completata.");
    }

    Texture2D Resize(Texture2D src)
    {
        RenderTexture rt   = RenderTexture.GetTemporary(textureSize, textureSize);
        Graphics.Blit(src, rt);
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        Texture2D dst = new Texture2D(textureSize, textureSize);
        dst.ReadPixels(new Rect(0, 0, textureSize, textureSize), 0, 0);
        dst.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        return dst;
    }
}
