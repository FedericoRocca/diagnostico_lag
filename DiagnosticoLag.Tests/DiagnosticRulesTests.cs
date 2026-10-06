using DiagnosticoLag;

namespace DiagnosticoLag.Tests;

public sealed class DiagnosticRulesTests
{
    [Fact]
    public void DoesNotFlagFrequentSpikesBelowRuleThresholds()
    {
        var belowThreshold = Summary(samples: 100, spikes80: 4);

        Assert.False(DiagnosticSession.HasFrequentSpikes(belowThreshold));
    }

    [Fact]
    public void FlagsRepeatedHighLatencySpikesAtRuleThreshold()
    {
        var repeatedHighSpikes = Summary(samples: 100, spikes120: 3);

        Assert.True(DiagnosticSession.HasFrequentSpikes(repeatedHighSpikes));
    }

    [Fact]
    public void FlagsFrequentModerateSpikesAtRuleThreshold()
    {
        var frequentModerateSpikes = Summary(samples: 100, spikes80: 5);

        Assert.True(DiagnosticSession.HasFrequentSpikes(frequentModerateSpikes));
    }

    [Fact]
    public void IdentifiesTcpProblemsWhenIcmpIsHealthy()
    {
        var problems = new List<DiagnosticFinding>();
        var warnings = new List<DiagnosticFinding>();

        DiagnosticSession.AddInternetDiagnosis(
            "Destino 1",
            "icmp",
            "tcp",
            Values(Summary(30), Summary(30, average: 151)),
            problems,
            warnings);

        Assert.Single(problems);
        Assert.Contains("TCP/443", problems[0].Text);
        Assert.False(string.IsNullOrWhiteSpace(problems[0].Explanation));
        Assert.Empty(warnings);
    }

    [Fact]
    public void TreatsIcmpOnlyProblemsAsWarningsWhenTcpIsHealthy()
    {
        var problems = new List<DiagnosticFinding>();
        var warnings = new List<DiagnosticFinding>();

        DiagnosticSession.AddInternetDiagnosis(
            "Destino 1",
            "icmp",
            "tcp",
            Values(Summary(30, average: 101), Summary(30)),
            problems,
            warnings);

        Assert.Empty(problems);
        Assert.Single(warnings);
        Assert.Contains("filtrado", warnings[0].Text, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(warnings[0].Explanation));
    }

    [Fact]
    public void ReportsBothProtocolsWhenBothHaveProblems()
    {
        var problems = new List<DiagnosticFinding>();
        var warnings = new List<DiagnosticFinding>();

        DiagnosticSession.AddInternetDiagnosis(
            "Destino 1",
            "icmp",
            "tcp",
            Values(Summary(30, lossPercent: 4), Summary(30, lossPercent: 4)),
            problems,
            warnings);

        Assert.Single(problems);
        Assert.Contains("ICMP", problems[0].Text);
        Assert.Contains("TCP/443", problems[0].Text);
        Assert.Empty(warnings);
    }

    [Fact]
    public void IgnoresInternetComparisonsUntilBothTargetsHaveThirtySamples()
    {
        var problems = new List<DiagnosticFinding>();
        var warnings = new List<DiagnosticFinding>();

        DiagnosticSession.AddInternetDiagnosis(
            "Destino 1",
            "icmp",
            "tcp",
            Values(Summary(29, average: 500), Summary(30, average: 500)),
            problems,
            warnings);

        Assert.Empty(problems);
        Assert.Empty(warnings);
    }

    [Fact]
    public void AddsWarningForIsolatedLargeIcmpSpikeEvenWhenAverageIsHealthy()
    {
        var problems = new List<DiagnosticFinding>();
        var warnings = new List<DiagnosticFinding>();

        DiagnosticSession.AddInternetDiagnosis(
            "Destino 1",
            "icmp",
            "tcp",
            Values(Summary(30, maximum: 200), Summary(30)),
            problems,
            warnings);

        Assert.Empty(problems);
        Assert.Single(warnings);
        Assert.Contains("pico aislado", warnings[0].Text);
    }

    [Fact]
    public void ReportsExpectedIcmpFilteringForLeagueOfLegends()
    {
        var problems = new List<DiagnosticFinding>();
        var warnings = new List<DiagnosticFinding>();
        Localization.SetLanguage("es");

        DiagnosticSession.AddInternetDiagnosis(
            "LoL",
            "icmp",
            "tcp",
            Values(Summary(30, lossPercent: 100), Summary(30)),
            problems,
            warnings);

        Assert.Empty(problems);
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, warning => warning.Text.Contains("no responde a ICMP", StringComparison.Ordinal));
        Assert.All(warnings, warning => Assert.False(string.IsNullOrWhiteSpace(warning.Explanation)));
    }

    private static IReadOnlyDictionary<string, StatSummary> Values(StatSummary icmp, StatSummary tcp) =>
        new Dictionary<string, StatSummary>
        {
            ["icmp"] = icmp,
            ["tcp"] = tcp
        };

    private static StatSummary Summary(
        int samples,
        double lossPercent = 0,
        double average = 10,
        double jitter = 0,
        int maximum = 10,
        int spikes80 = 0,
        int spikes120 = 0)
    {
        var lost = (int)Math.Round(samples * lossPercent / 100);
        return new StatSummary(samples, samples - lost, lost, average, 10, 10, 10, maximum, jitter,
            spikes80, spikes120, lossPercent);
    }
}
