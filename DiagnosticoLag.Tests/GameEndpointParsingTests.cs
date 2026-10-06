using System.Net;
using DiagnosticoLag;

namespace DiagnosticoLag.Tests;

public sealed class GameEndpointParsingTests
{
    private static readonly GameMonitoringProfile Profile =
        new("sample-game", "Sample Game", ["sample-game.exe"], true);

    [Fact]
    public void ParsesEstablishedPublicIpv4EndpointForConfiguredProcess()
    {
        const string netstat = "  TCP    10.0.0.2:53000    93.184.216.34:443    ESTABLISHED    42";
        var result = DiagnosticSession.ParseGameEndpoints(netstat, new Dictionary<int, string> { [42] = "sample-game" }, [Profile]);

        var endpoint = Assert.Single(result);
        Assert.Equal("sample-game", endpoint.ProfileId);
        Assert.Equal("93.184.216.34", endpoint.Address);
        Assert.Equal(443, endpoint.Port);
        Assert.Equal("sample-game", endpoint.ProcessName);
    }

    [Fact]
    public void ParsesBracketedPublicIpv6Endpoint()
    {
        const string netstat = "  TCP    [2001:db8::1]:53000    [2606:4700:4700::1111]:443    ESTABLISHED    42";
        var result = DiagnosticSession.ParseGameEndpoints(netstat, new Dictionary<int, string> { [42] = "SAMPLE-GAME" }, [Profile]);

        var endpoint = Assert.Single(result);
        Assert.Equal(IPAddress.Parse("2606:4700:4700::1111").ToString(), endpoint.Address);
        Assert.Equal(443, endpoint.Port);
    }

    [Fact]
    public void IgnoresUnknownProcessesPrivateAddressesAndNonEstablishedConnections()
    {
        const string netstat = """
            TCP    10.0.0.2:53000    93.184.216.34:443    ESTABLISHED    99
            TCP    10.0.0.2:53001    192.168.1.20:443    ESTABLISHED    42
            TCP    10.0.0.2:53002    93.184.216.35:443    TIME_WAIT    42
            UDP    10.0.0.2:53003    93.184.216.36:443    ESTABLISHED    42
            """;

        var result = DiagnosticSession.ParseGameEndpoints(netstat, new Dictionary<int, string> { [42] = "sample-game" }, [Profile]);

        Assert.Empty(result);
    }

    [Fact]
    public void LimitsActiveEndpointsPerProfileAndDeduplicatesRepeatedEndpoints()
    {
        const string netstat = """
            TCP    10.0.0.2:53000    93.184.216.34:443    ESTABLISHED    42
            TCP    10.0.0.2:53001    93.184.216.34:443    ESTABLISHED    42
            TCP    10.0.0.2:53002    93.184.216.35:443    ESTABLISHED    42
            TCP    10.0.0.2:53003    93.184.216.36:443    ESTABLISHED    42
            """;

        var result = DiagnosticSession.ParseGameEndpoints(netstat, new Dictionary<int, string> { [42] = "sample-game" }, [Profile]);

        Assert.Equal(2, result.Count);
        Assert.Equal(["93.184.216.34", "93.184.216.35"], result.Select(endpoint => endpoint.Address));
    }

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.1.2")]
    [InlineData("127.0.0.1")]
    [InlineData("0.1.2.3")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("::1")]
    public void IdentifiesPrivateAndNonRoutableAddresses(string value)
    {
        Assert.True(DiagnosticSession.IsPrivateAddress(IPAddress.Parse(value)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("93.184.216.34")]
    [InlineData("2606:4700:4700::1111")]
    public void KeepsPublicAddressesEligibleForProbing(string value)
    {
        Assert.False(DiagnosticSession.IsPrivateAddress(IPAddress.Parse(value)));
    }
}
