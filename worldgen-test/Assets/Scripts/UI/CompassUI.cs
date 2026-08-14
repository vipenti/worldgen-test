using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;

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

    [Header("Points of Interest")]
    [Tooltip("Colore del marker per gli oggetti 'unique' del bioma (edifici landmark, oggetti rari one-off...).")]
    public Color poiColor = new Color(1f, 0.85f, 0.2f, 1f);
    [Tooltip("Distanza massima (world units) entro cui un punto di interesse resta visibile sulla bussola. Oltre, sparisce — evita che landmark di biomi vecchi/lontani restino segnati per sempre.")]
    public float poiMaxDistance = 400f;

    RectTransform _content;
    float _viewportHalfWidthPx;

    // Posizione mondo nota di ogni oggetto "unique" già piazzato (nome → posizione) e il marker UI
    // corrispondente, entrambi popolati/svuotati dagli eventi statici di WorldManager — vedi
    // HandlePOISpawned/HandlePOICleared. Un dizionario per nome (non una lista) perché lo stesso
    // nome unique non viene mai piazzato due volte (WorldManager lo garantisce già).
    readonly Dictionary<string, Vector3> _pois = new Dictionary<string, Vector3>();
    readonly Dictionary<string, RectTransform> _poiMarkers = new Dictionary<string, RectTransform>();

    void Awake()
    {
        BuildUI();
    }

    void OnEnable()
    {
        WorldManager.OnUniqueObjectSpawned += HandlePOISpawned;
        WorldManager.OnUniqueObjectsCleared += HandlePOICleared;
    }

    void OnDisable()
    {
        WorldManager.OnUniqueObjectSpawned -= HandlePOISpawned;
        WorldManager.OnUniqueObjectsCleared -= HandlePOICleared;
    }

    void Update()
    {
        if (headingSource == null || _content == null) return;

        float heading = Mathf.Repeat(headingSource.eulerAngles.y, 360f);
        float contentX = _viewportHalfWidthPx - (heading + marginDeg) * pixelsPerDegree;

        Vector2 pos = _content.anchoredPosition;
        pos.x = contentX;
        _content.anchoredPosition = pos;

        UpdatePOIMarkers(heading);
    }

    // Un marker per punto di interesse: posizione lungo la striscia calcolata dal bearing
    // giocatore→oggetto (non dalla sola rotazione, a differenza dei tick cardinali fissi), nascosto
    // oltre poiMaxDistance. Usiamo sempre la rappresentazione del bearing più vicina all'heading
    // corrente (±180°) — stessa logica implicita dei tick, che esistono anche oltre 0-360 grazie a
    // marginDeg proprio per coprire questo caso vicino al punto di wrap.
    void UpdatePOIMarkers(float heading)
    {
        if (_pois.Count == 0) return;

        Vector3 origin = headingSource.position;
        foreach (var kvp in _pois)
        {
            if (!_poiMarkers.TryGetValue(kvp.Key, out var markerRT) || markerRT == null) continue;

            Vector3 delta = kvp.Value - origin;
            delta.y = 0f;
            float dist = delta.magnitude;
            bool visible = dist > 0.01f && dist <= poiMaxDistance;

            if (markerRT.gameObject.activeSelf != visible)
                markerRT.gameObject.SetActive(visible);
            if (!visible) continue;

            float bearing = Mathf.Repeat(Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg, 360f);
            float d = ClosestWrappedDeg(bearing, heading);
            float localX = (d + marginDeg) * pixelsPerDegree;

            Vector2 mp = markerRT.anchoredPosition;
            mp.x = localX;
            markerRT.anchoredPosition = mp;
        }
    }

    // Restituisce targetDeg (o un suo alias ±360) più vicino a referenceDeg — così un oggetto
    // proprio dietro l'angolo di wrap (es. bearing=350° con heading=10°) appare a -10° invece che
    // dover attraversare tutta la striscia fino a 350°.
    static float ClosestWrappedDeg(float targetDeg, float referenceDeg)
    {
        float d = targetDeg;
        while (d - referenceDeg > 180f) d -= 360f;
        while (d - referenceDeg < -180f) d += 360f;
        return d;
    }

    void HandlePOISpawned(string name, Vector3 worldPos)
    {
        _pois[name] = worldPos;
        if (!_poiMarkers.ContainsKey(name))
            _poiMarkers[name] = CreatePOIMarker(name);
    }

    void HandlePOICleared()
    {
        foreach (var kvp in _poiMarkers)
            if (kvp.Value != null) Destroy(kvp.Value.gameObject);
        _poiMarkers.Clear();
        _pois.Clear();
    }

    RectTransform CreatePOIMarker(string label)
    {
        // markerRT è il nodo la cui anchoredPosition.x viene aggiornata ogni frame (vedi
        // UpdatePOIMarkers) — resta NON ruotato apposta, così la label figlia non eredita la
        // rotazione a 45° del solo diamante (un figlio separato, ruotato per conto suo).
        var markerGO = CreateUIObject($"POI_{label}", _content);
        var markerRT = (RectTransform)markerGO.transform;
        markerRT.anchorMin = markerRT.anchorMax = new Vector2(0f, 0.5f);
        markerRT.pivot = new Vector2(0.5f, 0.5f);
        markerRT.sizeDelta = new Vector2(12f, 12f);

        var diamondGO = CreateUIObject("Diamond", markerRT);
        var diamondRT = (RectTransform)diamondGO.transform;
        diamondRT.anchorMin = diamondRT.anchorMax = new Vector2(0.5f, 0.5f);
        diamondRT.pivot = new Vector2(0.5f, 0.5f);
        diamondRT.anchoredPosition = Vector2.zero;
        diamondRT.sizeDelta = new Vector2(10f, 10f);
        diamondRT.localRotation = Quaternion.Euler(0f, 0f, 45f); // diamante, si distingue dai tick rettangolari

        var img = diamondGO.AddComponent<Image>();
        img.color = poiColor;

        CreateLabel(markerRT, 0f, label, poiColor, yOffset: -20f, width: 140f);

        markerGO.SetActive(false); // UpdatePOIMarkers lo riattiva appena entra in poiMaxDistance
        return markerRT;
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

    void CreateLabel(Transform parent, float localX, string label, Color? color = null, float yOffset = 17f, float width = 40f)
    {
        var labelGO = CreateUIObject("Label", parent);
        var labelRT = (RectTransform)labelGO.transform;
        labelRT.anchorMin = labelRT.anchorMax = new Vector2(0f, 0.5f);
        labelRT.pivot = new Vector2(0.5f, 0.5f);
        labelRT.anchoredPosition = new Vector2(localX, yOffset);
        labelRT.sizeDelta = new Vector2(width, 20f);

        var tmp = labelGO.AddComponent<TextMeshProUGUI>();
        tmp.text = label;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.fontSize = 22f;
        tmp.fontStyle = FontStyles.Bold;
        tmp.color = color ?? accentColor;
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
