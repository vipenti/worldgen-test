using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Newtonsoft.Json.Linq;

public class GenerateAsset : MonoBehaviour
{
    [Header("Input")]
    public string listPath    = "Assets/Json/current_objects.json";
    public string assetFolder = "Assets/GeneratedAsset";

    [Header("References")]
    public LLMObjectGeneration llmObjectGeneration;
    public WorldManager worldManager;

    // Chiamato da LLMWorldDirector con i soli oggetti nuovi — gira in background
    public void GenerateNew(List<string> names) => StartCoroutine(GenerateList(names));

    // Fallback manuale: legge current_objects.json e salta i prefab già esistenti
    public void RegenerateFromFile() => StartCoroutine(GenerateAll());

    IEnumerator GenerateList(List<string> names)
    {
        if (!CheckReady()) yield break;
        Debug.Log($"GenerateAsset: avvio generazione di {names.Count} nuovi oggetti in background...");

        llmObjectGeneration.OnImageReady += OnObjectImageReady;

        foreach (string name in names)
        {
            string prefabPath = Path.Combine(assetFolder, name + ".prefab");
            if (File.Exists(prefabPath)) { Debug.Log($"GenerateAsset: '{name}' già presente, skip."); continue; }

            Debug.Log($"GenerateAsset: generazione '{name}'...");
            llmObjectGeneration.object_name = name;
            llmObjectGeneration.StartObjectGeneration();
            yield return new WaitUntil(() => !llmObjectGeneration.IsGenerating);
            Debug.Log($"GenerateAsset: '{name}' completato.");
            worldManager?.ReplacePlaceholders(name);
        }

        llmObjectGeneration.OnImageReady -= OnObjectImageReady;
        Debug.Log($"GenerateAsset: {names.Count} oggetti generati.");
    }

    void OnObjectImageReady(string name, byte[] bytes)
    {
        worldManager?.UpdatePlaceholderTexture(name, bytes);
    }

    IEnumerator GenerateAll()
    {
        if (!CheckReady()) yield break;
        if (!File.Exists(listPath)) { Debug.LogError($"GenerateAsset: file non trovato — {listPath}"); yield break; }

        var entries = JObject.Parse(File.ReadAllText(listPath));
        Debug.Log($"GenerateAsset: {entries.Count} oggetti in lista, cerco quelli mancanti...");

        int generated = 0;
        foreach (var entry in entries)
        {
            string name      = entry.Key;
            string prefabPath = Path.Combine(assetFolder, name + ".prefab");
            if (File.Exists(prefabPath)) { Debug.Log($"GenerateAsset: '{name}' già presente, skip."); continue; }

            llmObjectGeneration.object_name = name;
            llmObjectGeneration.StartObjectGeneration();
            yield return new WaitUntil(() => !llmObjectGeneration.IsGenerating);
            Debug.Log($"GenerateAsset: '{name}' completato.");
            generated++;
        }

        Debug.Log($"GenerateAsset: {generated} oggetti generati.");
    }

    bool CheckReady()
    {
        if (llmObjectGeneration != null) return true;
        Debug.LogError("GenerateAsset: llmObjectGeneration non assegnato.");
        return false;
    }
}
