using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Newtonsoft.Json.Linq;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class GenerateAsset : MonoBehaviour
{
    [Header("Input")]
    public string listPath    = "Assets/Json/current_objects.json";
    public string assetFolder = "Assets/GeneratedAsset";

    [Header("References")]
    public LLMObjectGeneration llmObjectGeneration;
    public WorldManager worldManager;

    [Header("Pipeline (optional)")]
    [Tooltip("Assign a second LLMObjectGeneration with its LLM3D Generation left empty. " +
             "Enables two-phase generation: all reference images first, then all 3D models.")]
    public LLMObjectGeneration llmObjectGenerationForImages;

    public static bool              IsGenerating       { get; private set; }
    public static event Action         OnAllAssetsComplete;
    public static event Action<int, int> OnAssetProgress;

    // Called by LLMWorldDirector with the new objects only — runs in background
    public void GenerateNew(List<string> names) => StartCoroutine(GenerateList(names));

    // Manual fallback: reads current_objects.json and skips existing prefabs
    public void RegenerateFromFile() => StartCoroutine(GenerateAll());

    // ── Entry point ───────────────────────────────────────────────────────────

    IEnumerator GenerateList(List<string> names)
    {
        if (!CheckReady()) { OnAllAssetsComplete?.Invoke(); yield break; }

        IsGenerating = true;
        int total = names.Count;
        OnAssetProgress?.Invoke(0, total);
        Debug.Log($"GenerateAsset: avvio di {total} oggetti in background...");

        if (llmObjectGenerationForImages != null)
            yield return StartCoroutine(GenerateTwoPhase(names));
        else
            yield return StartCoroutine(GenerateSequential(names));

        IsGenerating = false;
        OnAllAssetsComplete?.Invoke();
        Debug.Log($"GenerateAsset: {total} oggetti processati.");
    }

    // ── Sequential path (single LLMObjectGeneration instance) ────────────────

    IEnumerator GenerateSequential(List<string> names)
    {
        int done  = 0;
        int total = names.Count;
        llmObjectGeneration.OnImageReady += OnObjectImageReady;

        foreach (string name in names)
        {
            string prefabPath = Path.Combine(assetFolder, name + ".prefab");
            if (File.Exists(prefabPath))
            {
                Debug.Log($"GenerateAsset: '{name}' già presente, skip.");
                OnAssetProgress?.Invoke(++done, total);
                continue;
            }

            Debug.Log($"GenerateAsset: generazione '{name}'...");
            llmObjectGeneration.object_name = name;
            llmObjectGeneration.StartObjectGeneration();
            yield return new WaitUntil(() => !llmObjectGeneration.IsGenerating);
            Debug.Log($"GenerateAsset: '{name}' completato.");
            worldManager?.ReplacePlaceholders(name);
            OnAssetProgress?.Invoke(++done, total);
        }

        llmObjectGeneration.OnImageReady -= OnObjectImageReady;
    }

    // ── Two-phase path (image-only fetcher + 3D generator) ────────────────────
    // Round 1 — very_common + common: images shown on billboards → then 3D replaces them.
    // Round 2 — rare + super_rare + unique: images fetched silently → then 3D added to pool.

    IEnumerator GenerateTwoPhase(List<string> names)
    {
        int total = names.Count;
        int done  = 0;

        // Pre-classify: skip existing prefabs
        var toGenerate = new List<string>();
        foreach (string n in names)
        {
            if (File.Exists(Path.Combine(assetFolder, n + ".prefab")))
            {
                Debug.Log($"GenerateAsset: '{n}' già presente, skip.");
                OnAssetProgress?.Invoke(++done, total);
            }
            else
                toGenerate.Add(n);
        }
        if (toGenerate.Count == 0) yield break;

        // Split by rarity tier: fc > 0.10 = common / very_common; rest = rare and below
        var freqData     = LoadFrequencyData();
        var highPriority = new List<string>();
        var lowPriority  = new List<string>();
        foreach (string n in toGenerate)
        {
            float fc = freqData.TryGetValue(n, out float f) ? f : 0.5f;
            (fc > 0.10f ? highPriority : lowPriority).Add(n);
        }
        Debug.Log($"GenerateAsset: {highPriority.Count} high-priority, {lowPriority.Count} low-priority.");

        // ── Round 1A: fetch images for common / very_common ───────────────────
        var highCollected = new List<(string name, byte[] bytes)>();
        foreach (string n in highPriority)
            yield return StartCoroutine(FetchImage(n, highCollected, showOnBillboard: true));

        // ── Round 1B: 3D generation for common / very_common ─────────────────
        foreach (var (name, bytes) in highCollected)
        {
            Debug.Log($"GenerateAsset: 3D '{name}'...");
            llmObjectGeneration.StartFromImageBytes(name, bytes);
            yield return new WaitUntil(() => !llmObjectGeneration.IsGenerating);
            worldManager?.ReplacePlaceholders(name);
            OnAssetProgress?.Invoke(++done, total);
            Debug.Log($"GenerateAsset: '{name}' completato.");
        }

        // ── Round 2A: fetch images for rare / super_rare / unique ─────────────
        var lowCollected = new List<(string name, byte[] bytes)>();
        foreach (string n in lowPriority)
            yield return StartCoroutine(FetchImage(n, lowCollected, showOnBillboard: false));

        // ── Round 2B: 3D generation for rare / super_rare / unique ────────────
        // No billboards to replace; ReplacePlaceholders still registers the prefab
        // so new chunks spawned after this point will use the ready 3D model.
        foreach (var (name, bytes) in lowCollected)
        {
            Debug.Log($"GenerateAsset: 3D '{name}'...");
            llmObjectGeneration.StartFromImageBytes(name, bytes);
            yield return new WaitUntil(() => !llmObjectGeneration.IsGenerating);
            worldManager?.ReplacePlaceholders(name);
            OnAssetProgress?.Invoke(++done, total);
            Debug.Log($"GenerateAsset: '{name}' completato.");
        }
    }

    // Fetches one reference image using llmObjectGenerationForImages.
    // Adds (name, bytes) to results on success; if showOnBillboard, updates the world placeholder.
    IEnumerator FetchImage(string name, List<(string, byte[])> results, bool showOnBillboard)
    {
        byte[] imageBytes = null;
        Action<string, byte[]> onImage = (n, b) => { if (n == name) imageBytes = b; };
        llmObjectGenerationForImages.OnImageReady += onImage;

        llmObjectGenerationForImages.object_name = name;
        llmObjectGenerationForImages.StartObjectGeneration();
        yield return new WaitUntil(() => !llmObjectGenerationForImages.IsGenerating);

        llmObjectGenerationForImages.OnImageReady -= onImage;

        if (imageBytes != null)
        {
            if (showOnBillboard)
                worldManager?.UpdatePlaceholderTexture(name, imageBytes);
            results.Add((name, imageBytes));
            Debug.Log($"GenerateAsset: immagine pronta '{name}'.");
        }
        else
        {
            Debug.LogWarning($"GenerateAsset: immagine non ricevuta per '{name}', skip 3D.");
        }
    }

    // ── File-based fallback ───────────────────────────────────────────────────

    IEnumerator GenerateAll()
    {
        if (!CheckReady()) yield break;
        if (!File.Exists(listPath)) { Debug.LogError($"GenerateAsset: file non trovato — {listPath}"); yield break; }

        var entries = JObject.Parse(File.ReadAllText(listPath));
        var names   = new List<string>();
        foreach (var entry in entries) names.Add(entry.Key);

        yield return StartCoroutine(GenerateList(names));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    Dictionary<string, float> LoadFrequencyData()
    {
        var result = new Dictionary<string, float>();
        if (!File.Exists(listPath)) return result;
        try
        {
            var root = JObject.Parse(File.ReadAllText(listPath));
            foreach (var entry in root)
            {
                if (entry.Value?.Type != JTokenType.Object) { result[entry.Key] = 0.5f; continue; }
                result[entry.Key] = (entry.Value["frequency"]?.ToString()) switch
                {
                    "very_common" => 1.00f,
                    "common"      => 0.50f,
                    "rare"        => 0.10f,
                    "super_rare"  => 0.03f,
                    "unique"      => -1f,
                    _             => 0.50f
                };
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"GenerateAsset: errore lettura frequenza: {e.Message}");
        }
        return result;
    }

    void OnObjectImageReady(string name, byte[] bytes)
    {
        worldManager?.UpdatePlaceholderTexture(name, bytes);
    }

    bool CheckReady()
    {
        if (llmObjectGeneration != null) return true;
        Debug.LogError("GenerateAsset: llmObjectGeneration non assegnato.");
        return false;
    }
}
