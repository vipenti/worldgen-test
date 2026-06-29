using System.IO;
using UnityEditor;
using UnityEngine;

public static class BuildAssetPacker
{
    [MenuItem("Tools/Prepare Baseline Assets for Build")]
    public static void PrepareBaselineAssets()
    {
        string baselineFolder = Path.Combine(Application.dataPath, "Resources", "Baseline");
        string objectsFolder = Path.Combine(baselineFolder, "objects");

        // 1. Crea le directory se non esistono
        Directory.CreateDirectory(baselineFolder);
        Directory.CreateDirectory(objectsFolder);

        // 2. Copia i file JSON del registro
        CopyFile(Path.Combine(Application.dataPath, "Json", "all_objects.json"), Path.Combine(baselineFolder, "all_objects.json"));
        CopyFile(Path.Combine(Application.dataPath, "Json", "current_objects.json"), Path.Combine(baselineFolder, "current_objects.json"));
        CopyFile(Path.Combine(Application.dataPath, "Json", "materials.json"), Path.Combine(baselineFolder, "materials.json"));
        CopyFile(Path.Combine(Application.dataPath, "Json", "llm_generated.json"), Path.Combine(baselineFolder, "llm_generated.json"));

        // 3. Copia le texture dell'atlas (rinominandole in .bytes per permettere la copia dei byte grezzi in build)
        CopyFile(Path.Combine(Application.dataPath, "GeneratedTexture", "TextureAtlas.png"), Path.Combine(baselineFolder, "TextureAtlas.bytes"));
        CopyFile(Path.Combine(Application.dataPath, "GeneratedTexture", "TextureAtlas_Normal.png"), Path.Combine(baselineFolder, "TextureAtlas_Normal.bytes"));

        // 4. Copia tutti i file voxjson degli oggetti, rinominandoli in .txt per il caricamento TextAsset
        string sourceObjectsDir = Path.Combine(Application.dataPath, "Json", "objects");
        if (Directory.Exists(sourceObjectsDir))
        {
            // Pulisce vecchi file prima di copiare per evitare avanzi obsoleti
            if (Directory.Exists(objectsFolder))
            {
                Directory.Delete(objectsFolder, true);
                Directory.CreateDirectory(objectsFolder);
            }

            string[] files = Directory.GetFiles(sourceObjectsDir, "*.voxjson");
            int count = 0;
            foreach (string file in files)
            {
                string destFileName = Path.GetFileNameWithoutExtension(file);
                string destPath = Path.Combine(objectsFolder, destFileName + ".txt");
                CopyFile(file, destPath);
                count++;
            }
            Debug.Log($"[BuildAssetPacker] Copiati {count} file voxjson in Baseline/objects (rinominati in .txt per compatibilità TextAsset).");
        }

        AssetDatabase.Refresh();
        Debug.Log("[BuildAssetPacker] Risorse baseline preparate con successo! Ora puoi compilare la build.");
    }

    private static void CopyFile(string src, string dest)
    {
        if (File.Exists(src))
        {
            File.Copy(src, dest, true);
        }
        else
        {
            Debug.LogWarning($"[BuildAssetPacker] File sorgente non trovato per la copia: {src}");
        }
    }
}
