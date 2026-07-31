using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class LogPanelUI : MonoBehaviour
{
    [Header("Behaviour")]
    public int maxEntries = 60;
    public float fontSize = 20f;

    [Header("Colors")]
    public Color panelColor = new Color(0.05f, 0.05f, 0.08f, 0.6f);
    public Color eventColor = new Color(1f, 0.85f, 0.45f, 1f);
    public Color progressColor = new Color(0.82f, 0.88f, 0.95f, 1f);
    public Color errorColor = new Color(1f, 0.4f, 0.4f, 1f);

    TextMeshProUGUI _logText;
    ScrollRect _scrollRect;
    readonly List<string> _lines = new List<string>();
    bool _lastWasProgress = false;
    Coroutine _scrollCoroutine;

    void Awake()
    {
        BuildUI();
    }

    public void LogEvent(string text) => AddLine(text, eventColor, isProgress: false);
    public void LogProgress(string text) => AddLine(text, progressColor, isProgress: true);
    public void LogError(string text) => AddLine(text, errorColor, isProgress: false);

    void AddLine(string text, Color color, bool isProgress)
    {
        if (string.IsNullOrEmpty(text)) return;
        string colored = $"<color=#{ColorUtility.ToHtmlStringRGB(color)}>{text}</color>";

        if (isProgress && _lastWasProgress && _lines.Count > 0)
            _lines[_lines.Count - 1] = colored;
        else
            _lines.Add(colored);

        _lastWasProgress = isProgress;

        while (_lines.Count > maxEntries)
            _lines.RemoveAt(0);

        RefreshText();
    }

    void RefreshText()
    {
        if (_logText == null) return;
        _logText.text = string.Join("\n", _lines);

        if (_scrollCoroutine != null) StopCoroutine(_scrollCoroutine);
        _scrollCoroutine = StartCoroutine(ScrollToBottomNextFrame());
    }

    IEnumerator ScrollToBottomNextFrame()
    {
        yield return null;
        Canvas.ForceUpdateCanvases();
        if (_scrollRect != null) _scrollRect.verticalNormalizedPosition = 0f;
    }

    void BuildUI()
    {
        var rootRT = (RectTransform)transform;

        var bgImg = gameObject.AddComponent<Image>();
        bgImg.sprite = RoundedRectSprite.Create();
        bgImg.type = Image.Type.Sliced;
        bgImg.color = panelColor;

        _scrollRect = gameObject.AddComponent<ScrollRect>();
        _scrollRect.horizontal = false;
        _scrollRect.vertical = true;
        _scrollRect.movementType = ScrollRect.MovementType.Clamped;
        _scrollRect.scrollSensitivity = 24f;

        var viewportGO = CreateUIObject("Viewport", rootRT);
        var viewportRT = (RectTransform)viewportGO.transform;
        StretchFull(viewportRT, 10f);
        viewportGO.AddComponent<RectMask2D>();

        var contentGO = CreateUIObject("Content", viewportRT);
        var contentRT = (RectTransform)contentGO.transform;
        contentRT.anchorMin = new Vector2(0f, 1f);
        contentRT.anchorMax = new Vector2(1f, 1f);
        contentRT.pivot = new Vector2(0.5f, 1f);
        contentRT.anchoredPosition = Vector2.zero;
        contentRT.sizeDelta = new Vector2(0f, 0f);

        _logText = contentGO.AddComponent<TextMeshProUGUI>();
        _logText.text = "";
        _logText.fontSize = fontSize;
        _logText.textWrappingMode = TextWrappingModes.Normal;
        _logText.richText = true;
        _logText.alignment = TextAlignmentOptions.TopLeft;
        _logText.color = Color.white;

        var outlineMat = _logText.fontMaterial;
        outlineMat.SetFloat(ShaderUtilities.ID_OutlineWidth, 0.18f);
        outlineMat.SetColor(ShaderUtilities.ID_OutlineColor, new Color(0f, 0f, 0f, 0.9f));
        _logText.fontMaterial = outlineMat;

        var fitter = contentGO.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

        _scrollRect.viewport = viewportRT;
        _scrollRect.content = contentRT;
    }

    GameObject CreateUIObject(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = parent.gameObject.layer;
        go.transform.SetParent(parent, false);
        return go;
    }

    void StretchFull(RectTransform rt, float padding = 0f)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(padding, padding);
        rt.offsetMax = new Vector2(-padding, -padding);
    }
}
