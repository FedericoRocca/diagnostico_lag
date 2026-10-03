namespace DiagnosticoLag;

internal sealed record StatSummary(
    int Samples,
    int Successful,
    int Lost,
    double Average,
    int Median,
    int P95,
    int P99,
    int Maximum,
    double Jitter,
    int Spikes80,
    int Spikes120,
    double LossPercent);

internal sealed class LatencyStatistics
{
    private readonly int[] _histogram = new int[1001];
    private int? _previous;
    private long _latencySum;
    private long _differenceSum;
    private int _differenceCount;
    private int _successCount;
    private int _lostCount;
    private int _maximum;
    private int _spikes80;
    private int _spikes120;

    public int Samples { get; private set; }

    public static StatSummary FromSamples(IEnumerable<int> samples)
    {
        var statistics = new LatencyStatistics();
        foreach (var sample in samples)
        {
            statistics.Add(sample);
        }

        return statistics.Snapshot();
    }

    public void Add(int milliseconds)
    {
        Samples++;
        if (milliseconds < 0)
        {
            _lostCount++;
            return;
        }

        _successCount++;
        _latencySum += milliseconds;
        _maximum = Math.Max(_maximum, milliseconds);
        if (_previous.HasValue)
        {
            _differenceSum += Math.Abs(milliseconds - _previous.Value);
            _differenceCount++;
        }

        _previous = milliseconds;
        _histogram[Math.Min(milliseconds, 1000)]++;
        if (milliseconds >= 120)
        {
            _spikes120++;
        }
        else if (milliseconds >= 80)
        {
            _spikes80++;
        }
    }

    public StatSummary Snapshot()
    {
        return new StatSummary(
            Samples,
            _successCount,
            _lostCount,
            _successCount == 0 ? 0 : Math.Round((double)_latencySum / _successCount, 1),
            Percentile(50),
            Percentile(95),
            Percentile(99),
            _maximum,
            _differenceCount == 0 ? 0 : Math.Round((double)_differenceSum / _differenceCount, 1),
            _spikes80,
            _spikes120,
            Samples == 0 ? 0 : Math.Round((double)_lostCount / Samples * 100, 1));
    }

    private int Percentile(int percentile)
    {
        if (_successCount == 0)
        {
            return 0;
        }

        var rank = Math.Max(1, (int)Math.Ceiling(_successCount * percentile / 100d));
        var count = 0;
        for (var i = 0; i < _histogram.Length; i++)
        {
            count += _histogram[i];
            if (count >= rank)
            {
                return i;
            }
        }

        return 1000;
    }
}
