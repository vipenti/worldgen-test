using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using System;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class GenerateTexture : MonoBehaviour
{
    [Header("Server")]
    public string serverUrl = "http://25.18.55.60:5000";

    [Header("Parametri")]
    public string materialName = "wood";
    [TextArea] public string negativePrompt = "";
    public bool tileable = false;
    public float normalStrength = 5.0f;

    [Header("Qualità (SD Turbo: steps 1-4, guidanceScale 0)")]
    public int steps = 4;
    public float guidanceScale = 0.0f;

    public static event Action<Texture2D, string> OnTextureGenerated;
    public static event Action<Texture2D>         OnNormalGenerated;
    public static event Action<string>            OnGenerationComplete;

    [System.Serializable] class ImageResponse  { public string image_base64; }
    [System.Serializable] class NormalResponse { public string image_base64; }

    [ContextMenu("Genera Texture")]
    public void Generate() => StartCoroutine(GenerateRoutine());

    public void Generate(string name, string paletteColorDescription = "")
    {
        materialName = name;
        StartCoroutine(GenerateRoutine(paletteColorDescription));
    }

    IEnumerator GenerateRoutine(string paletteColorDescription = "")
    {
        string outputDir = Path.Combine(Application.dataPath, "GeneratedTexture");
        Directory.CreateDirectory(outputDir);
        string safeName = materialName.Trim().Replace(" ", "_");
        
        string palettePrompt = string.IsNullOrEmpty(paletteColorDescription) ? "" : $" The colors must be perfectly coherent with this color palette: {paletteColorDescription}.";
        string prompt = $"Seamless flat tileable texture of a simple {materialName} surface for a 3D videogame, to be applied on cube faces. It must contain exclusively simple, clean characteristic details of the material. Top-down orthographic 90-degree view.{palettePrompt}";

        // Step 1: genera immagine
        Debug.Log("<color=cyan>GenerateTexture:</color> Chiamata /create_image...");
        string gsStr = guidanceScale.ToString("F1", CultureInfo.InvariantCulture);
        string body1 = $"{{\"prompt\":\"{EscapeJson(prompt)}\",\"negative_prompt\":\"{EscapeJson(negativePrompt)}\",\"tileable\":{tileable.ToString().ToLower()},\"steps\":{steps},\"guidance_scale\":{gsStr}}}";
        UnityWebRequest req1 = MakePost($"{serverUrl}/create_image", body1);
        yield return req1.SendWebRequest();

        if (req1.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[GenerateTexture] /create_image fallita: {req1.error}");
            req1.Dispose();
            OnGenerationComplete?.Invoke(safeName);
            yield break;
        }

        byte[] imageBytes = req1.downloadHandler.data;
        req1.Dispose();

        if (imageBytes == null || imageBytes.Length == 0)
        {
            Debug.LogError("[GenerateTexture] risposta vuota da /create_image");
            OnGenerationComplete?.Invoke(safeName);
            yield break;
        }

        string imageBase64 = Convert.ToBase64String(imageBytes);
        string albedoPath = Path.Combine(outputDir, $"{safeName}.png");
        File.WriteAllBytes(albedoPath, imageBytes);
        Debug.Log($"<color=cyan>GenerateTexture:</color> Albedo salvata → {albedoPath}");

        Texture2D generated = new Texture2D(2, 2);
        generated.LoadImage(imageBytes);
        OnTextureGenerated?.Invoke(generated, safeName);

        // Step 2: estrai normal map
        Debug.Log("<color=cyan>GenerateTexture:</color> Chiamata /extract_normal_map...");
        string strengthStr = normalStrength.ToString("F1", CultureInfo.InvariantCulture);
        string body2 = $"{{\"image_base64\":\"{imageBase64}\",\"strength\":{strengthStr}}}";
        UnityWebRequest req2 = MakePost($"{serverUrl}/extract_normal_map", body2);
        yield return req2.SendWebRequest();

        if (req2.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[GenerateTexture] /extract_normal_map fallita: {req2.error}");
            req2.Dispose();
            OnGenerationComplete?.Invoke(safeName);
            yield break;
        }

        byte[] normalBytes = req2.downloadHandler.data;
        req2.Dispose();

        if (normalBytes == null || normalBytes.Length == 0)
        {
            Debug.LogError("[GenerateTexture] risposta vuota da /extract_normal_map");
            OnGenerationComplete?.Invoke(safeName);
            yield break;
        }

        string normalPath = Path.Combine(outputDir, $"{safeName}_normal.png");
        File.WriteAllBytes(normalPath, normalBytes);
        Debug.Log($"<color=cyan>GenerateTexture:</color> Normal map salvata → {normalPath}");

        Texture2D generatedNormal = new Texture2D(2, 2);
        generatedNormal.LoadImage(normalBytes);
        OnNormalGenerated?.Invoke(generatedNormal);

        OnGenerationComplete?.Invoke(safeName);

#if UNITY_EDITOR
        AssetDatabase.Refresh();
#endif
    }

    UnityWebRequest MakePost(string url, string json)
    {
        var req = new UnityWebRequest(url, "POST");
        req.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        return req;
    }

    static string EscapeJson(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
}
