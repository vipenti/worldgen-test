using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Ricostruisce, bypassando del tutto l'LLM, la sequenza di biomi concatenati salvata da
/// BiomeSnapshotRecorder durante una sessione live precedente — il "mondo pregenerato" da
/// esplorare. Per ogni bioma della sessione: copia lo snapshot su llm_generated.json/
/// current_objects.json, teletrasporta il player nella posizione in cui quel bioma era stato
/// attivato dal vivo, e chiama WorldManager.ApplyNewConfig() — che non tocca mai la rete: legge
/// solo il file appena copiato. Gli asset 3D dei singoli oggetti restano quelli già in cache su
/// disco (Json/objects/*.voxjson), quindi ogni applicazione è istantanea, senza placeholder in
/// attesa: è esattamente l'effetto "tutto già pronto" richiesto per il video del mondo
/// pre-generato.
///
/// NOTA: dopo il caricamento, esplorare in avanti (oltre l'ultimo bioma) funziona normalmente.
/// Tornare indietro molto oltre la view distance può far rigenerare quei chunk con il bioma
/// ATTIVO più recente invece di quello originale in quel punto — nessuna mappa spaziale
/// bioma-per-regione viene persistita da nessuna parte (limite dell'architettura attuale, non
/// introdotto da questo script: capiterebbe anche ripetendo lo stesso percorso dal vivo).
/// </summary>
public class WorldSnapshotLoader : MonoBehaviour
{
    public WorldManager worldManager;

    [Tooltip("Nome della cartella sessione dentro persistentDataPath/WorldSnapshots/ da caricare. Se vuoto, usa automaticamente la più recente.")]
    public string sessionFolderName = "";

    [Tooltip("Se true, il caricamento parte da solo all'avvio della scena. Lascialo disattivato per le sessioni live normali: va acceso solo per la passata di registrazione del mondo pre-generato.")]
    public bool loadOnStart = false;

    [Tooltip("Secondi di pausa dopo ogni ApplyNewConfig, prima di passare al bioma successivo — tempo per lasciare che i chunk vicini al player si ricostruiscano con la nuova config.")]
    public float delayBetweenBiomes = 2f;

    public bool IsLoading { get; private set; }

    void Awake()
    {
        if (worldManager == null) worldManager = GetComponent<WorldManager>();
    }

    void Start()
    {
        if (loadOnStart) StartCoroutine(LoadLatestOrNamedSession());
    }

    public void LoadSession(string folderName)
    {
        sessionFolderName = folderName;
        StartCoroutine(LoadLatestOrNamedSession());
    }

    IEnumerator LoadLatestOrNamedSession()
    {
        if (IsLoading) yield break;
        if (worldManager == null)
        {
            Debug.LogError("[WorldSnapshotLoader] Nessun WorldManager assegnato — impossibile caricare la sessione.");
            yield break;
        }

        string root = Path.Combine(Application.persistentDataPath, "WorldSnapshots");
        string sessionPath;

        if (!string.IsNullOrEmpty(sessionFolderName))
        {
            sessionPath = Path.Combine(root, sessionFolderName);
        }
        else
        {
            if (!Directory.Exists(root))
            {
                Debug.LogError($"[WorldSnapshotLoader] Nessuna cartella di snapshot trovata in {root}.");
                yield break;
            }
            var dirs = new List<string>(Directory.GetDirectories(root));
            dirs.Sort();
            if (dirs.Count == 0)
            {
                Debug.LogError($"[WorldSnapshotLoader] {root} esiste ma non contiene nessuna sessione.");
                yield break;
            }
            sessionPath = dirs[dirs.Count - 1]; // I nomi hanno il timestamp nel nome, l'ordine alfabetico coincide con quello cronologico.
        }

        if (!Directory.Exists(sessionPath))
        {
            Debug.LogError($"[WorldSnapshotLoader] Cartella sessione non trovata: {sessionPath}");
            yield break;
        }

        string eventsPath = Path.Combine(sessionPath, "biome_events.jsonl");
        if (!File.Exists(eventsPath))
        {
            Debug.LogError($"[WorldSnapshotLoader] {eventsPath} mancante.");
            yield break;
        }

        IsLoading = true;
        Debug.Log($"[WorldSnapshotLoader] Caricamento sessione: {sessionPath}");

        string activeJsonDir = PathSettings.GetJsonFolder();
        var controller = worldManager.player != null ? worldManager.player.GetComponent<CharacterController>() : null;

        string[] lines = File.ReadAllLines(eventsPath);
        int applied = 0;
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JObject evt;
            try { evt = JObject.Parse(line); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WorldSnapshotLoader] Riga non valida in biome_events.jsonl, saltata: {ex.Message}");
                continue;
            }

            int index = evt["index"]?.Value<int>() ?? (applied + 1);
            string prefix = $"biome_{index:D4}";
            string srcLlm = Path.Combine(sessionPath, prefix + "_llm_generated.json");
            string srcObjects = Path.Combine(sessionPath, prefix + "_current_objects.json");

            if (!File.Exists(srcLlm) || !File.Exists(srcObjects))
            {
                Debug.LogWarning($"[WorldSnapshotLoader] Snapshot #{index} incompleto in {sessionPath}, saltato.");
                continue;
            }

            File.Copy(srcLlm, Path.Combine(activeJsonDir, "llm_generated.json"), overwrite: true);
            File.Copy(srcObjects, Path.Combine(activeJsonDir, "current_objects.json"), overwrite: true);

            if (worldManager.player != null)
            {
                float x = evt["x"]?.Value<float>() ?? worldManager.player.position.x;
                float y = evt["y"]?.Value<float>() ?? worldManager.player.position.y;
                float z = evt["z"]?.Value<float>() ?? worldManager.player.position.z;
                float yaw = evt["yaw"]?.Value<float>() ?? worldManager.player.eulerAngles.y;

                if (controller != null) controller.enabled = false;
                worldManager.player.position = new Vector3(x, y, z);
                worldManager.player.eulerAngles = new Vector3(0f, yaw, 0f);
                if (controller != null) controller.enabled = true;
            }

            worldManager.ApplyNewConfig();
            applied++;
            Debug.Log($"[WorldSnapshotLoader] Bioma #{index} applicato ({applied}/{lines.Length}).");

            yield return new WaitForSeconds(delayBetweenBiomes);
        }

        IsLoading = false;
        Debug.Log($"[WorldSnapshotLoader] Sessione caricata: {applied} biomi applicati. Mondo pronto per l'esplorazione libera.");
    }
}
