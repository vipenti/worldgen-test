using System.Collections;
using System.IO;
using UnityEngine;
using Newtonsoft.Json.Linq;

public class GenerateAsset : MonoBehaviour
{
    [Header("Input")]
    public string listPath = "Assets/Json/list_of_objects.json";

    [Header("References")]
    public LLMObjectGeneration llmObjectGeneration;

    void Start() => StartCoroutine(GenerateAll());

    IEnumerator GenerateAll()
    {
        if (llmObjectGeneration == null)
        {
            Debug.LogError("GenerateAsset: llmObjectGeneration non assegnato");
            yield break;
        }

        if (!File.Exists(listPath))
        {
            Debug.LogError($"GenerateAsset: file non trovato — {listPath}");
            yield break;
        }

        var names = JObject.Parse(File.ReadAllText(listPath));
        Debug.Log($"GenerateAsset: {names.Count} oggetti da generare");

        foreach (var entry in names)
        {
            var objectName = entry.Key;
            Debug.Log($"GenerateAsset: avvio '{objectName}'");

            llmObjectGeneration.object_name = objectName;
            llmObjectGeneration.StartObjectGeneration();

            yield return new WaitUntil(() => !llmObjectGeneration.IsGenerating);
            Debug.Log($"GenerateAsset: '{objectName}' completato");
        }

        Debug.Log("GenerateAsset: tutti gli oggetti generati!");
    }
}
