using System.Text.Json;
using DiagnosticoLag;

namespace DiagnosticoLag.Tests;

public sealed class DiagnosticSettingsTests
{
    [Fact]
    public void DefaultSettingsAreValidAndUseExpectedDestinations()
    {
        var exception = Record.Exception(() => DiagnosticSettings.Validate(DiagnosticSettings.Default));

        Assert.Null(exception);
        Assert.Equal("1.1.1.1", DiagnosticSettings.Default.CloudflareAddress);
        Assert.Equal("8.8.8.8", DiagnosticSettings.Default.GoogleAddress);
        Assert.Equal(1000, DiagnosticSettings.Default.SampleIntervalMilliseconds);
    }

    [Theory]
    [InlineData(49)]
    [InlineData(10001)]
    public void RejectsSampleIntervalsOutsideSupportedRange(int interval)
    {
        var settings = DiagnosticSettings.Default with { SampleIntervalMilliseconds = interval };

        Assert.Throws<InvalidDataException>(() => DiagnosticSettings.Validate(settings));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a host name !")]
    public void RejectsInvalidProbeHosts(string host)
    {
        var settings = DiagnosticSettings.Default with { CloudflareAddress = host };

        Assert.Throws<InvalidDataException>(() => DiagnosticSettings.Validate(settings));
    }

    [Fact]
    public void RejectsRelativeOutputDirectories()
    {
        var settings = DiagnosticSettings.Default with { LogDirectory = "relative\\logs" };

        Assert.Throws<InvalidDataException>(() => DiagnosticSettings.Validate(settings));
    }

    [Fact]
    public void RejectsDuplicateProfileIdentifiersIgnoringCase()
    {
        var profiles = new[]
        {
            new GameMonitoringProfile("game", "Game A", ["a"], true),
            new GameMonitoringProfile("GAME", "Game B", ["b"], false)
        };

        var settings = DiagnosticSettings.Default with { GameProfiles = profiles };

        Assert.Throws<InvalidDataException>(() => DiagnosticSettings.Validate(settings));
    }

    [Fact]
    public void RejectsProfilesWithNoProcessNames()
    {
        var profile = new GameMonitoringProfile("game", "Game", [], true);
        var settings = DiagnosticSettings.Default with { GameProfiles = [profile] };

        Assert.Throws<InvalidDataException>(() => DiagnosticSettings.Validate(settings));
    }

    [Fact]
    public void ParsesAndValidatesCurrentSettingsWithoutWritingToDisk()
    {
        var expected = DiagnosticSettings.Default with
        {
            Language = "es",
            SampleIntervalMilliseconds = 3000,
            GameProfiles = [new GameMonitoringProfile("game", "Game", ["game.exe"], true)]
        };
        var json = JsonSerializer.Serialize(expected);

        var result = DiagnosticSettings.Parse(json, "en");

        Assert.Equal(expected with { GameProfiles = [], ChartVisibility = result.Settings.ChartVisibility },
            result.Settings with { GameProfiles = [] });
        var expectedProfile = Assert.Single(expected.GameProfiles);
        var actualProfile = Assert.Single(result.Settings.GameProfiles);
        Assert.Equal(expectedProfile.Id, actualProfile.Id);
        Assert.Equal(expectedProfile.Name, actualProfile.Name);
        Assert.Equal(expectedProfile.Enabled, actualProfile.Enabled);
        Assert.Equal(expectedProfile.ProcessNames, actualProfile.ProcessNames);
        Assert.False(result.NeedsSave);
    }

    [Fact]
    public void PersistsChartVisibilityPreferences()
    {
        var expected = DiagnosticSettings.Default with
        {
            ChartVisibility = new Dictionary<string, bool>
            {
                ["router"] = true,
                ["game:league-of-legends:203.0.113.42:443"] = false
            }
        };

        var result = DiagnosticSettings.Parse(JsonSerializer.Serialize(expected), "en");

        Assert.Equal(expected.ChartVisibility, result.Settings.ChartVisibility);
        Assert.False(result.NeedsSave);
    }

    [Fact]
    public void MigratesSettingsWithoutChartVisibility()
    {
        const string json = """
            {
              "CloudflareAddress": "1.1.1.1",
              "GoogleAddress": "8.8.8.8",
              "SampleIntervalSeconds": 1,
              "NetworkInterfaceId": null,
              "Language": "en",
              "GameProfiles": []
            }
            """;

        var result = DiagnosticSettings.Parse(json, "en");

        Assert.Empty(result.Settings.ChartVisibility);
        Assert.Equal(1000, result.Settings.SampleIntervalMilliseconds);
        Assert.True(result.NeedsSave);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void MigratesLegacyGameFlagsToSingleProfile(bool detectLeague, bool includeInDiagnosis, bool enabled)
    {
        var json = $$"""
            {
              "CloudflareAddress": "1.1.1.1",
              "GoogleAddress": "8.8.8.8",
              "SampleIntervalSeconds": 2,
              "NetworkInterfaceId": null,
              "DetectLeague": {{detectLeague.ToString().ToLowerInvariant()}},
              "IncludeLeagueInDiagnosis": {{includeInDiagnosis.ToString().ToLowerInvariant()}}
            }
            """;

        var result = DiagnosticSettings.Parse(json, "es");

        var profile = Assert.Single(result.Settings.GameProfiles);
        Assert.Equal(GameMonitoringProfile.LeagueOfLegends.Id, profile.Id);
        Assert.Equal(enabled, profile.Enabled);
        Assert.Equal(2000, result.Settings.SampleIntervalMilliseconds);
        Assert.True(result.NeedsSave);
    }

    [Fact]
    public void AcceptsShortSamplingIntervalsThatWarnInTheSettingsUi()
    {
        var settings = DiagnosticSettings.Default with { SampleIntervalMilliseconds = 500 };

        var exception = Record.Exception(() => DiagnosticSettings.Validate(settings));

        Assert.Null(exception);
    }

    [Fact]
    public void InvalidJsonIsReportedAsJsonErrorForCallerToWrap()
    {
        Assert.Throws<JsonException>(() => DiagnosticSettings.Parse("{", "en"));
    }

    [Fact]
    public void SaveWritesAtomicallyAndRoundTrips()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lagnostics-tests-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "configuracion.json");
        try
        {
            var first = DiagnosticSettings.Default with { Language = "es", SampleIntervalMilliseconds = 3000 };
            first.SaveTo(path);
            var second = first with { SampleIntervalMilliseconds = 5000 };
            second.SaveTo(path);

            var loaded = DiagnosticSettings.Parse(File.ReadAllText(path), "en").Settings;
            Assert.Equal(5000, loaded.SampleIntervalMilliseconds);
            Assert.Equal("es", loaded.Language);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void SaveRejectsInvalidSettingsWithoutTouchingExistingFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lagnostics-tests-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "configuracion.json");
        try
        {
            DiagnosticSettings.Default.SaveTo(path);
            var original = File.ReadAllText(path);

            Assert.Throws<InvalidDataException>(() =>
                (DiagnosticSettings.Default with { SampleIntervalMilliseconds = 10001 }).SaveTo(path));

            Assert.Equal(original, File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
