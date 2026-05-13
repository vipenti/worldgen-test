using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

[Serializable]
class VoxOnlyRequest
{
    public string image_path;
    public string output_dir;
    public string output_name;
    public int    seed                   = 1;
    public float  ss_guidance_strength   = 7.5f;
    public int    ss_sampling_steps      = 12;
    public float  slat_guidance_strength = 3.0f;
    public int    slat_sampling_steps    = 12;
}

[Serializable]
class VoxOnlyResponse
{
    public string status;
    public int    voxel_count;
    public string vxz_path;
    public string voxjson_path;
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

    const string Endpoint = "http://127.0.0.1:7960/generate_vox_only";

    string OutputDir => Path.Combine(Application.dataPath, "Json", "objects");

    public bool IsGenerating { get; private set; }

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
        // Rimuove virgolette/spazi accidentali e normalizza i separatori
        var cleanPath = imagePath.Trim().Trim('"');
        var absImagePath = Path.IsPathRooted(cleanPath)
            ? cleanPath
            : Path.GetFullPath(Path.Combine(Application.dataPath, "..", cleanPath));
        absImagePath = absImagePath.Replace('\\', '/');

        if (!File.Exists(absImagePath))
        {
            Debug.LogError($"LLM3DGeneration: file non trovato: {absImagePath}");
            yield break;
        }

        var absOutputDir = OutputDir.Replace('\\', '/');

        var name = string.IsNullOrEmpty(outputName)
            ? Path.GetFileNameWithoutExtension(cleanPath)
            : outputName;

        var request = new VoxOnlyRequest
        {
            image_path             = absImagePath,
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

        Debug.Log($"LLM3DGeneration: generazione '{name}' da {imagePath}...");
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"LLM3DGeneration: {req.responseCode} {req.error}\n{req.downloadHandler.text}");
            IsGenerating = false;
            yield break;
        }

        var response = JsonUtility.FromJson<VoxOnlyResponse>(req.downloadHandler.text);
        Debug.Log($"LLM3DGeneration: {response.voxel_count} voxels → {response.voxjson_path}");

        if (voxelRenderer != null)
        {
            voxelRenderer.jsonFolder   = Path.GetDirectoryName(response.voxjson_path);
            voxelRenderer.jsonFileName = Path.GetFileName(response.voxjson_path);
            voxelRenderer.Reload();
        }

        IsGenerating = false;
    }
}
