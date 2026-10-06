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
        Assert.Equal(1, DiagnosticSettings.Default.SampleIntervalSeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void RejectsSampleIntervalsOutsideSupportedRange(int interval)
    {
        var settings = DiagnosticSettings.Default with { SampleIntervalSeconds = interval };

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
            SampleIntervalSeconds = 3,
            GameProfiles = [new GameMonitoringProfile("game", "Game", ["game.exe"], true)]
        };
        var json = JsonSerializer.Serialize(expected);

        var result = DiagnosticSettings.Parse(json, "en");

        Assert.Equal(expected with { GameProfiles = [] }, result.Settings with { GameProfiles = [] });
        var expectedProfile = Assert.Single(expected.GameProfiles);
        var actualProfile = Assert.Single(result.Settings.GameProfiles);
        Assert.Equal(expectedProfile.Id, actualProfile.Id);
        Assert.Equal(expectedProfile.Name, actualProfile.Name);
        Assert.Equal(expectedProfile.Enabled, actualProfile.Enabled);
        Assert.Equal(expectedProfile.ProcessNames, actualProfile.ProcessNames);
        Assert.False(result.NeedsSave);
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
        Assert.True(result.NeedsSave);
    }

    [Fact]
    public void InvalidJsonIsReportedAsJsonErrorForCallerToWrap()
    {
        Assert.Throws<JsonException>(() => DiagnosticSettings.Parse("{", "en"));
    }
}
