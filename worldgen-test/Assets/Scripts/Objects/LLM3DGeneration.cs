using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

[Serializable]
class VoxOnlyRequest
{
    public string image_base64;
    public string output_dir;
    public string output_name;
    public int    seed                   = 1;
    public float  ss_guidance_strength   = 7.5f;
    public int    ss_sampling_steps      = 12;
    public float  slat_guidance_strength = 3.0f;
    public int    slat_sampling_steps    = 12;
}


public class LLM3DGeneration : MonoBehaviour
{
    [Header("Input")]
    public string imagePath  = "";
    public string outputName = "";  // se vuoto usa il nome del file immagine

    [Header("Generation Settings")]
    public int   seed                  = 1;
    public float ssGuidanceStrength    = 7.5f;
    public int   ssSamplingSteps       = 12;
    public float slatGuidanceStrength  = 3.0f;
    public int   slatSamplingSteps     = 12;

    [Header("References")]
    public VoxelRenderer voxelRenderer;

    const string Endpoint = "http://25.18.55.60:7960/generate_vox_only";

    string OutputDir => Path.Combine("Assets", "Json", "objects");

    public bool IsGenerating { get; private set; }

    byte[] _pendingImageBytes;

    public void GenerateFromBytes(byte[] bytes, string name = "")
    {
        _pendingImageBytes = bytes;
        outputName         = name;
        IsGenerating       = true;
        StartCoroutine(GenerateCoroutine());
    }

    public void GenerateFromPath(string path, string name = "")
    {
        imagePath    = path;
        outputName   = name;
        IsGenerating = true;
        Generate();
    }

    [ContextMenu("Generate")]
    public void Generate()
    {
        if (string.IsNullOrEmpty(imagePath))
        {
            Debug.LogError("LLM3DGeneration: imagePath is empty");
            return;
        }
        StartCoroutine(GenerateCoroutine());
    }

    IEnumerator GenerateCoroutine()
    {
        byte[] imageBytes;
        string name;

        if (_pendingImageBytes != null)
        {
            imageBytes         = _pendingImageBytes;
            _pendingImageBytes = null;
            name = string.IsNullOrEmpty(outputName) ? "object" : outputName;
        }
        else
        {
            var cleanPath = imagePath.Trim().Trim('"');
            var absImagePath = Path.IsPathRooted(cleanPath)
                ? cleanPath
                : Path.GetFullPath(Path.Combine(Application.dataPath, "..", cleanPath));
            absImagePath = absImagePath.Replace('\\', '/');

            if (!File.Exists(absImagePath))
            {
                Debug.LogError($"LLM3DGeneration: file non trovato: {absImagePath}");
                IsGenerating = false;
                yield break;
            }

            imageBytes = File.ReadAllBytes(absImagePath);
            name = string.IsNullOrEmpty(outputName)
                ? Path.GetFileNameWithoutExtension(cleanPath)
                : outputName;
        }

        var absOutputDir = OutputDir.Replace('\\', '/');

        var request = new VoxOnlyRequest
        {
            image_base64           = Convert.ToBase64String(imageBytes),
            output_dir             = absOutputDir,
            output_name            = name,
            seed                   = seed,
            ss_guidance_strength   = ssGuidanceStrength,
            ss_sampling_steps      = ssSamplingSteps,
            slat_guidance_strength = slatGuidanceStrength,
            slat_sampling_steps    = slatSamplingSteps,
        };

        var bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(request));

        using var req = new UnityWebRequest(Endpoint, "POST");
        req.uploadHandler   = new UploadHandlerRaw(bytes);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = 180;

        Debug.Log($"LLM3DGeneration: generazione '{name}'...");
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"LLM3DGeneration: {req.responseCode} {req.error}\n{req.downloadHandler.text}");
            IsGenerating = false;
            yield break;
        }

        Debug.Log($"LLM3DGeneration: risposta ricevuta ({req.downloadHandler.data.Length} bytes)");
        voxelRenderer?.LoadFromJson(req.downloadHandler.text, name);

        IsGenerating = false;
    }
}
