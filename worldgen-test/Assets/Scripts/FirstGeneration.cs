using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;
using System.Collections;

public class FirstGeneration : MonoBehaviour
{
    [Header("UI - Input Panel")]
    public GameObject inputPanel;
    public TMP_InputField promptInput;
    public Button generateButton;

    [Header("UI - Loading Panel")]
    public GameObject loadingPanel;
    public Slider progressBar;
    public TMP_Text statusText;
    public TMP_Text percentText;

    [Header("References")]
    public LLMWorldDirector llmWorldDirector;

    [Header("Scene")]
    public string mainSceneName = "MainScene";

    private bool _terrainDone;
    private bool _failed;
    private bool _isGenerating;

    private static readonly WaitForSeconds WaitError   = new(2.5f);
    private static readonly WaitForSeconds WaitSuccess = new(0.6f);

    void Start()
    {
        if (inputPanel   != null) inputPanel.SetActive(true);
        if (loadingPanel != null) loadingPanel.SetActive(false);
        SetProgress(0f, "Enter a world description and press Generate.");
        if (generateButton != null) generateButton.onClick.AddListener(OnGenerateClicked);
    }

    void OnDestroy()
    {
        if (generateButton != null) generateButton.onClick.RemoveListener(OnGenerateClicked);
        UnsubscribeAll();
    }

    void OnGenerateClicked()
    {
        if (_isGenerating) return;
        string prompt = promptInput != null ? promptInput.text.Trim() : "";
        if (string.IsNullOrEmpty(prompt)) return;
        StartCoroutine(RunGeneration(prompt));
    }

    IEnumerator RunGeneration(string prompt)
    {
        _isGenerating = true;
        _terrainDone  = false;
        _failed       = false;

        if (inputPanel   != null) inputPanel.SetActive(false);
        if (loadingPanel != null) loadingPanel.SetActive(true);
        SetProgress(0f, "Starting...");

        LLMWorldDirector.OnGenerationProgress += OnProgress;
        LLMWorldDirector.OnTerrainPipelineDone += OnTerrainDone;
        LLMWorldDirector.OnGenerationFailed    += OnFailed;
        GenerateAsset.OnAssetProgress          += OnAssetProgress;

        llmWorldDirector.SendRequestToLLM(prompt);

        // Wait for terrain pipeline — timeout after 5 minutes
        float elapsed = 0f;
        while (!_terrainDone && !_failed && elapsed < 300f)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        LLMWorldDirector.OnGenerationProgress  -= OnProgress;
        LLMWorldDirector.OnTerrainPipelineDone -= OnTerrainDone;
        LLMWorldDirector.OnGenerationFailed    -= OnFailed;

        if (_failed || !_terrainDone)
        {
            SetProgress(0f, "Generation failed. Please try again.");
            yield return WaitError;
            if (loadingPanel != null) loadingPanel.SetActive(false);
            if (inputPanel   != null) inputPanel.SetActive(true);
            _isGenerating = false;
            yield break;
        }

        SetProgress(0.65f, "Creating 3D models...");

        // Wait one frame for GenerateNew coroutine to start (if any assets)
        yield return null;
        yield return null;

        while (GenerateAsset.IsGenerating)
            yield return null;

        GenerateAsset.OnAssetProgress -= OnAssetProgress;

        SetProgress(1f, "World ready!");
        yield return WaitSuccess;

        SceneManager.LoadScene(mainSceneName);
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    void OnProgress(string status, float progress) => SetProgress(progress, status);

    void OnTerrainDone() => _terrainDone = true;

    void OnFailed(string reason)
    {
        _failed = true;
        SetProgress(0f, $"Error: {reason}");
    }

    void OnAssetProgress(int done, int total)
    {
        if (total <= 0) return;
        float fraction = (float)done / total;
        SetProgress(0.65f + fraction * 0.35f, $"Creating 3D models... ({done}/{total})");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    void SetProgress(float value, string status)
    {
        value = Mathf.Clamp01(value);
        if (progressBar != null) progressBar.value = value;
        if (statusText  != null) statusText.text   = status;
        if (percentText != null) percentText.text  = $"{Mathf.RoundToInt(value * 100)}%";
    }

    void UnsubscribeAll()
    {
        LLMWorldDirector.OnGenerationProgress  -= OnProgress;
        LLMWorldDirector.OnTerrainPipelineDone -= OnTerrainDone;
        LLMWorldDirector.OnGenerationFailed    -= OnFailed;
        GenerateAsset.OnAssetProgress          -= OnAssetProgress;
    }
}
