using System;
using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>
/// Salva uno "snapshot" di ogni bioma nel momento esatto in cui diventa attivo durante una
/// sessione live: la sua config (llm_generated.json), il suo registro oggetti
/// (current_objects.json) e la posizione/yaw del player in quell'istante.
///
/// Perché serve: llm_generated.json/current_objects.json vengono sovrascritti ad ogni cambio
/// bioma — contengono sempre e solo l'ULTIMO bioma, mai la sequenza intera. I confini spaziali
/// tra i biomi non sono salvati da nessuna parte: esistono solo perché il player si trovava in un
/// certo punto quando ApplyNewConfig è scattato. Senza questi snapshot, ricaricare la scena
/// riproduce solo l'ultimo bioma su tutta la mappa, perdendo la concatenazione vista dal vivo.
/// Con WorldSnapshotLoader, questi snapshot permettono di ricostruire la stessa sequenza
/// concatenata bypassando del tutto l'LLM (gli asset 3D sono già in cache, quindi istantaneo).
///
/// Output: {persistentDataPath}/WorldSnapshots/session_yyyyMMdd_HHmmss/
///   biome_events.jsonl              — una riga per bioma: {"index","x","y","z","yaw"}
///   biome_0001_llm_generated.json
///   biome_0001_current_objects.json
///   biome_0002_llm_generated.json
///   ... ecc.
/// </summary>
public class BiomeSnapshotRecorder : MonoBehaviour
{
    [Tooltip("Transform di cui registrare posizione/yaw al momento di ogni cambio bioma. Se vuoto, usa questo stesso GameObject.")]
    public Transform target;

    public string SessionFolder { get; private set; }
    public int BiomeCount { get; private set; }

    void Awake()
    {
        if (target == null) target = transform;
    }

    void OnEnable() => LLMWorldDirector.OnTerrainPipelineDone += HandleBiomeChanged;
    void OnDisable() => LLMWorldDirector.OnTerrainPipelineDone -= HandleBiomeChanged;

    void HandleBiomeChanged()
    {
        try
        {
            if (string.IsNullOrEmpty(SessionFolder))
            {
                SessionFolder = Path.Combine(Application.persistentDataPath, "WorldSnapshots",
                    $"session_{DateTime.Now:yyyyMMdd_HHmmss}");
                Directory.CreateDirectory(SessionFolder);
            }

            BiomeCount++;
            string prefix = $"biome_{BiomeCount:D4}";

            string srcLlm = Path.Combine(PathSettings.GetJsonFolder(), "llm_generated.json");
            string srcObjects = Path.Combine(PathSettings.GetJsonFolder(), "current_objects.json");
            if (!File.Exists(srcLlm) || !File.Exists(srcObjects))
            {
                Debug.LogWarning($"[BiomeSnapshotRecorder] llm_generated.json o current_objects.json mancanti al momento dello snapshot #{BiomeCount} — salto.");
                BiomeCount--;
                return;
            }

            File.Copy(srcLlm, Path.Combine(SessionFolder, prefix + "_llm_generated.json"), overwrite: true);
            File.Copy(srcObjects, Path.Combine(SessionFolder, prefix + "_current_objects.json"), overwrite: true);

            Vector3 p = target != null ? target.position : Vector3.zero;
            float yaw = target != null ? target.eulerAngles.y : 0f;
            string eventsPath = Path.Combine(SessionFolder, "biome_events.jsonl");
            File.AppendAllText(eventsPath, string.Format(CultureInfo.InvariantCulture,
                "{{\"index\":{0},\"x\":{1:F3},\"y\":{2:F3},\"z\":{3:F3},\"yaw\":{4:F2}}}\n",
                BiomeCount, p.x, p.y, p.z, yaw));

            Debug.Log($"[BiomeSnapshotRecorder] Snapshot bioma #{BiomeCount} salvato in {SessionFolder} (pos={p}, yaw={yaw:F1}).");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[BiomeSnapshotRecorder] Errore salvando lo snapshot del bioma: {ex.Message}");
        }
    }
}
