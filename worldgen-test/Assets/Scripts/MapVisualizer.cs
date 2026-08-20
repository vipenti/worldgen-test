using System.IO;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

// Genera 5 PNG in Assets/Mappe ogni volta che viene chiamato GenerateMaps():
//   1_macro_noise.png       – continental noise (grayscale)
//   2_billow_layer.png      – billow contribution post-exponent (grayscale)
//   3_ridged_layer.png      – ridged contribution post-exponent (grayscale)
//   4_compound_height.png   – heightmap finale (colorata per quota)
//   5_layout_map.png        – densità oggetti (verde = spawn, grigio = densità)
public class MapVisualizer : MonoBehaviour
{
    [Header("Settings")]
    public int    mapSize      = 300;
    public string outputFolder = "Assets/Mappe";

    public void GenerateMaps(BiomeConfig cfg)
    {
        Directory.CreateDirectory(outputFolder);

        // Campiona tutti i layer sul grid mapSize × mapSize
        var macroArr  = new float[mapSize, mapSize];
        var billowArr = new float[mapSize, mapSize];
        var ridgedArr = new float[mapSize, mapSize];
        var heightArr = new float[mapSize, mapSize];
        var layoutArr = new float[mapSize, mapSize];

        float maxH = 0f;

        for (int x = 0; x < mapSize; x++)
        for (int z = 0; z < mapSize; z++)
        {
            float wx = x, wz = z;

            // Domain warping
            float warpedX = wx, warpedZ = wz;
            if (cfg.warpStrength > 0f && cfg.warpScale > 0f)
            {
                float s = 1f / cfg.warpScale;
                warpedX += (Mathf.PerlinNoise(wx * s + 501f, wz * s + 1f)   * 2f - 1f) * cfg.warpStrength;
                warpedZ += (Mathf.PerlinNoise(wx * s + 2f,   wz * s + 502f) * 2f - 1f) * cfg.warpStrength;
            }

            // 1. Macro (no warp)
            float macro = cfg.scaleMacro > 0f
                ? Mathf.PerlinNoise(wx / cfg.scaleMacro + 101f, wz / cfg.scaleMacro + 4917.3f)
                : 0.5f;

            // 2. Billow
            float billow = 0f;
            if (cfg.ampBillow > 0f && cfg.scaleBillow > 0f)
            {
                float raw = Mathf.PerlinNoise(warpedX / cfg.scaleBillow + 202f, warpedZ / cfg.scaleBillow + 7631.1f);
                billow = Mathf.Pow(Mathf.Abs(raw * 2f - 1f), cfg.billowExponent) * cfg.ampBillow;
            }

            // 3. Ridged
            float ridged = 0f;
            if (cfg.ampRidged > 0f && cfg.scaleRidged > 0f)
            {
                float raw = Mathf.PerlinNoise(warpedX / cfg.scaleRidged + 303f, warpedZ / cfg.scaleRidged + 2813.9f);
                ridged = Mathf.Pow(1f - Mathf.Abs(raw * 2f - 1f), cfg.ridgedExponent) * cfg.ampRidged;
            }

            // 4. Fusione Python
            float h = cfg.baseTerrainHeight
                    + billow * (1f - macro * cfg.macroInfluence)
                    + ridged * macro;
            float depression = Mathf.Clamp01((macro - 0.25f) / 0.15f);
            h = depression * h + (1f - depression) * (cfg.waterLevel - 3f);

            macroArr[x, z]  = macro;
            billowArr[x, z] = cfg.ampBillow  > 0f ? billow  / cfg.ampBillow  : 0f; // normalizzato 0-1
            ridgedArr[x, z] = cfg.ampRidged  > 0f ? ridged  / cfg.ampRidged  : 0f;
            heightArr[x, z] = h;
            if (h > maxH) maxH = h;
        }

        // Layout: slope (central diff) + noise
        float slopeLimit = cfg.slopeMaxLimit > 0f ? cfg.slopeMaxLimit : 4f;
        float scaleL     = cfg.scaleLayout   > 0f ? cfg.scaleLayout   : 30f;
        for (int x = 0; x < mapSize; x++)
        for (int z = 0; z < mapSize; z++)
        {
            float h = heightArr[x, z];
            if (h <= cfg.waterLevel) { layoutArr[x, z] = 0f; continue; }

            float hn = z + 1 < mapSize ? heightArr[x, z + 1] : h;
            float hs = z - 1 >= 0      ? heightArr[x, z - 1] : h;
            float he = x + 1 < mapSize ? heightArr[x + 1, z] : h;
            float hw = x - 1 >= 0      ? heightArr[x - 1, z] : h;
            float gx    = (he - hw) * 0.5f;
            float gz    = (hn - hs) * 0.5f;
            float slope = Mathf.Sqrt(gx * gx + gz * gz);

            if (slope > slopeLimit) { layoutArr[x, z] = 0f; continue; }

            layoutArr[x, z] = Mathf.PerlinNoise(x / scaleL + 404f, z / scaleL + 5591.7f);
        }

        SaveGrayscale(macroArr,  "1_macro_noise.png");
        SaveGrayscale(billowArr, "2_billow_layer.png");
        SaveGrayscale(ridgedArr, "3_ridged_layer.png");
        SaveColorHeight(heightArr, maxH, cfg, "4_compound_height.png");
        SaveLayoutMap(layoutArr, cfg.objectDensityThreshold, "5_layout_map.png");

#if UNITY_EDITOR
        AssetDatabase.Refresh();
#endif
        Debug.Log($"<color=lime>[MapVisualizer]</color> 5 maps saved to {outputFolder}  (maxH={maxH:F1})");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    void SaveGrayscale(float[,] data, string fileName)
    {
        var tex = new Texture2D(mapSize, mapSize, TextureFormat.RGB24, false);
        for (int x = 0; x < mapSize; x++)
        for (int z = 0; z < mapSize; z++)
        {
            float v = Mathf.Clamp01(data[x, z]);
            tex.SetPixel(x, mapSize - 1 - z, new Color(v, v, v));
        }
        tex.Apply();
        File.WriteAllBytes(Path.Combine(outputFolder, fileName), tex.EncodeToPNG());
        Destroy(tex);
    }

    void SaveColorHeight(float[,] heights, float maxHeight, BiomeConfig cfg, string fileName)
    {
        float norm = maxHeight > 0f ? maxHeight : 1f;
        var   tex  = new Texture2D(mapSize, mapSize, TextureFormat.RGB24, false);

        for (int x = 0; x < mapSize; x++)
        for (int z = 0; z < mapSize; z++)
        {
            float h = heights[x, z];
            Color c;

            if (h <= cfg.waterLevel)
            {
                float wt = cfg.waterLevel > 0 ? h / cfg.waterLevel : 0f;
                c = Color.Lerp(new Color(0.04f, 0.08f, 0.45f), new Color(0.20f, 0.50f, 0.88f), wt);
            }
            else
            {
                float t = Mathf.Clamp01((h - cfg.waterLevel) / (norm - cfg.waterLevel));
                if      (t < 0.20f) c = Color.Lerp(new Color(0.80f, 0.75f, 0.48f), new Color(0.42f, 0.68f, 0.22f), t / 0.20f);
                else if (t < 0.55f) c = Color.Lerp(new Color(0.42f, 0.68f, 0.22f), new Color(0.48f, 0.40f, 0.28f), (t - 0.20f) / 0.35f);
                else if (t < 0.80f) c = Color.Lerp(new Color(0.48f, 0.40f, 0.28f), new Color(0.58f, 0.58f, 0.58f), (t - 0.55f) / 0.25f);
                else                c = Color.Lerp(new Color(0.58f, 0.58f, 0.58f), Color.white,                     (t - 0.80f) / 0.20f);
            }

            tex.SetPixel(x, mapSize - 1 - z, c);
        }

        tex.Apply();
        File.WriteAllBytes(Path.Combine(outputFolder, fileName), tex.EncodeToPNG());
        Destroy(tex);
    }

    void SaveLayoutMap(float[,] layout, float threshold, string fileName)
    {
        var tex = new Texture2D(mapSize, mapSize, TextureFormat.RGB24, false);
        for (int x = 0; x < mapSize; x++)
        for (int z = 0; z < mapSize; z++)
        {
            float v = layout[x, z];
            Color c = v > threshold
                ? new Color(0.08f, 0.88f, 0.12f)              // verde = spawn
                : new Color(v * 0.55f, v * 0.55f, v * 0.55f); // grigio sfumato
            tex.SetPixel(x, mapSize - 1 - z, c);
        }
        tex.Apply();
        File.WriteAllBytes(Path.Combine(outputFolder, fileName), tex.EncodeToPNG());
        Destroy(tex);
    }
}
