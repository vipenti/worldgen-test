using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Pannello Opzioni apribile a runtime (pulsante UI o tasto fisico) con tre sezioni:
/// 1) Generali — device audio input, indirizzo/porte del server LLM/immagini/3D e qualità ombre
///    (scrive su <see cref="GameSettings"/>, che è l'unica sorgente di verità letta da tutti gli
///    script di rete e da ShadowQualityController — vedi GameSettings.cs).
/// 2) Parametri mondo — densità oggetti, render distance, ampiezza transizione bioma, on/off
///    del percorso centrale, scala oggetti per categoria di size (huge/big/normal/small/tiny) e
///    dimensioni del percorso (larghezza/sfumatura ai bordi), applicati live tramite
///    GameSettings.ApplyWorldParams / ApplyObjectScale / ApplyPathDimensions.
/// 3) Info mondo — dump di sola lettura di llm_generated.json (parametri bioma corrente) e
///    current_objects.json (catalogo oggetti del bioma), con verifica su disco di quali
///    oggetti hanno già un voxjson generato.
/// </summary>
public class OptionsPanel : MonoBehaviour
{
    [Header("Apertura")]
    public GameObject panelRoot;
    public Button openButton;
    public Button closeButton;
    [Tooltip("Tasto fisico che apre/chiude il pannello, ignorato mentre si scrive in un campo di testo.")]
    public Key toggleKey = Key.O;

    [Header("Tab")]
    public Button tabGeneraliButton;
    public Button tabMondoButton;
    public Button tabInfoButton;
    public GameObject sectionGenerali;
    public GameObject sectionMondo;
    public GameObject sectionInfo;

    [Header("Sezione Generali")]
    [Tooltip("Testo che mostra il device microfono attualmente selezionato (\"Default\" o il nome del device).")]
    public TMP_Text micDeviceLabel;
    [Tooltip("Pulsante che fa scorrere ciclicamente Microphone.devices (Default incluso).")]
    public Button micCycleButton;
    public TMP_InputField serverHostField;
    public TMP_InputField llmPortField;
    public TMP_InputField imagePortField;
    public TMP_InputField gen3dPortField;
    public Button applyGeneraliButton;
    [Tooltip("Opzioni attese in ordine: Off, Low, Medium, High (indice 0-3, vedi GameSettingsData.shadowQuality / ShadowQualityController). Applicata subito al cambio, senza bisogno del pulsante Apply.")]
    public TMP_Dropdown shadowQualityDropdown;

    [Header("Sezione Parametri mondo")]
    [Tooltip("0.5x - 2x, scala le dimensioni cella di piazzamento (WorldManager.cellVeryCommon/Common/...).")]
    public Slider densitySlider;
    public TMP_Text densityValueLabel;
    [Tooltip("0.5x - 2x, scala tutte le renderDist* (huge/big/normal/small/tiny).")]
    public Slider renderDistSlider;
    public TMP_Text renderDistValueLabel;
    [Tooltip("In chunk, sovrascrive WorldManager.transitionLengthInChunks.")]
    public Slider transitionSlider;
    public TMP_Text transitionValueLabel;
    public Toggle pathToggle;

    [Header("Sezione Parametri mondo — Scala oggetti (per categoria size)")]
    [Tooltip("0.5x - 2x, scala WorldManager.scaleHuge.")]
    public Slider scaleHugeSlider;
    public TMP_Text scaleHugeValueLabel;
    [Tooltip("0.5x - 2x, scala WorldManager.scaleBig.")]
    public Slider scaleBigSlider;
    public TMP_Text scaleBigValueLabel;
    [Tooltip("0.5x - 2x, scala WorldManager.scaleNormal.")]
    public Slider scaleNormalSlider;
    public TMP_Text scaleNormalValueLabel;
    [Tooltip("0.5x - 2x, scala WorldManager.scaleSmall.")]
    public Slider scaleSmallSlider;
    public TMP_Text scaleSmallValueLabel;
    [Tooltip("0.5x - 2x, scala WorldManager.scaleTiny.")]
    public Slider scaleTinySlider;
    public TMP_Text scaleTinyValueLabel;

    [Header("Sezione Parametri mondo — Percorso/strada")]
    [Tooltip("0.5x - 2x, scala WorldManager.pathWidth (larghezza del percorso centrale).")]
    public Slider pathWidthSlider;
    public TMP_Text pathWidthValueLabel;
    [Tooltip("0.5x - 2x, scala WorldManager.pathBlendWidth (sfumatura ai bordi del percorso).")]
    public Slider pathBlendWidthSlider;
    public TMP_Text pathBlendWidthValueLabel;

    public Button applyMondoButton;
    [Tooltip("Facoltativo: testo di avviso mostrato quando si modificano parametri che si applicano solo alla PROSSIMA generazione/transizione, non retroattivamente.")]
    public TMP_Text mondoHintLabel;

    [Header("Sezione Info mondo")]
    public TMP_Text infoText;
    public Button refreshInfoButton;

    void Awake()
    {
        if (panelRoot != null) panelRoot.SetActive(false);
        if (mondoHintLabel != null)
            mondoHintLabel.text = "Density, object scale and transition apply from the next biome/chunk change; render distance and path (on/off, width, blend) are immediate.";
    }

    void OnEnable()
    {
        if (openButton != null) openButton.onClick.AddListener(TogglePanel);
        if (closeButton != null) closeButton.onClick.AddListener(ClosePanel);
        if (tabGeneraliButton != null) tabGeneraliButton.onClick.AddListener(OnTabGenerali);
        if (tabMondoButton != null) tabMondoButton.onClick.AddListener(OnTabMondo);
        if (tabInfoButton != null) tabInfoButton.onClick.AddListener(OnTabInfo);
        if (applyGeneraliButton != null) applyGeneraliButton.onClick.AddListener(ApplyGenerali);
        if (micCycleButton != null) micCycleButton.onClick.AddListener(CycleMicDevice);
        if (shadowQualityDropdown != null) shadowQualityDropdown.onValueChanged.AddListener(OnShadowQualityChanged);
        if (applyMondoButton != null) applyMondoButton.onClick.AddListener(ApplyMondo);
        if (refreshInfoButton != null) refreshInfoButton.onClick.AddListener(RefreshInfo);
        if (densitySlider != null) densitySlider.onValueChanged.AddListener(OnDensityChanged);
        if (renderDistSlider != null) renderDistSlider.onValueChanged.AddListener(OnRenderDistChanged);
        if (transitionSlider != null) transitionSlider.onValueChanged.AddListener(OnTransitionChanged);
        if (scaleHugeSlider != null) scaleHugeSlider.onValueChanged.AddListener(OnScaleHugeChanged);
        if (scaleBigSlider != null) scaleBigSlider.onValueChanged.AddListener(OnScaleBigChanged);
        if (scaleNormalSlider != null) scaleNormalSlider.onValueChanged.AddListener(OnScaleNormalChanged);
        if (scaleSmallSlider != null) scaleSmallSlider.onValueChanged.AddListener(OnScaleSmallChanged);
        if (scaleTinySlider != null) scaleTinySlider.onValueChanged.AddListener(OnScaleTinyChanged);
        if (pathWidthSlider != null) pathWidthSlider.onValueChanged.AddListener(OnPathWidthChanged);
        if (pathBlendWidthSlider != null) pathBlendWidthSlider.onValueChanged.AddListener(OnPathBlendWidthChanged);
    }

    void OnDisable()
    {
        if (openButton != null) openButton.onClick.RemoveListener(TogglePanel);
        if (closeButton != null) closeButton.onClick.RemoveListener(ClosePanel);
        if (tabGeneraliButton != null) tabGeneraliButton.onClick.RemoveListener(OnTabGenerali);
        if (tabMondoButton != null) tabMondoButton.onClick.RemoveListener(OnTabMondo);
        if (tabInfoButton != null) tabInfoButton.onClick.RemoveListener(OnTabInfo);
        if (applyGeneraliButton != null) applyGeneraliButton.onClick.RemoveListener(ApplyGenerali);
        if (micCycleButton != null) micCycleButton.onClick.RemoveListener(CycleMicDevice);
        if (shadowQualityDropdown != null) shadowQualityDropdown.onValueChanged.RemoveListener(OnShadowQualityChanged);
        if (applyMondoButton != null) applyMondoButton.onClick.RemoveListener(ApplyMondo);
        if (refreshInfoButton != null) refreshInfoButton.onClick.RemoveListener(RefreshInfo);
        if (densitySlider != null) densitySlider.onValueChanged.RemoveListener(OnDensityChanged);
        if (renderDistSlider != null) renderDistSlider.onValueChanged.RemoveListener(OnRenderDistChanged);
        if (transitionSlider != null) transitionSlider.onValueChanged.RemoveListener(OnTransitionChanged);
        if (scaleHugeSlider != null) scaleHugeSlider.onValueChanged.RemoveListener(OnScaleHugeChanged);
        if (scaleBigSlider != null) scaleBigSlider.onValueChanged.RemoveListener(OnScaleBigChanged);
        if (scaleNormalSlider != null) scaleNormalSlider.onValueChanged.RemoveListener(OnScaleNormalChanged);
        if (scaleSmallSlider != null) scaleSmallSlider.onValueChanged.RemoveListener(OnScaleSmallChanged);
        if (scaleTinySlider != null) scaleTinySlider.onValueChanged.RemoveListener(OnScaleTinyChanged);
        if (pathWidthSlider != null) pathWidthSlider.onValueChanged.RemoveListener(OnPathWidthChanged);
        if (pathBlendWidthSlider != null) pathBlendWidthSlider.onValueChanged.RemoveListener(OnPathBlendWidthChanged);
    }

    void Update()
    {
        if (Keyboard.current == null || IsTypingInTextField()) return;
        if (Keyboard.current[toggleKey].wasPressedThisFrame) TogglePanel();
    }

    static bool IsTypingInTextField()
    {
        var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        return selected != null && selected.GetComponent<TMP_InputField>() != null;
    }

    // ── Apertura / tab ──────────────────────────────────────────────────

    public void TogglePanel()
    {
        if (panelRoot == null) return;
        bool opening = !panelRoot.activeSelf;
        panelRoot.SetActive(opening);
        SetCursorForPanel(opening);
        if (opening)
        {
            PopulateFromSettings();
            OnTabGenerali();
        }
    }

    public void ClosePanel()
    {
        if (panelRoot != null) panelRoot.SetActive(false);
        SetCursorForPanel(false);
    }

    // Mentre il pannello è aperto, sblocca il cursore per poter cliccare sui controlli e usa lo
    // stesso flag già letto da MouseLook/PlayerMovement/FootstepAudio per "congelare" camera e
    // movimento — altrimenti il click sul pulsante veniva interpretato come "riaggancia il mouse
    // alla camera" (comportamento di default di Unity quando Cursor.lockState resta Locked) prima
    // ancora che l'evento arrivasse al Button.
    static void SetCursorForPanel(bool panelOpen)
    {
        LLMWorldDirector.IsUIFocused = panelOpen;
        Cursor.lockState = panelOpen ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = panelOpen;
    }

    void OnTabGenerali() => ShowSection(sectionGenerali);
    void OnTabMondo() => ShowSection(sectionMondo);
    void OnTabInfo() { ShowSection(sectionInfo); RefreshInfo(); }

    void ShowSection(GameObject section)
    {
        if (sectionGenerali != null) sectionGenerali.SetActive(section == sectionGenerali);
        if (sectionMondo != null) sectionMondo.SetActive(section == sectionMondo);
        if (sectionInfo != null) sectionInfo.SetActive(section == sectionInfo);
    }

    // ── Sezione Generali ────────────────────────────────────────────────

    static List<string> MicOptions()
    {
        var options = new List<string> { "Default" };
        options.AddRange(Microphone.devices);
        return options;
    }

    void CycleMicDevice()
    {
        var options = MicOptions();
        string current = micDeviceLabel != null ? micDeviceLabel.text : "Default";
        int idx = options.IndexOf(current);
        idx = (idx + 1) % options.Count;
        if (micDeviceLabel != null) micDeviceLabel.text = options[idx];
    }

    void PopulateFromSettings()
    {
        var data = GameSettings.Instance != null ? GameSettings.Instance.Data : new GameSettingsData();

        if (micDeviceLabel != null)
        {
            var options = MicOptions();
            string current = string.IsNullOrEmpty(data.micDeviceName) ? "Default" : data.micDeviceName;
            micDeviceLabel.text = options.Contains(current) ? current : "Default";
        }

        if (serverHostField != null) serverHostField.text = data.serverHost;
        if (llmPortField != null) llmPortField.text = data.llmPort.ToString();
        if (imagePortField != null) imagePortField.text = data.imagePort.ToString();
        if (gen3dPortField != null) gen3dPortField.text = data.gen3dPort.ToString();
        if (shadowQualityDropdown != null) shadowQualityDropdown.SetValueWithoutNotify(data.shadowQuality);

        if (densitySlider != null) densitySlider.SetValueWithoutNotify(data.objectDensityMultiplier);
        if (renderDistSlider != null) renderDistSlider.SetValueWithoutNotify(data.renderDistanceMultiplier);
        if (transitionSlider != null) transitionSlider.SetValueWithoutNotify(data.transitionLengthInChunks);
        if (pathToggle != null) pathToggle.isOn = data.enablePath;

        if (scaleHugeSlider != null) scaleHugeSlider.SetValueWithoutNotify(data.scaleHugeMultiplier);
        if (scaleBigSlider != null) scaleBigSlider.SetValueWithoutNotify(data.scaleBigMultiplier);
        if (scaleNormalSlider != null) scaleNormalSlider.SetValueWithoutNotify(data.scaleNormalMultiplier);
        if (scaleSmallSlider != null) scaleSmallSlider.SetValueWithoutNotify(data.scaleSmallMultiplier);
        if (scaleTinySlider != null) scaleTinySlider.SetValueWithoutNotify(data.scaleTinyMultiplier);
        if (pathWidthSlider != null) pathWidthSlider.SetValueWithoutNotify(data.pathWidthMultiplier);
        if (pathBlendWidthSlider != null) pathBlendWidthSlider.SetValueWithoutNotify(data.pathBlendWidthMultiplier);

        OnDensityChanged(data.objectDensityMultiplier);
        OnRenderDistChanged(data.renderDistanceMultiplier);
        OnTransitionChanged(data.transitionLengthInChunks);
        OnScaleHugeChanged(data.scaleHugeMultiplier);
        OnScaleBigChanged(data.scaleBigMultiplier);
        OnScaleNormalChanged(data.scaleNormalMultiplier);
        OnScaleSmallChanged(data.scaleSmallMultiplier);
        OnScaleTinyChanged(data.scaleTinyMultiplier);
        OnPathWidthChanged(data.pathWidthMultiplier);
        OnPathBlendWidthChanged(data.pathBlendWidthMultiplier);
    }

    void ApplyGenerali()
    {
        if (GameSettings.Instance == null)
        {
            Debug.LogWarning("[OptionsPanel] GameSettings non presente in scena, impossibile applicare.");
            return;
        }

        if (micDeviceLabel != null)
        {
            string selected = micDeviceLabel.text;
            GameSettings.Instance.ApplyMicDevice(selected == "Default" ? null : selected);
        }

        string host = serverHostField != null && !string.IsNullOrWhiteSpace(serverHostField.text)
            ? serverHostField.text
            : GameSettings.Instance.Data.serverHost;
        int llmPort = ParseIntOr(llmPortField, GameSettings.Instance.Data.llmPort);
        int imagePort = ParseIntOr(imagePortField, GameSettings.Instance.Data.imagePort);
        int gen3dPort = ParseIntOr(gen3dPortField, GameSettings.Instance.Data.gen3dPort);
        GameSettings.Instance.ApplyServerSettings(host, llmPort, imagePort, gen3dPort);
    }

    static int ParseIntOr(TMP_InputField field, int fallback) =>
        field != null && int.TryParse(field.text, out int v) ? v : fallback;

    // Applicata subito al cambio (a differenza degli altri campi di "Generali", che aspettano il
    // pulsante Apply): è un'impostazione puramente grafica/performance, ha senso vederne l'effetto
    // in tempo reale mentre si regola, specialmente in VR.
    void OnShadowQualityChanged(int level)
    {
        if (GameSettings.Instance == null)
        {
            Debug.LogWarning("[OptionsPanel] GameSettings non presente in scena, impossibile applicare.");
            return;
        }
        GameSettings.Instance.ApplyShadowQuality(level);
    }

    // ── Sezione Parametri mondo ─────────────────────────────────────────

    void OnDensityChanged(float v) { if (densityValueLabel != null) densityValueLabel.text = $"{v:F2}x"; }
    void OnRenderDistChanged(float v) { if (renderDistValueLabel != null) renderDistValueLabel.text = $"{v:F2}x"; }
    void OnTransitionChanged(float v) { if (transitionValueLabel != null) transitionValueLabel.text = $"{(int)v} chunks"; }
    void OnScaleHugeChanged(float v) { if (scaleHugeValueLabel != null) scaleHugeValueLabel.text = $"{v:F2}x"; }
    void OnScaleBigChanged(float v) { if (scaleBigValueLabel != null) scaleBigValueLabel.text = $"{v:F2}x"; }
    void OnScaleNormalChanged(float v) { if (scaleNormalValueLabel != null) scaleNormalValueLabel.text = $"{v:F2}x"; }
    void OnScaleSmallChanged(float v) { if (scaleSmallValueLabel != null) scaleSmallValueLabel.text = $"{v:F2}x"; }
    void OnScaleTinyChanged(float v) { if (scaleTinyValueLabel != null) scaleTinyValueLabel.text = $"{v:F2}x"; }
    void OnPathWidthChanged(float v) { if (pathWidthValueLabel != null) pathWidthValueLabel.text = $"{v:F2}x"; }
    void OnPathBlendWidthChanged(float v) { if (pathBlendWidthValueLabel != null) pathBlendWidthValueLabel.text = $"{v:F2}x"; }

    void ApplyMondo()
    {
        if (GameSettings.Instance == null)
        {
            Debug.LogWarning("[OptionsPanel] GameSettings non presente in scena, impossibile applicare.");
            return;
        }
        float density = densitySlider != null ? densitySlider.value : 1f;
        float renderDist = renderDistSlider != null ? renderDistSlider.value : 1f;
        int transition = transitionSlider != null ? Mathf.RoundToInt(transitionSlider.value) : 8;
        bool path = pathToggle == null || pathToggle.isOn;
        GameSettings.Instance.ApplyWorldParams(density, renderDist, transition, path);

        float scaleHugeM = scaleHugeSlider != null ? scaleHugeSlider.value : 1f;
        float scaleBigM = scaleBigSlider != null ? scaleBigSlider.value : 1f;
        float scaleNormalM = scaleNormalSlider != null ? scaleNormalSlider.value : 1f;
        float scaleSmallM = scaleSmallSlider != null ? scaleSmallSlider.value : 1f;
        float scaleTinyM = scaleTinySlider != null ? scaleTinySlider.value : 1f;
        GameSettings.Instance.ApplyObjectScale(scaleHugeM, scaleBigM, scaleNormalM, scaleSmallM, scaleTinyM);

        float pathWidthM = pathWidthSlider != null ? pathWidthSlider.value : 1f;
        float pathBlendWidthM = pathBlendWidthSlider != null ? pathBlendWidthSlider.value : 1f;
        GameSettings.Instance.ApplyPathDimensions(pathWidthM, pathBlendWidthM);
    }

    // ── Sezione Info mondo ──────────────────────────────────────────────

    void RefreshInfo()
    {
        if (infoText == null) return;
        var sb = new StringBuilder();

        sb.AppendLine("<b>── Current biome (llm_generated.json) ──</b>");
        AppendRawJson(sb, "llm_generated.json");

        sb.AppendLine();
        sb.AppendLine("<b>── Biome objects (current_objects.json) ──</b>");
        AppendObjectsStatus(sb);

        infoText.text = sb.ToString();
    }

    void AppendRawJson(StringBuilder sb, string fileName)
    {
        string path = Path.Combine(PathSettings.GetJsonFolder(), fileName);
        if (!File.Exists(path)) { sb.AppendLine("(file not found)"); return; }
        try
        {
            sb.AppendLine(JToken.Parse(File.ReadAllText(path)).ToString(Newtonsoft.Json.Formatting.Indented));
        }
        catch (System.Exception ex)
        {
            sb.AppendLine($"(read error: {ex.Message})");
        }
    }

    // Per ogni oggetto del catalogo del bioma corrente, verifica se la sua mesh è già in
    // VoxelRenderer.RuntimeMeshCache (base o una delle tier LOD) — risponde direttamente alla
    // domanda "sono stati generati tutti gli oggetti del bioma?". Usiamo la cache a runtime e non
    // l'esistenza dei file .voxjson su disco perché in build i voxjson non vengono MAI scritti su
    // disco per design (solo in Editor) — un check su disco avrebbe sempre mostrato 0/N in build
    // anche per oggetti generati e piazzati correttamente.
    void AppendObjectsStatus(StringBuilder sb)
    {
        string path = Path.Combine(PathSettings.GetJsonFolder(), "current_objects.json");
        if (!File.Exists(path)) { sb.AppendLine("(file not found)"); return; }

        try
        {
            var root = JObject.Parse(File.ReadAllText(path));
            int total = 0, ready = 0;

            foreach (var prop in root.Properties())
            {
                total++;
                string name = prop.Name;
                bool hasBase = VoxelRenderer.RuntimeMeshCache.ContainsKey(name);
                bool hasLod = VoxelRenderer.RuntimeMeshCache.ContainsKey(name + "_low")
                           || VoxelRenderer.RuntimeMeshCache.ContainsKey(name + "_medium")
                           || VoxelRenderer.RuntimeMeshCache.ContainsKey(name + "_high");
                bool isReady = hasBase || hasLod;
                if (isReady) ready++;

                string size = prop.Value["size"]?.ToString() ?? "?";
                string freq = prop.Value["frequency"]?.ToString() ?? "?";
                // "OK"/"X" invece di ✓/✗: quei glifi non esistono nel font TMP di default
                // (LiberationSans SDF) e in build venivano sostituiti da un quadratino, con tanto
                // di spam di warning in console a ogni frame in cui il pannello Info era visibile.
                string status = isReady ? "<color=#66ff66>OK</color>" : "<color=#ff6666>X</color>";
                sb.AppendLine($"{status}  {name}  <color=#999999>({size}, {freq})</color>");
            }

            sb.AppendLine();
            sb.AppendLine($"<b>Total: {ready}/{total} objects generated on disk.</b>");
        }
        catch (System.Exception ex)
        {
            sb.AppendLine($"(read error: {ex.Message})");
        }
    }
}
