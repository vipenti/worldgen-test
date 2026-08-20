using System.IO;
using UnityEditor;
using UnityEngine;

// Voce di menu "Tools" per azzerare a mano tutto il contenuto generato dalla pipeline IA (stesso
// tipo di reset fatto da BenchmarkRunner.ResetGenerationState() all'avvio di una run benchmark, ma
// richiamabile in qualunque momento dall'Editor senza dover passare dalla scena di benchmark) —
// utile per ripartire da uno stato pulito prima di un test manuale o di un nuovo giro di sviluppo.
public static class ClearGeneratedContent
{
    [MenuItem("Tools/Clear Generated Content")]
    public static void ClearAll()
    {
        bool confirmed = EditorUtility.DisplayDialog(
            "Clear Generated Content",
            "This will permanently delete:\n\n" +
            "• all files in GeneratedImages\n" +
            "• all files in GeneratedAsset (except VoxelVertexColor.mat)\n" +
            "• all files in Json/objects\n" +
            "• all_objects.json, current_objects.json, llm_generated.json, materials.json\n" +
            "• all files in GeneratedTexture\n\n" +
            "This cannot be undone. Continue?",
            "Delete", "Cancel");
        if (!confirmed) return;

        int deletedCount = 0;

        // 1. GeneratedImages — tutto il contenuto
        deletedCount += ClearDirectoryContents(PathSettings.GetImagesFolder());

        // 2. GeneratedAsset — tutto TRANNE VoxelVertexColor.mat (il materiale condiviso usato da
        // VoxelRenderer per tutte le mesh vertex-color, non un asset "generato" per un singolo
        // oggetto — cancellarlo romperebbe i prefab che lo referenziano finché non viene ricreato).
        deletedCount += ClearDirectoryContents(
            Path.Combine(Application.dataPath, "GeneratedAsset"),
            keepFileNames: new[] { "VoxelVertexColor.mat" });

        // 3. Json/objects — tutti i voxjson generati
        deletedCount += ClearDirectoryContents(PathSettings.GetObjectsFolder());

        // 4. Registri principali in Json/
        string jsonFolder = PathSettings.GetJsonFolder();
        deletedCount += DeleteFileWithMeta(Path.Combine(jsonFolder, "all_objects.json"));
        deletedCount += DeleteFileWithMeta(Path.Combine(jsonFolder, "current_objects.json"));
        deletedCount += DeleteFileWithMeta(Path.Combine(jsonFolder, "llm_generated.json"));
        deletedCount += DeleteFileWithMeta(Path.Combine(jsonFolder, "materials.json"));

        // 5. GeneratedTexture — tutto il contenuto
        deletedCount += ClearDirectoryContents(PathSettings.GetTextureFolder());

        AssetDatabase.Refresh();
        Debug.Log($"[ClearGeneratedContent] Done — {deletedCount} file(s) deleted.");
    }

    // Cancella tutti i file (e i rispettivi .meta) in una directory, opzionalmente escludendo dei
    // nomi file specifici. Cancella anche il .meta insieme al file per evitare gli "orphaned .meta"
    // warning che Unity mostra quando un asset viene rimosso da fuori l'Editor senza il suo .meta.
    static int ClearDirectoryContents(string folderPath, string[] keepFileNames = null)
    {
        if (!Directory.Exists(folderPath)) return 0;

        int count = 0;
        foreach (string file in Directory.GetFiles(folderPath))
        {
            if (file.EndsWith(".meta")) continue; // gestito insieme al file corrispondente sotto

            string fileName = Path.GetFileName(file);
            if (keepFileNames != null && System.Array.IndexOf(keepFileNames, fileName) >= 0) continue;

            count += DeleteFileWithMeta(file);
        }
        return count;
    }

    static int DeleteFileWithMeta(string filePath)
    {
        int count = 0;
        if (File.Exists(filePath))
        {
            try { File.Delete(filePath); count++; }
            catch (System.Exception e) { Debug.LogWarning($"[ClearGeneratedContent] Could not delete '{filePath}': {e.Message}"); }
        }

        string metaPath = filePath + ".meta";
        if (File.Exists(metaPath))
        {
            try { File.Delete(metaPath); }
            catch (System.Exception e) { Debug.LogWarning($"[ClearGeneratedContent] Could not delete '{metaPath}': {e.Message}"); }
        }

        return count;
    }
}
