using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

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
    }

    readonly List<float> _fpsSamples = new List<float>();
    readonly List<float> _ramSamples = new List<float>();
    float _timer;
    bool _sampling;
    string _currentLabel;
    bool _currentStructured;
    Process _process;

    void Awake() => _process = Process.GetCurrentProcess();

    public void BeginSegment(string label, bool structured)
    {
        _currentLabel = label;
        _currentStructured = structured;
        _fpsSamples.Clear();
        _ramSamples.Clear();
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

        float ramSum = 0f, ramMax = 0f;
        foreach (float r in _ramSamples)
        {
            ramSum += r;
            if (r > ramMax) ramMax = r;
        }
        stats.avgRamPercent = ramSum / _ramSamples.Count;
        stats.maxRamPercent = ramMax;

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

        // % di RAM FISICA usata dal processo Unity sul totale disponibile sulla macchina — più
        // leggibile per il paper di un valore assoluto in MB, e confrontabile fra hardware diversi.
        _process.Refresh();
        long workingSetBytes = _process.WorkingSet64;
        float totalSystemBytes = SystemInfo.systemMemorySize * 1024f * 1024f;
        float ramPercent = totalSystemBytes > 0f ? workingSetBytes / totalSystemBytes * 100f : 0f;
        _ramSamples.Add(ramPercent);
    }
}
