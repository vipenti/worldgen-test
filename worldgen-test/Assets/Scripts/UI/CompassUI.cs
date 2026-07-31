using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class CompassUI : MonoBehaviour
{
    [Header("Heading Source")]
    [Tooltip("Transform whose Y rotation (yaw) drives the compass. Should be the Player root, since that's what actually turns left/right.")]
    public Transform headingSource;

    [Header("Layout")]
    public float pixelsPerDegree = 6f;
    public float minorTickStepDeg = 15f;
    public float majorTickStepDeg = 90f;
    [Tooltip("Extra degrees built on both ends of the 0-360 strip so the viewport never runs out of ticks near the wrap point.")]
    public float marginDeg = 90f;

    [Header("Colors")]
    public Color panelColor = new Color(0.05f, 0.05f, 0.08f, 0.5f);
    public Color tickColor = new Color(0.85f, 0.85f, 0.9f, 0.85f);
    public Color accentColor = new Color(1f, 0.78f, 0.35f, 1f);

    RectTransform _content;
    float _viewportHalfWidthPx;

    void Awake()
    {
        BuildUI();
    }

    void Update()
    {
        if (headingSource == null || _content == null) return;

        float heading = Mathf.Repeat(headingSource.eulerAngles.y, 360f);
        float contentX = _viewportHalfWidthPx - (heading + marginDeg) * pixelsPerDegree;

        Vector2 pos = _content.anchoredPosition;
        pos.x = contentX;
        _content.anchoredPosition = pos;
    }

    void BuildUI()
    {
        var rootRT = (RectTransform)transform;

        var bgImg = gameObject.AddComponent<Image>();
        bgImg.sprite = RoundedRectSprite.Create();
        bgImg.type = Image.Type.Sliced;
        bgImg.color = panelColor;

        var viewportGO = CreateUIObject("Viewport", rootRT);
        var viewportRT = (RectTransform)viewportGO.transform;
        StretchFull(viewportRT, 6f);
        viewportGO.AddComponent<RectMask2D>();

        _viewportHalfWidthPx = viewportRT.rect.width * 0.5f;

        var contentGO = CreateUIObject("Content", viewportRT);
        _content = (RectTransform)contentGO.transform;
        _content.anchorMin = new Vector2(0f, 0.5f);
        _content.anchorMax = new Vector2(0f, 0.5f);
        _content.pivot = new Vector2(0f, 0.5f);

        float stripWidthPx = (360f + 2f * marginDeg) * pixelsPerDegree;
        _content.sizeDelta = new Vector2(stripWidthPx, viewportRT.rect.height);
        _content.anchoredPosition = Vector2.zero;

        for (float d = -marginDeg; d <= 360f + marginDeg + 0.01f; d += minorTickStepDeg)
        {
            float localX = (d + marginDeg) * pixelsPerDegree;
            float normDeg = ((d % 360f) + 360f) % 360f;
            bool isMajor = IsCloseToMultiple(normDeg, majorTickStepDeg);

            CreateTick(_content, localX, isMajor);

            if (isMajor)
            {
                string label = CardinalLabel(normDeg);
                if (!string.IsNullOrEmpty(label))
                    CreateLabel(_content, localX, label);
            }
        }

        var marker = CreateUIObject("CenterMarker", rootRT);
        var markerRT = (RectTransform)marker.transform;
        markerRT.anchorMin = markerRT.anchorMax = new Vector2(0.5f, 0.5f);
        markerRT.pivot = new Vector2(0.5f, 0.5f);
        markerRT.anchoredPosition = Vector2.zero;
        markerRT.sizeDelta = new Vector2(3f, rootRT.rect.height);
        var markerImg = marker.AddComponent<Image>();
        markerImg.color = accentColor;
    }

    void CreateTick(Transform parent, float localX, bool isMajor)
    {
        var tick = CreateUIObject(isMajor ? "MajorTick" : "MinorTick", parent);
        var tickRT = (RectTransform)tick.transform;
        tickRT.anchorMin = tickRT.anchorMax = new Vector2(0f, 0.5f);
        tickRT.pivot = new Vector2(0.5f, 0.5f);
        tickRT.anchoredPosition = new Vector2(localX, 0f);
        tickRT.sizeDelta = new Vector2(isMajor ? 3f : 1.5f, isMajor ? 26f : 14f);

        var img = tick.AddComponent<Image>();
        img.color = isMajor ? accentColor : tickColor;
    }

    void CreateLabel(Transform parent, float localX, string label)
    {
        var labelGO = CreateUIObject("Label", parent);
        var labelRT = (RectTransform)labelGO.transform;
        labelRT.anchorMin = labelRT.anchorMax = new Vector2(0f, 0.5f);
        labelRT.pivot = new Vector2(0.5f, 0.5f);
        labelRT.anchoredPosition = new Vector2(localX, 17f);
        labelRT.sizeDelta = new Vector2(40f, 20f);

        var tmp = labelGO.AddComponent<TextMeshProUGUI>();
        tmp.text = label;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.fontSize = 22f;
        tmp.fontStyle = FontStyles.Bold;
        tmp.color = accentColor;
    }

    static bool IsCloseToMultiple(float value, float step)
    {
        float remainder = value % step;
        return remainder < 0.01f || (step - remainder) < 0.01f;
    }

    static string CardinalLabel(float normDeg)
    {
        if (Mathf.Abs(normDeg - 0f) < 0.5f) return "N";
        if (Mathf.Abs(normDeg - 90f) < 0.5f) return "E";
        if (Mathf.Abs(normDeg - 180f) < 0.5f) return "S";
        if (Mathf.Abs(normDeg - 270f) < 0.5f) return "W";
        return "";
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
