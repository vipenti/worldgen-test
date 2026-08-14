using UnityEngine;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// Componente opzionale per le main scene (MainScene / MainScene_VR): quando attivo, esegue in
/// automatico una sequenza di prompt testuali sul LLMWorldDirector, uno alla volta, aspettando che
/// la generazione del mondo precedente sia COMPLETAMENTE finita (pipeline terreno + asset 3D del
/// bioma, stesso criterio usato da FirstGeneration per la primissima generazione) prima di lanciare
/// il prompt successivo. Utile per demo/benchmark/showcase senza dover digitare ogni prompt a mano
/// nell'input field di LLMWorldDirector.
///
/// Basta trascinare questo componente su un GameObject della scena, compilare la lista "Prompts" e
/// lasciarlo enabled: la sequenza parte da sola. Disattivare il componente (checkbox in Inspector o
/// SetActive) interrompe la sequenza al prompt corrente.
/// </summary>
public class AutoPromptSequencer : MonoBehaviour
{
    [Tooltip("Riferimento al direttore LLM che genera i mondi. Se lasciato vuoto, viene cercato automaticamente in scena.")]
    public LLMWorldDirector llmWorldDirector;

    [Tooltip("Prompt eseguiti in ordine, uno alla volta. Il successivo parte solo quando il precedente ha finito di generare (terreno + asset 3D) o è fallito/andato in timeout.")]
    public List<string> prompts = new List<string>();

    [Tooltip("Pausa (in secondi) tra la fine di un prompt e l'invio del successivo.")]
    public float delayBetweenPrompts = 1f;

    [Tooltip("Se true, al termine dell'ultimo prompt la sequenza riparte dal primo invece di fermarsi.")]
    public bool loopSequence = false;

    [Tooltip("Timeout di sicurezza (secondi) per singolo prompt: se superato senza segnali di completamento/fallimento dal LLM, si passa comunque al prompt successivo invece di restare bloccati per sempre.")]
    public float perPromptTimeout = 300f;

    public bool IsRunning { get; private set; }
    public int CurrentIndex { get; private set; } = -1;

    bool _terrainDone;
    bool _failed;
    Coroutine _routine;

    void Awake()
    {
        if (llmWorldDirector == null) llmWorldDirector = FindAnyObjectByType<LLMWorldDirector>();
    }

    void OnEnable()
    {
        if (_routine == null && prompts.Count > 0)
            _routine = StartCoroutine(RunSequence());
    }

    void OnDisable()
    {
        if (_routine != null)
        {
            StopCoroutine(_routine);
            _routine = null;
        }
        UnsubscribeCurrent();
        IsRunning = false;
    }

    IEnumerator RunSequence()
    {
        IsRunning = true;
        do
        {
            for (int i = 0; i < prompts.Count; i++)
            {
                CurrentIndex = i;
                string prompt = prompts[i];
                if (string.IsNullOrWhiteSpace(prompt)) continue;

                yield return StartCoroutine(RunSinglePrompt(prompt));

                if (delayBetweenPrompts > 0f)
                    yield return new WaitForSeconds(delayBetweenPrompts);
            }
        } while (loopSequence && enabled);

        IsRunning = false;
        CurrentIndex = -1;
        _routine = null;
    }

    IEnumerator RunSinglePrompt(string prompt)
    {
        if (llmWorldDirector == null)
        {
            Debug.LogError("[AutoPromptSequencer] Nessun LLMWorldDirector trovato in scena — impossibile eseguire il prompt.");
            yield break;
        }

        _terrainDone = false;
        _failed = false;

        LLMWorldDirector.OnTerrainPipelineDone += OnTerrainDone;
        LLMWorldDirector.OnGenerationFailed    += OnFailed;

        Debug.Log($"<color=cyan>[AutoPromptSequencer]</color> Avvio prompt {CurrentIndex + 1}/{prompts.Count}: \"{prompt}\"");
        llmWorldDirector.SendRequestToLLM(prompt);

        float elapsed = 0f;
        while (!_terrainDone && !_failed && elapsed < perPromptTimeout)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        UnsubscribeCurrent();

        if (_failed)
        {
            Debug.LogWarning($"[AutoPromptSequencer] Prompt {CurrentIndex + 1}/{prompts.Count} fallito, passo al successivo.");
            yield break;
        }
        if (!_terrainDone)
        {
            Debug.LogWarning($"[AutoPromptSequencer] Prompt {CurrentIndex + 1}/{prompts.Count} in timeout dopo {perPromptTimeout}s, passo al successivo.");
            yield break;
        }

        // Il terreno è pronto (OnTerrainPipelineDone), ma la generazione dei modelli 3D del bioma
        // può proseguire in background (vedi GenerateAsset.IsGenerating, avviato da
        // LLMWorldDirector.SizeFrequencyCoroutine senza attenderlo) — aspettiamo anche quella prima
        // di considerare il mondo davvero "finito di generare", stesso identico criterio usato da
        // FirstGeneration.RunGeneration per la primissima generazione del gioco.
        while (GenerateAsset.IsGenerating)
            yield return null;

        Debug.Log($"<color=cyan>[AutoPromptSequencer]</color> Prompt {CurrentIndex + 1}/{prompts.Count} completato.");
    }

    void OnTerrainDone() => _terrainDone = true;
    void OnFailed(string reason) => _failed = true;

    void UnsubscribeCurrent()
    {
        LLMWorldDirector.OnTerrainPipelineDone -= OnTerrainDone;
        LLMWorldDirector.OnGenerationFailed    -= OnFailed;
    }
}
