using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;

/// <summary>
/// Campiona FPS e % di RAM di processo a intervalli regolari, raggruppati in "segmenti"
/// (un segmento = il tempo in cui un certo bioma è quello visivamente attivo sotto i piedi del
/// player). Usato da BenchmarkRunner: BeginSegment() quando un bioma diventa attivo, EndSegment()
/// quando arriva il successivo (o a fine run per l'ultimo).
/// </summary>
public class BenchmarkMetricsLogger : MonoBehaviour
{
    [Tooltip("Intervallo di campionamento in secondi. 0 = ogni frame (più preciso per l'1% low, ma più campioni da tenere in memoria).")]
    public float sampleInterval = 0.5f;

    public struct SegmentStats
    {
        public string label;
        public bool structured;
        public int sampleCount;
        public float avgFps;
        public float minFps;
        public float fps1PercentLow;
        public float avgRamPercent;
        public float maxRamPercent;
        // MB assoluti del working set del processo — indipendenti da SystemInfo.systemMemorySize,
        // che su alcune configurazioni può restituire 0 (RAM totale non disponibile) e far collassare
        // avgRamPercent/maxRamPercent a 0 anche quando il processo usa RAM per davvero. Vedi Awake().
        public float avgRamMB;
        public float maxRamMB;
    }

    readonly List<float> _fpsSamples = new List<float>();
    readonly List<float> _ramMbSamples = new List<float>();
    float _timer;
    bool _sampling;
    string _currentLabel;
    bool _currentStructured;
    float _totalSystemMB;

    void Awake()
    {
        _totalSystemMB = SystemInfo.systemMemorySize;

        // NOTA STORICA: qui prima leggevamo System.Diagnostics.Process.GetCurrentProcess().WorkingSet64
        // per la RAM di processo — funziona in Editor (Mono) ma su una build IL2CPP restituisce
        // silenziosamente 0 in ogni frame invece di lanciare un errore (limite noto: le API di
        // introspezione del processo del BCL .NET non sono pienamente supportate sotto IL2CPP),
        // quindi ogni report da build finiva con RAM sempre a 0 MB / 0%. Profiler.GetTotalAllocatedMemoryLong()
        // è l'API nativa di Unity per lo stesso scopo (la usa anche la finestra Profiler) e funziona
        // identica in Editor e in build a prescindere dal backend di scripting.
        Debug.Log($"[BenchmarkMetricsLogger] System RAM: {_totalSystemMB:F0} MB. Initial allocated memory: {Profiler.GetTotalAllocatedMemoryLong() / 1024f / 1024f:F1} MB.");
        if (_totalSystemMB <= 0f)
            Debug.LogWarning("[BenchmarkMetricsLogger] SystemInfo.systemMemorySize returned an invalid value — RAM % will read 0.0 for the whole run; use avgRamMB/maxRamMB in the report instead.");
    }

    public void BeginSegment(string label, bool structured)
    {
        _currentLabel = label;
        _currentStructured = structured;
        _fpsSamples.Clear();
        _ramMbSamples.Clear();
        _timer = 0f;
        _sampling = true;
    }

    public SegmentStats EndSegment()
    {
        _sampling = false;
        return ComputeStats();
    }

    SegmentStats ComputeStats()
    {
        var stats = new SegmentStats
        {
            label = _currentLabel,
            structured = _currentStructured,
            sampleCount = _fpsSamples.Count
        };
        if (_fpsSamples.Count == 0) return stats;

        float sum = 0f, min = float.MaxValue;
        foreach (float f in _fpsSamples)
        {
            sum += f;
            if (f < min) min = f;
        }
        stats.avgFps = sum / _fpsSamples.Count;
        stats.minFps = min;

        // 1% low: media del peggior 1% dei campioni (metrica standard per gli stutter, molto più
        // informativa della sola media quando il rendering ha picchi di lag durante lo streaming).
        var sorted = new List<float>(_fpsSamples);
        sorted.Sort();
        int lowCount = Mathf.Max(1, Mathf.CeilToInt(sorted.Count * 0.01f));
        float lowSum = 0f;
        for (int i = 0; i < lowCount; i++) lowSum += sorted[i];
        stats.fps1PercentLow = lowSum / lowCount;

        float mbSum = 0f, mbMax = 0f;
        foreach (float mb in _ramMbSamples)
        {
            mbSum += mb;
            if (mb > mbMax) mbMax = mb;
        }
        float avgMb = mbSum / _ramMbSamples.Count;
        stats.avgRamMB = avgMb;
        stats.maxRamMB = mbMax;
        stats.avgRamPercent = _totalSystemMB > 0f ? avgMb / _totalSystemMB * 100f : 0f;
        stats.maxRamPercent = _totalSystemMB > 0f ? mbMax / _totalSystemMB * 100f : 0f;

        return stats;
    }

    void Update()
    {
        if (!_sampling) return;

        _timer += Time.unscaledDeltaTime;
        if (sampleInterval > 0f && _timer < sampleInterval) return;
        _timer = 0f;

        float fps = Time.unscaledDeltaTime > 0f ? 1f / Time.unscaledDeltaTime : 0f;
        _fpsSamples.Add(fps);

        // Memoria totale allocata da Unity (managed + subsystem nativi) — vedi nota in Awake() sul
        // perché non usiamo più System.Diagnostics.Process qui.
        float allocatedMB = Profiler.GetTotalAllocatedMemoryLong() / 1024f / 1024f;
        _ramMbSamples.Add(allocatedMB);
    }
}
