using System;

/// <summary>
/// Hook centrale per misurare la latenza delle singole chiamate HTTP verso i server LLM/generazione
/// sulla LAN (chat-completions LM Studio :1234, image-gen :5000, 3D voxel-gen/Trellis :7960 — vedi
/// LLMWorldDirector, GenerateTexture, Objects/GenerateAsset, Objects/LLMObjectGeneration,
/// Objects/LLM3DGeneration). Ogni punto di chiamata invoca Report() subito dopo che la
/// UnityWebRequest è tornata (successo, errore o retry su 503), passando quanto è durata.
///
/// Un evento C# senza iscritti costa pochissimo, quindi questi punti di chiamata restano invariati
/// anche fuori dal benchmark: solo BenchmarkRunner si iscrive, e solo mentre una run è in corso.
/// </summary>
public static class LlmCallMetrics
{
    /// <param name="callType">Etichetta stabile del tipo di chiamata, es. "chat_completion", "texture_create_image".</param>
    /// <param name="seconds">Durata della singola richiesta HTTP (da poco prima di SendWebRequest a quando è tornata).</param>
    /// <param name="success">Esito di QUESTA richiesta (una retry su 503 è success=false, non è un fallimento della generazione nel suo complesso).</param>
    public static event Action<string, float, bool> OnCallCompleted;

    public static void Report(string callType, float seconds, bool success) =>
        OnCallCompleted?.Invoke(callType, seconds, success);
}
