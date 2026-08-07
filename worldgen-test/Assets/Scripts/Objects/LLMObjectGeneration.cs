using System;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using System.Collections;


public class LLMObjectGeneration : MonoBehaviour
{
    public enum Quality { low, high, super_high }

    [Header("Object")]
    public string object_name = "house";

    [Header("Prompt")]
    [TextArea(3, 6)]
    public string stylePrompt;
    [TextArea(2, 4)]
    public string negativePrompt = "shadow, background, ground, floor, base, text, watermark, multiple elements";

    [Header("Generation")]
    public Quality quality = Quality.low;
    public bool transparent = false;
    public string serverAddress = "http://25.18.55.60:5000";

    [Header("Output")]
    public string outputFolder = "Assets/GeneratedImages";

    [Header("3D Generation")]
    public LLM3DGeneration llm3DGeneration;
    public bool autoGenerate = false;

    public bool IsGenerating { get; private set; }

    // Fires as soon as the PNG is ready, before 3D generation starts
    public event Action<string, byte[]> OnImageReady;

    void Start() { if (autoGenerate) StartObjectGeneration(); }

    [ContextMenu("Start Object Generation")]
    public void StartObjectGeneration()
    {
        if (IsGenerating)
        {
            Debug.LogWarning($"LLMObjectGeneration: era ancora in generazione — forzato reset.");
            StopAllCoroutines();
            IsGenerating = false;
        }

        OnValidate(); // rigenera sempre il prompt da object_name corrente
        IsGenerating = true;
        StartCoroutine(PostRequest());
    }

    void OnValidate()
    {
        //stylePrompt = $"A {object_name} made only of cubes in Minecraft style on a neutral background on a super thin white plane. Low cube resolution, frontal view, studio lighting.";
        stylePrompt = $"A beautiful, high-quality 3D render of a single realistic {object_name}. High definition, soft lighting, vibrant color palette. The object is perfectly centered against a clean, plain, neutral background (like pastel blue or cream). Studio lighting, sharp details, a touch of cozy game aesthetic.";
        //stylePrompt = $"A {object_name} made only of cubes in Minecraft style on a neutral background. Low cube resolution, isometric view, studio lighting.";
    }

    IEnumerator PostRequest()
    {
        //string paletteColorDescription = LoadPaletteColorDescription();
        string finalPrompt = stylePrompt;
        /*if (!string.IsNullOrEmpty(paletteColorDescription))
        {
            finalPrompt += $" Coherent with this color palette: {paletteColorDescription}.";
        }*/

        Debug.Log($"LLMObjectGeneration: invio richiesta immagine → {serverAddress}/generate_edit | oggetto: {object_name} | prompt finale: {finalPrompt}");
        string json = $"{{\"prompt\": \"{EscapeJson(finalPrompt)}\", \"negative_prompt\": \"{EscapeJson(negativePrompt)}\", \"quality\": \"{quality}\", \"transparent\": {(transparent ? "true" : "false")}}}";
        byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
        using var req = new UnityWebRequest(serverAddress + "/generate_edit", "POST");
        req.uploadHandler   = new UploadHandlerRaw(bodyRaw);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");

        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"LLMObjectGeneration: request failed — {req.responseCode} {req.error}\n{req.downloadHandler.text}");
            IsGenerating = false;
            yield break;
        }

        var imageData = req.downloadHandler.data;
        if (imageData == null || imageData.Length == 0)
        {
            Debug.LogError("LLMObjectGeneration: empty response from server");
            IsGenerating = false;
            yield break;
        }

        if (!Directory.Exists(outputFolder))
            Directory.CreateDirectory(outputFolder);
        string outputPath = Path.Combine(outputFolder, $"{object_name}.png");
        File.WriteAllBytes(outputPath, imageData);
        Debug.Log($"LLMObjectGeneration: saved to {outputPath}");

        OnImageReady?.Invoke(object_name, imageData);
        llm3DGeneration?.GenerateFromBytes(imageData, object_name);

        if (llm3DGeneration != null)
            yield return new WaitUntil(() => !llm3DGeneration.IsGenerating);

        IsGenerating = false;
    }

    // Skips the image HTTP request — starts 3D generation directly from pre-fetched bytes.
    // Used by GenerateAsset's pipeline mode so image[n+1] can overlap with 3D[n].
    public void StartFromImageBytes(string name, byte[] imageBytes)
    {
        if (IsGenerating)
        {
            Debug.LogWarning("LLMObjectGeneration: era ancora in generazione — forzato reset.");
            StopAllCoroutines();
            IsGenerating = false;
        }
        object_name  = name;
        IsGenerating = true;
        StartCoroutine(Run3DOnly(imageBytes));
    }

    IEnumerator Run3DOnly(byte[] imageBytes)
    {
        if (!Directory.Exists(outputFolder))
            Directory.CreateDirectory(outputFolder);
        File.WriteAllBytes(Path.Combine(outputFolder, $"{object_name}.png"), imageBytes);

        llm3DGeneration?.GenerateFromBytes(imageBytes, object_name);
        if (llm3DGeneration != null)
            yield return new WaitUntil(() => !llm3DGeneration.IsGenerating);

        IsGenerating = false;
    }

    string LoadPaletteColorDescription()
    {
        try
        {
            string path = Path.Combine(PathSettings.GetJsonFolder(), "llm_generated.json");
            if (File.Exists(path))
            {
                var root = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
                return root["palette_color_description"]?.ToString();
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[LLMObjectGeneration] Non è stato possibile caricare la palette dal file JSON: {ex.Message}");
        }
        return null;
    }

    static string EscapeJson(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
}
