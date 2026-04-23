using UnityEngine;
using System.IO;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class TextureAtlasManager : MonoBehaviour
{
    [Header("Atlas")]
    public int maxTextures = 32;
    public int columns = 8;       // righe = ceil(maxTextures / columns)
    public int textureSize = 512; // deve corrispondere alla dimensione generata dal server

    private Texture2D atlas;
    private int count = 0;
    private int Rows => Mathf.CeilToInt((float)maxTextures / columns);

    void OnEnable()  => GenerateTexture.OnTextureGenerated += Append;
    void OnDisable() => GenerateTexture.OnTextureGenerated -= Append;

    void Append(Texture2D tex)
    {
        if (count >= maxTextures)
        {
            Debug.LogWarning("[TextureAtlasManager] Atlas piena, texture ignorata.");
            return;
        }

        if (atlas == null)
            InitAtlas();

        int col = count % columns;
        int row = count / columns;
        int x   = col * textureSize;
        int y   = (Rows - 1 - row) * textureSize; // Unity usa Y bottom-up

        Color[] pixels = (tex.width == textureSize && tex.height == textureSize)
            ? tex.GetPixels()
            : Resize(tex).GetPixels();

        atlas.SetPixels(x, y, textureSize, textureSize, pixels);
        atlas.Apply();
        count++;

        Save();
        Debug.Log($"[TextureAtlasManager] Texture #{count}/{maxTextures} aggiunta (col={col} row={row}).");
    }

    void InitAtlas()
    {
        atlas = new Texture2D(columns * textureSize, Rows * textureSize, TextureFormat.RGBA32, false);
        Color[] blank = new Color[atlas.width * atlas.height];
        atlas.SetPixels(blank);
        atlas.Apply();
    }

    void Save()
    {
        string dir  = Path.Combine(Application.dataPath, "GeneratedTexture");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "TextureAtlas.png");
        File.WriteAllBytes(path, atlas.EncodeToPNG());
#if UNITY_EDITOR
        AssetDatabase.ImportAsset("Assets/GeneratedTexture/TextureAtlas.png");
#endif
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
