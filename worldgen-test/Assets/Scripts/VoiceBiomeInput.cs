using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Whisper;
using Whisper.Utils;

/// <summary>
/// Input vocale "push-to-talk" per la richiesta del bioma, pensato per un gioco streammato
/// da PC (niente vincoli hardware Quest: Whisper gira lato host). Tenendo premuto il
/// pulsante si registra dal microfono — in pratica quasi sempre 1-2s, comunque cappato a
/// <see cref="MicrophoneRecord.maxLengthSec"/> secondi — al rilascio il buffer viene
/// trascritto in locale/offline con Whisper (modello "base" consigliato) e il testo viene
/// mostrato in un pannello di conferma. Solo dopo il "Sì" dell'utente il testo viene
/// inoltrato esattamente come se fosse stato scritto nel campo di input esistente, tramite
/// <see cref="OnConfirmedText"/> — da agganciare in Inspector a
/// LLMWorldDirector.SendRequestToLLM(string) (richiesta bioma in-game) oppure a
/// FirstGeneration.GenerateFromText(string) (prima generazione del mondo).
/// </summary>
public class VoiceBiomeInput : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
{
    [System.Serializable] public class StringEvent : UnityEvent<string> { }

    [Header("Whisper")]
    public WhisperManager whisper;
    public MicrophoneRecord microphoneRecord;

    [Header("UI - Pulsante push-to-talk")]
    [Tooltip("Facoltativo: testo del pulsante, aggiornato per dare feedback (\"Hold and speak...\" / \"Capturing...\" / \"Transcribing...\").")]
    public TMP_Text buttonLabel;

    [Header("UI - Pannello di conferma")]
    public GameObject confirmPanel;
    public TMP_Text transcribedText;
    public Button confirmButton;
    public Button cancelButton;

    [Header("Destinazione")]
    [Tooltip("Riferimento diretto opzionale: se assegnato, invoca direttamente SendRequestToLLM(text) alla conferma (uso in-game, MainScene).")]
    public LLMWorldDirector llmWorldDirector;
    [Tooltip("Riferimento diretto opzionale: se assegnato, invoca direttamente GenerateFromText(text) alla conferma (uso nella schermata di prima generazione).")]
    public FirstGeneration firstGeneration;
    [Tooltip("Invocato con il testo trascritto SOLO dopo la conferma dell'utente. Utile per agganci aggiuntivi via Inspector.")]
    public StringEvent OnConfirmedText;

    [Tooltip("Registrazioni più corte di questa soglia (rilascio accidentale, rumore) vengono scartate senza trascriverle.")]
    public float minRecordSeconds = 0.3f;

    [Header("Tasto fisico")]
    [Tooltip("Tenendo premuto questo tasto (in aggiunta al click sul pulsante UI) si registra allo stesso modo.")]
    public Key pushToTalkKey = Key.V;

    string _lastTranscription;
    bool _isTranscribing;
    bool _recordingFromKey;

    void Awake()
    {
        if (microphoneRecord == null) microphoneRecord = GetComponent<MicrophoneRecord>();

        // Push-to-talk: non vogliamo che il microfono si fermi da solo per silenzio (VAD) o
        // che registri più dei ~30s massimi attesi — è l'utente a controllare inizio/fine
        // tenendo premuto il pulsante.
        if (microphoneRecord != null)
        {
            microphoneRecord.vadStop = false;
            if (microphoneRecord.maxLengthSec > 30) microphoneRecord.maxLengthSec = 30;
        }

        if (confirmPanel != null) confirmPanel.SetActive(false);
        SetLabel("Hold and speak...");

        ApplyMicDeviceFromSettings();
    }

    void ApplyMicDeviceFromSettings()
    {
        if (microphoneRecord == null || GameSettings.Instance == null) return;
        string device = GameSettings.Instance.Data.micDeviceName;
        try
        {
            microphoneRecord.SelectedMicDevice = string.IsNullOrEmpty(device) ? null : device;
        }
        catch (System.ArgumentException)
        {
            // Il device salvato non è più disponibile (scollegato/rinominato) — resta sul default.
            Debug.LogWarning($"[VoiceBiomeInput] Device microfono '{device}' non trovato, uso il default.");
            microphoneRecord.SelectedMicDevice = null;
        }
    }

    void OnEnable()
    {
        if (microphoneRecord != null) microphoneRecord.OnRecordStop += OnRecordStop;
        if (confirmButton != null) confirmButton.onClick.AddListener(OnConfirmClicked);
        if (cancelButton != null) cancelButton.onClick.AddListener(OnCancelClicked);
        if (GameSettings.Instance != null) GameSettings.Instance.OnAudioSettingsChanged += ApplyMicDeviceFromSettings;
    }

    void OnDisable()
    {
        if (microphoneRecord != null) microphoneRecord.OnRecordStop -= OnRecordStop;
        if (confirmButton != null) confirmButton.onClick.RemoveListener(OnConfirmClicked);
        if (cancelButton != null) cancelButton.onClick.RemoveListener(OnCancelClicked);
        if (GameSettings.Instance != null) GameSettings.Instance.OnAudioSettingsChanged -= ApplyMicDeviceFromSettings;

        // Se il componente viene disattivato a metà registrazione (cambio scena/pannello),
        // non lasciamo il microfono aperto in background.
        if (microphoneRecord != null && microphoneRecord.IsRecording) microphoneRecord.StopRecord();
    }

    // ── Push-to-talk ─────────────────────────────────────────────────────────

    // Pulsante UI (mouse/touch/controller sull'EventSystem)
    public void OnPointerDown(PointerEventData eventData) => BeginRecording();
    public void OnPointerUp(PointerEventData eventData) => EndRecording();

    void Update()
    {
        if (Keyboard.current == null) return;

        // Se il pannello di conferma è aperto, "Y"/"N" confermano o annullano — al posto del
        // click sui pulsanti Sì/No — e in questo stato ignoriamo il tasto push-to-talk (non ha
        // senso avviare una nuova registrazione mentre si sta ancora confermando la precedente).
        if (confirmPanel != null && confirmPanel.activeSelf)
        {
            if (!IsTypingInTextField())
            {
                if (Keyboard.current.yKey.wasPressedThisFrame) OnConfirmClicked();
                else if (Keyboard.current.nKey.wasPressedThisFrame) OnCancelClicked();
            }
            return;
        }

        // Tasto fisico tenuto premuto (es. "V"): stesso comportamento del pulsante UI, ma
        // ignorato se l'utente sta scrivendo in un campo di testo (per non intercettare la
        // lettera "v" digitata a mano).
        var keyControl = Keyboard.current[pushToTalkKey];

        if (keyControl.wasPressedThisFrame && !IsTypingInTextField())
        {
            _recordingFromKey = true;
            BeginRecording();
        }
        else if (keyControl.wasReleasedThisFrame && _recordingFromKey)
        {
            _recordingFromKey = false;
            EndRecording();
        }
    }

    static bool IsTypingInTextField()
    {
        var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        return selected != null && selected.GetComponent<TMP_InputField>() != null;
    }

    void BeginRecording()
    {
        if (_isTranscribing || microphoneRecord == null || microphoneRecord.IsRecording) return;
        if (whisper == null || !whisper.IsLoaded)
        {
            SetLabel(whisper != null && whisper.IsLoading ? "Modello vocale in caricamento..." : "Modello vocale non pronto");
            return;
        }

        if (confirmPanel != null) confirmPanel.SetActive(false); // nasconde un'eventuale conferma precedente
        microphoneRecord.StartRecord();
        SetLabel("Capturing... release to finish");
    }

    void EndRecording()
    {
        if (microphoneRecord == null || !microphoneRecord.IsRecording) return;
        microphoneRecord.StopRecord();
        SetLabel("Transcribing...");
    }

    async void OnRecordStop(AudioChunk recordedAudio)
    {
        if (recordedAudio.Length < minRecordSeconds)
        {
            SetLabel("Hold and speak...");
            return;
        }

        _isTranscribing = true;
        var result = await whisper.GetTextAsync(recordedAudio.Data, recordedAudio.Frequency, recordedAudio.Channels);
        _isTranscribing = false;

        if (result == null || string.IsNullOrWhiteSpace(result.Result))
        {
            SetLabel("Hold and speak...");
            return;
        }

        SetLabel("Hold and speak...");
        _lastTranscription = result.Result.Trim();
        ShowConfirmation(_lastTranscription);
    }

    // ── Conferma ────────────────────────────────────────────────────────────

    void ShowConfirmation(string text)
    {
        if (transcribedText != null) transcribedText.text = text;
        if (confirmPanel != null) confirmPanel.SetActive(true);
    }

    void OnConfirmClicked()
    {
        if (confirmPanel != null) confirmPanel.SetActive(false);
        if (!string.IsNullOrEmpty(_lastTranscription))
        {
            llmWorldDirector?.SendRequestToLLM(_lastTranscription);
            firstGeneration?.GenerateFromText(_lastTranscription);
            OnConfirmedText?.Invoke(_lastTranscription);
        }
        _lastTranscription = null;
    }

    void OnCancelClicked()
    {
        if (confirmPanel != null) confirmPanel.SetActive(false);
        _lastTranscription = null;
        SetLabel("Hold and speak...");
    }

    void SetLabel(string text)
    {
        if (buttonLabel != null) buttonLabel.text = text;
    }
}
