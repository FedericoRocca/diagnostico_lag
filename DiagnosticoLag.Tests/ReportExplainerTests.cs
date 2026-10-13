using DiagnosticoLag;

namespace DiagnosticoLag.Tests;

public sealed class ReportExplainerTests
{
    private static readonly ProbeTarget Router = new("router", "Router", "192.168.0.1", ProbeType.Icmp);
    private static readonly ProbeTarget Web = new("google-icmp", "Google", "8.8.8.8", ProbeType.Icmp);
    private static readonly ProbeTarget WebTcp = new("google-tcp", "Google TCP", "8.8.8.8", ProbeType.Tcp);

    private static StatSummary Stats(int samples = 100, int lost = 0, double average = 20, double jitter = 2, int spikes80 = 0, int spikes120 = 0, double loss = 0) =>
        new(samples, samples - lost, lost, average, (int)average, (int)average + 5, (int)average + 10, (int)average + 20, jitter, spikes80, spikes120, loss);

    [Fact]
    public void Rate_WithFewSamples_IsInsufficient() =>
        Assert.Equal(QualityLevel.Insufficient, ReportExplainer.Rate(Web, Stats(samples: 10)));

    [Fact]
    public void Rate_WithLowLatency_IsGood() =>
        Assert.Equal(QualityLevel.Good, ReportExplainer.Rate(Web, Stats(average: 20)));

    [Fact]
    public void Rate_RouterIsStricterThanInternetTargets()
    {
        var stats = Stats(average: 40);
        Assert.Equal(QualityLevel.Regular, ReportExplainer.Rate(Router, stats));
        Assert.Equal(QualityLevel.Good, ReportExplainer.Rate(Web, stats));
    }

    [Fact]
    public void Rate_TcpToleratesMoreLatencyThanIcmp()
    {
        var stats = Stats(average: 110);
        Assert.Equal(QualityLevel.Regular, ReportExplainer.Rate(Web, stats));
        Assert.Equal(QualityLevel.Acceptable, ReportExplainer.Rate(WebTcp, stats));
    }

    [Fact]
    public void Rate_WithHighLoss_IsBad() =>
        Assert.Equal(QualityLevel.Bad, ReportExplainer.Rate(Web, Stats(lost: 5, loss: 5)));

    [Fact]
    public void DescribeTarget_WithoutLoss_ExplainsInPlainLanguage()
    {
        Localization.SetLanguage("es");
        var text = string.Join("\n", ReportExplainer.DescribeTarget(Web, Stats()));
        Assert.Contains("No se perdió ninguna respuesta", text);
        Assert.Contains("Qué significa", text);
    }

    [Fact]
    public void DescribeTarget_WithFewSamples_ExplainsMissingData()
    {
        Localization.SetLanguage("es");
        var text = string.Join("\n", ReportExplainer.DescribeTarget(Web, Stats(samples: 5)));
        Assert.Contains("Solo hay 5 muestras", text);
    }

    [Fact]
    public void Group_SplitsIncidentsByTimeGap()
    {
        var t = new DateTime(2026, 1, 1, 20, 0, 0);
        var events = new[]
        {
            new LagEvent(t, "google-icmp", "Google", -1),
            new LagEvent(t.AddSeconds(3), "google-icmp", "Google", 150),
            new LagEvent(t.AddSeconds(60), "router", "Router", -1)
        };
        var incidents = LagIncident.Group(events);
        Assert.Equal(2, incidents.Count);
        Assert.Equal(1, incidents[0].Losses);
        Assert.Equal(1, incidents[0].Spikes);
        Assert.Equal(150, incidents[0].Peak);
    }

    [Fact]
    public void Group_WithoutEvents_IsEmpty() =>
        Assert.Empty(LagIncident.Group(null));

    [Fact]
    public void Describe_RouterIncident_PointsToLocalNetwork()
    {
        Localization.SetLanguage("es");
        var t = new DateTime(2026, 1, 1, 20, 0, 0);
        var incident = LagIncident.Group([new LagEvent(t, "router", "Router", -1)])[0];
        Assert.Contains("red local", incident.Describe());
    }

    [Fact]
    public void Describe_SingleInternetTarget_IsNotBlamedOnConnection()
    {
        Localization.SetLanguage("es");
        var t = new DateTime(2026, 1, 1, 20, 0, 0);
        var incident = LagIncident.Group([new LagEvent(t, "google-icmp", "Google", 200)])[0];
        Assert.Contains("solo afectó a Google", incident.Describe());
    }

    [Fact]
    public void Describe_MultipleTargets_PointsToGeneralProblem()
    {
        Localization.SetLanguage("es");
        var t = new DateTime(2026, 1, 1, 20, 0, 0);
        var incident = LagIncident.Group([new LagEvent(t, "google-icmp", "Google", 200), new LagEvent(t, "cloudflare-icmp", "Cloudflare", 200)])[0];
        Assert.Contains("varios destinos", incident.Describe());
    }

    private static MonitorSnapshot Snapshot(params (ProbeTarget Target, StatSummary Stats)[] targets) =>
        new(DateTime.Now, TimeSpan.FromMinutes(10),
            targets.Select(item => new TargetSnapshot(item.Target, item.Stats)).ToArray(),
            new Dictionary<string, int>(), [], new ConnectionInfo("Ethernet", "test", null), "192.168.0.1", null, [], [],
            new DiagnosticResult("OK", "t", "e", "", []), 0);

    [Fact]
    public void Verdict_WithoutEnoughData_IsMeasuring() =>
        Assert.Equal(VerdictLevel.Measuring, SimpleVerdict.From(Snapshot((Web, Stats(samples: 5)))).Level);

    [Fact]
    public void Verdict_GoodTargets_IsGood() =>
        Assert.Equal(VerdictLevel.Good, SimpleVerdict.From(Snapshot((Router, Stats(average: 3)), (Web, Stats()))).Level);

    [Fact]
    public void Verdict_RouterLoss_IsBadAndPointsToLocalNetwork()
    {
        Localization.SetLanguage("es");
        var verdict = SimpleVerdict.From(Snapshot((Router, Stats(lost: 8, loss: 8)), (Web, Stats())));
        Assert.Equal(VerdictLevel.Bad, verdict.Level);
        Assert.Contains("red local", verdict.Detail);
    }

    [Fact]
    public void Verdict_Unstable_WhenOnlyRegular() =>
        Assert.Equal(VerdictLevel.Unstable, SimpleVerdict.From(Snapshot((Web, Stats(average: 110)))).Level);

    [Fact]
    public void Build_IncludesTimelineAndSummary()
    {
        Localization.SetLanguage("es");
        var t = new DateTime(2026, 1, 1, 20, 0, 0);
        var snapshot = Snapshot((Web, Stats())) with { Events = [new LagEvent(t, "google-icmp", "Google", -1)] };
        var text = string.Join("\n", ReportExplainer.Build(snapshot, new ConnectionHealth(100, "Salud buena")));
        Assert.Contains("RESUMEN RÁPIDO", text);
        Assert.Contains("4) LÍNEA DE TIEMPO DE INCIDENTES", text);
        Assert.Contains("5) CONCLUSIÓN Y QUÉ HACER", text);
        Assert.Contains("[20:00:00]", text);
    }

    [Fact]
    public void DescribeTarget_InEnglish_IsTranslated()
    {
        try
        {
            Localization.SetLanguage("en");
            var text = string.Join("\n", ReportExplainer.DescribeTarget(Router, Stats(average: 60, lost: 5, loss: 5)));
            Assert.Contains("Poor", text);
            Assert.Contains("What it means", text);
            Assert.DoesNotContain("Qué significa", text);
        }
        finally
        {
            Localization.SetLanguage("es");
        }
    }
}
