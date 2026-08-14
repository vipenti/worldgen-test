using System;
using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>
/// Registra il percorso del player VR per l'intera sessione (dall'avvio della build alla sua
/// chiusura) su un file di testo, per poterlo poi riprodurre a freddo con VRPathPlayback in una
/// passata desktop separata (ripresa aerea). Va agganciato all'XR Origin (o a qualunque transform
/// rappresenti la posizione "reale" del player nel mondo): registra la SUA posizione/yaw, non
/// quelli della testa, perché lo yaw della testa oscilla per i normali movimenti dello sguardo ed
/// è un dato più "rumoroso" per pilotare una camera aerea in un secondo momento.
///
/// Formato file: JSON Lines (un oggetto JSON per riga) in
/// {persistentDataPath}/Recordings/vr_path_yyyyMMdd_HHmmss.jsonl — scelto invece di un unico
/// array JSON perché scrive in streaming durante tutta la sessione senza dover riscrivere il file
/// intero ad ogni campione, ed è robusto: se la build crasha o viene terminata a forza invece che
/// chiusa normalmente, le righe già scritte (con Flush periodico) restano comunque leggibili.
/// </summary>
public class VRPathRecorder : MonoBehaviour
{
    [Tooltip("Transform di cui registrare posizione/yaw. Se vuoto, usa questo stesso GameObject.")]
    public Transform target;

    [Tooltip("Ogni quanti secondi salvare un campione. 10 volte al secondo è già più che sufficiente per un'interpolazione fluida in playback.")]
    public float sampleInterval = 0.1f;

    [Tooltip("Ogni quanti campioni forzare la scrittura su disco (Flush). Con sampleInterval=0.1s e questo a 10, il file viene salvato ~ogni secondo: un buon compromesso tra sicurezza in caso di crash e overhead di I/O.")]
    public int flushEverySamples = 10;

    public bool IsRecording { get; private set; }
    public string CurrentFilePath { get; private set; }

    StreamWriter _writer;
    float _timeSinceLastSample;
    float _recordingStartTime;
    int _samplesSinceFlush;

    void Awake()
    {
        if (target == null) target = transform;
    }

    void Start() => StartRecording();

    void Update()
    {
        if (!IsRecording) return;

        _timeSinceLastSample += Time.deltaTime;
        if (_timeSinceLastSample < sampleInterval) return;
        _timeSinceLastSample = 0f;

        WriteSample();
    }

    public void StartRecording()
    {
        if (IsRecording) return;

        try
        {
            string dir = Path.Combine(Application.persistentDataPath, "Recordings");
            Directory.CreateDirectory(dir);

            string fileName = $"vr_path_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl";
            CurrentFilePath = Path.Combine(dir, fileName);

            _writer = new StreamWriter(CurrentFilePath, append: false);
            _recordingStartTime = Time.time;
            _timeSinceLastSample = 0f;
            _samplesSinceFlush = 0;
            IsRecording = true;

            Debug.Log($"[VRPathRecorder] Registrazione avviata: {CurrentFilePath}");

            // Campione immediato a t=0, così il playback ha subito un punto di partenza valido
            // senza dover aspettare il primo sampleInterval.
            WriteSample();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[VRPathRecorder] Impossibile avviare la registrazione: {ex.Message}");
            IsRecording = false;
        }
    }

    public void StopRecording()
    {
        if (!IsRecording) return;

        IsRecording = false;
        try
        {
            _writer?.Flush();
            _writer?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[VRPathRecorder] Errore chiudendo il file di registrazione: {ex.Message}");
        }
        finally
        {
            _writer = null;
        }

        Debug.Log($"[VRPathRecorder] Registrazione salvata: {CurrentFilePath}");
    }

    void WriteSample()
    {
        if (_writer == null || target == null) return;

        float t = Time.time - _recordingStartTime;
        Vector3 p = target.position;
        float yaw = target.eulerAngles.y;

        // CultureInfo.InvariantCulture è FONDAMENTALE qui: su un sistema con locale italiano
        // "ToString()" userebbe la virgola come separatore decimale (es. "10,5" invece di "10.5"),
        // producendo righe che non sono JSON valido.
        _writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "{{\"t\":{0:F3},\"x\":{1:F3},\"y\":{2:F3},\"z\":{3:F3},\"yaw\":{4:F2}}}",
            t, p.x, p.y, p.z, yaw));

        _samplesSinceFlush++;
        if (_samplesSinceFlush >= flushEverySamples)
        {
            _samplesSinceFlush = 0;
            _writer.Flush();
        }
    }

    // Copre sia la chiusura pulita dell'app (Alt+F4 gestito da Windows, uscita da menu, ecc.) sia
    // la disattivazione dell'oggetto/scena — in entrambi i casi vogliamo che il file sia
    // chiuso/flushato correttamente invece di perdere l'ultimo secondo di campioni non ancora
    // scritti su disco.
    void OnApplicationQuit() => StopRecording();
    void OnDestroy() => StopRecording();
}
