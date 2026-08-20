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

    // Se GameSettings è presente (pannello Opzioni), il suo indirizzo server sovrascrive sempre
    // il campo Inspector qui sopra — stesso server delle immagini oggetto (LLMObjectGeneration).
    string EffectiveServerUrl =>
        GameSettings.Instance != null ? GameSettings.Instance.ImageServerBase : serverUrl;

    [Header("Parametri")]
    public string materialName = "wood";
    [TextArea] public string negativePrompt = "";
    public float normalStrength = 5.0f;

    // Backend attuale è FLUX.1-schnell dietro /create_image (non più SD Turbo): niente
    // guidance_scale, la qualità si controlla col campo fisso "quality":"high" richiesto dal
    // server (vedi GUIDA_UNITY_INTEGRAZIONE_PIPELINE.md) — "tileable"/"guidance_scale" non
    // fanno parte dello schema richiesta attuale e sono stati rimossi.
    [Header("Qualità")]
    public int steps = 4;
    [Tooltip("Seed fisso per /create_image — stessa immagine a parità di prompt invece di una nuova ad ogni generazione.")]
    public int seed = 46;

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
        string outputDir = PathSettings.GetTextureFolder();
        Directory.CreateDirectory(outputDir);
        string safeName = materialName.Trim().Replace(" ", "_");
        
        string palettePrompt = string.IsNullOrEmpty(paletteColorDescription) ? "" : $" The colors is {paletteColorDescription}.";
        //string prompt = $"Seamless flat tileable texture of a {materialName} to be applied on cube faces of a cozy game. Simple, clean characteristic of the material. Top-down orthographic 90-degree view.{palettePrompt}";
        string prompt = $"Top-down aerial view of a texture tile of pure {materialName} material. very low details, cartoonish style, {palettePrompt}. Flat colors, soft shading, minimal highlights. Absolutely NO other elements or materials. Only the isolated surface pattern of {materialName}, filling the entire frame completely.";
        // Step 1: genera immagine — "quality":"high" è un requisito fisso del server (vedi
        // GUIDA_UNITY_INTEGRAZIONE_PIPELINE.md), non un'opzione: va sempre mandato così.
        Debug.Log("<color=cyan>GenerateTexture:</color> Calling /create_image...");
        string body1 = $"{{\"prompt\":\"{EscapeJson(prompt)}\",\"quality\":\"high\",\"negative_prompt\":\"{EscapeJson(negativePrompt)}\",\"steps\":{steps},\"seed\":{seed}}}";
        UnityWebRequest req1 = MakePost($"{EffectiveServerUrl}/create_image", body1);
        float call1Start = Time.realtimeSinceStartup;
        yield return req1.SendWebRequest();
        LlmCallMetrics.Report("texture_create_image", Time.realtimeSinceStartup - call1Start, req1.result == UnityWebRequest.Result.Success);

        if (req1.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[GenerateTexture] /create_image failed: {req1.error}");
            req1.Dispose();
            OnGenerationComplete?.Invoke(safeName);
            yield break;
        }

        byte[] imageBytes = req1.downloadHandler.data;
        req1.Dispose();

        if (imageBytes == null || imageBytes.Length == 0)
        {
            Debug.LogError("[GenerateTexture] empty response from /create_image");
            OnGenerationComplete?.Invoke(safeName);
            yield break;
        }

        string imageBase64 = Convert.ToBase64String(imageBytes);
        string albedoPath = Path.Combine(outputDir, $"{safeName}.png");
        File.WriteAllBytes(albedoPath, imageBytes);
        Debug.Log($"<color=cyan>GenerateTexture:</color> Albedo saved → {albedoPath}");

        Texture2D generated = new Texture2D(2, 2);
        generated.LoadImage(imageBytes);
        OnTextureGenerated?.Invoke(generated, safeName);

        // Step 2: estrai normal map
        Debug.Log("<color=cyan>GenerateTexture:</color> Calling /extract_normal_map...");
        string strengthStr = normalStrength.ToString("F1", CultureInfo.InvariantCulture);
        string body2 = $"{{\"image_base64\":\"{imageBase64}\",\"strength\":{strengthStr}}}";
        UnityWebRequest req2 = MakePost($"{EffectiveServerUrl}/extract_normal_map", body2);
        float call2Start = Time.realtimeSinceStartup;
        yield return req2.SendWebRequest();
        LlmCallMetrics.Report("texture_normal_map", Time.realtimeSinceStartup - call2Start, req2.result == UnityWebRequest.Result.Success);

        if (req2.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[GenerateTexture] /extract_normal_map failed: {req2.error}");
            req2.Dispose();
            OnGenerationComplete?.Invoke(safeName);
            yield break;
        }

        byte[] normalBytes = req2.downloadHandler.data;
        req2.Dispose();

        if (normalBytes == null || normalBytes.Length == 0)
        {
            Debug.LogError("[GenerateTexture] empty response from /extract_normal_map");
            OnGenerationComplete?.Invoke(safeName);
            yield break;
        }

        string normalPath = Path.Combine(outputDir, $"{safeName}_normal.png");
        File.WriteAllBytes(normalPath, normalBytes);
        Debug.Log($"<color=cyan>GenerateTexture:</color> Normal map saved → {normalPath}");

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
