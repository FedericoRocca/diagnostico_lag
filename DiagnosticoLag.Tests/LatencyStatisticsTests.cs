using DiagnosticoLag;

namespace DiagnosticoLag.Tests;

public sealed class LatencyStatisticsTests
{
    [Fact]
    public void EmptySamplesProduceZeroSummary()
    {
        var summary = LatencyStatistics.FromSamples([]);

        Assert.Equal(0, summary.Samples);
        Assert.Equal(0, summary.Successful);
        Assert.Equal(0, summary.Lost);
        Assert.Equal(0, summary.Average);
        Assert.Equal(0, summary.Median);
        Assert.Equal(0, summary.P95);
        Assert.Equal(0, summary.P99);
        Assert.Equal(0, summary.LossPercent);
    }

    [Fact]
    public void CalculatesPercentilesAverageAndMaximum()
    {
        var summary = LatencyStatistics.FromSamples([10, 20, 30, 40]);

        Assert.Equal(4, summary.Samples);
        Assert.Equal(4, summary.Successful);
        Assert.Equal(25, summary.Average);
        Assert.Equal(20, summary.Median);
        Assert.Equal(40, summary.P95);
        Assert.Equal(40, summary.P99);
        Assert.Equal(40, summary.Maximum);
    }

    [Fact]
    public void LossesCountTowardSamplesButNotLatencyStatistics()
    {
        var summary = LatencyStatistics.FromSamples([-1, 10, -1, 30]);

        Assert.Equal(4, summary.Samples);
        Assert.Equal(2, summary.Successful);
        Assert.Equal(2, summary.Lost);
        Assert.Equal(20, summary.Average);
        Assert.Equal(10, summary.Median);
        Assert.Equal(30, summary.P95);
        Assert.Equal(50, summary.LossPercent);
        Assert.Equal(20, summary.Jitter);
    }

    [Fact]
    public void ClassifiesSpikeBoundariesWithoutDoubleCounting()
    {
        var summary = LatencyStatistics.FromSamples([79, 80, 119, 120]);

        Assert.Equal(2, summary.Spikes80);
        Assert.Equal(1, summary.Spikes120);
    }

    [Fact]
    public void PercentilesClampHistogramButMaximumKeepsActualLatency()
    {
        var summary = LatencyStatistics.FromSamples([10, 1500]);

        Assert.Equal(1000, summary.P95);
        Assert.Equal(1500, summary.Maximum);
    }
}
