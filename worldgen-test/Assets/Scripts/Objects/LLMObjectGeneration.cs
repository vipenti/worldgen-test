using System;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using System.Collections;

[Serializable]
class ImageResponse
{
    public string image_base64;
}

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
    public bool transparent = true;
    public string serverAddress = "http://127.0.0.1:5000";

    [Header("Output")]
    public string outputFolder = "Assets/GeneratedImages";

    [Header("3D Generation")]
    public LLM3DGeneration llm3DGeneration;
    public bool autoGenerate = false;

    public bool IsGenerating { get; private set; }

    void Start() { if (autoGenerate) StartObjectGeneration(); }

    [ContextMenu("Start Object Generation")]
    public void StartObjectGeneration()
    {
        OnValidate(); // rigenera sempre il prompt da object_name corrente

        IsGenerating = true;
        StartCoroutine(PostRequest());
    }

    void OnValidate()
    {
        stylePrompt = $"A {object_name} made only of cubes in Minecraft style on a neutral background. Low cube resolution, isometric view, studio lighting.";
    }

    IEnumerator PostRequest()
    {
        string json = $"{{\"prompt\": \"{EscapeJson(stylePrompt)}\", \"negative_prompt\": \"{EscapeJson(negativePrompt)}\", \"quality\": \"{quality}\", \"transparent\": {(transparent ? "true" : "false")}}}";
        byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
        using var req = new UnityWebRequest(serverAddress + "/generate_edit", "POST");
        req.uploadHandler   = new UploadHandlerRaw(bodyRaw);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");

        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"LLMObjectGeneration: request failed — {req.error}");
            IsGenerating = false;
            yield break;
        }

        var response = JsonUtility.FromJson<ImageResponse>(req.downloadHandler.text);
        if (string.IsNullOrEmpty(response?.image_base64))
        {
            Debug.LogError("LLMObjectGeneration: empty image_base64 in response");
            IsGenerating = false;
            yield break;
        }

        if (!Directory.Exists(outputFolder))
            Directory.CreateDirectory(outputFolder);

        string outputPath = Path.Combine(outputFolder, $"{object_name}.png");
        File.WriteAllBytes(outputPath, Convert.FromBase64String(response.image_base64));
        Debug.Log($"LLMObjectGeneration: saved to {outputPath}");

        llm3DGeneration?.GenerateFromPath(Path.GetFullPath(outputPath), object_name);

        if (llm3DGeneration != null)
            yield return new WaitUntil(() => !llm3DGeneration.IsGenerating);

        IsGenerating = false;
    }

    static string EscapeJson(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
}
