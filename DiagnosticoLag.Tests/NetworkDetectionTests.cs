using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using DiagnosticoLag;

namespace DiagnosticoLag.Tests;

public sealed class NetworkDetectionTests
{
    private const string EnglishWlan = """
        There is 1 interface on the system:

            Name                   : Wi-Fi
            Description            : Intel(R) Wi-Fi 6 AX201
            GUID                   : 11111111-1111-1111-1111-111111111111
            Physical address       : aa:bb:cc:dd:ee:ff
            State                  : connected
            SSID                   : HomeNet
            BSSID                  : 11:22:33:44:55:66
            Signal                 : 87%
        """;

    private const string SpanishWlan = """
        Hay 1 interfaz en el sistema:

            Nombre                 : Wi-Fi
            GUID                   : 11111111-1111-1111-1111-111111111111
            Estado                 : conectado
            SSID                   : Casa
            BSSID                  : 11:22:33:44:55:66
            Señal                  : 64%
        """;

    private const string GermanWlan = """
        Name                   : WLAN
        GUID                   : 11111111-1111-1111-1111-111111111111
        SSID                   : Heim
        BSSID                  : 11:22:33:44:55:66
        Signalstärke           : 41%
        """;

    private const string TwoInterfaces = """
        Name                   : Wi-Fi
        GUID                   : aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa
        SSID                   : First
        Signal                 : 90%

        Name                   : Wi-Fi 2
        GUID                   : bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb
        SSID                   : Second
        Signal                 : 35%
        """;

    [Theory]
    [InlineData(EnglishWlan, "HomeNet", 87)]
    [InlineData(SpanishWlan, "Casa", 64)]
    [InlineData(GermanWlan, "Heim", 41)]
    public void ParsesWifiDetailsIndependentlyOfLanguage(string output, string ssid, int signal)
    {
        var details = DiagnosticSession.ParseWifiDetails(output);

        Assert.Equal(ssid, details.Ssid);
        Assert.Equal(signal, details.Signal);
    }

    [Fact]
    public void SelectsTheWifiInterfaceMatchingTheActiveAdapter()
    {
        var second = DiagnosticSession.ParseWifiDetails(TwoInterfaces, "{BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB}");
        var first = DiagnosticSession.ParseWifiDetails(TwoInterfaces, "{aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa}");

        Assert.Equal(("Second", 35), (second.Ssid, second.Signal));
        Assert.Equal(("First", 90), (first.Ssid, first.Signal));
    }

    [Fact]
    public void FallsBackToFirstInterfaceWhenGuidDoesNotMatch()
    {
        var details = DiagnosticSession.ParseWifiDetails(TwoInterfaces, "{cccccccc-cccc-cccc-cccc-cccccccccccc}");

        Assert.Equal("First", details.Ssid);
    }

    [Fact]
    public void WifiDetailsAreEmptyWhenNotConnected()
    {
        var details = DiagnosticSession.ParseWifiDetails("The Wireless AutoConfig Service (wlansvc) is not running.");

        Assert.Null(details.Ssid);
        Assert.Null(details.Signal);
    }

    [Fact]
    public void BssidLinesAreNotMistakenForSsid()
    {
        var details = DiagnosticSession.ParseWifiDetails("    BSSID : 11:22:33:44:55:66");

        Assert.Null(details.Ssid);
    }

    [Fact]
    public async Task IspFirstHopSkipsSilentHopsAndRetries()
    {
        var calls = new List<int>();
        var answers = new Queue<(IPStatus, IPAddress?)>(
        [
            (IPStatus.TtlExpired, IPAddress.Parse("192.168.1.1")),
            (IPStatus.TimedOut, null),
            (IPStatus.TimedOut, null),
            (IPStatus.TimedOut, null),
            (IPStatus.TtlExpired, IPAddress.Parse("100.64.0.1"))
        ]);

        var hop = await DiagnosticSession.FindIspFirstHopAsync("192.168.1.1", (ttl, _) =>
        {
            calls.Add(ttl);
            return Task.FromResult(answers.Dequeue());
        }, CancellationToken.None);

        Assert.Equal("100.64.0.1", hop);
        Assert.Equal([2, 3, 3, 4, 4], calls);
    }

    [Fact]
    public async Task IspFirstHopRecoversWhenFirstAttemptTimesOut()
    {
        var answers = new Queue<(IPStatus, IPAddress?)>(
        [
            (IPStatus.TimedOut, null),
            (IPStatus.TtlExpired, IPAddress.Parse("10.20.30.40"))
        ]);

        var hop = await DiagnosticSession.FindIspFirstHopAsync("192.168.1.1",
            (_, _) => Task.FromResult(answers.Dequeue()), CancellationToken.None);

        Assert.Equal("10.20.30.40", hop);
    }

    [Fact]
    public async Task IspFirstHopStopsWhenDestinationIsReachedEarly()
    {
        var calls = 0;

        var hop = await DiagnosticSession.FindIspFirstHopAsync("192.168.1.1", (_, _) =>
        {
            calls++;
            return Task.FromResult((IPStatus.Success, (IPAddress?)IPAddress.Parse("1.1.1.1")));
        }, CancellationToken.None);

        Assert.Null(hop);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task IspFirstHopIsNullWhenNoHopResponds()
    {
        var calls = 0;

        var hop = await DiagnosticSession.FindIspFirstHopAsync("192.168.1.1", (_, _) =>
        {
            calls++;
            return Task.FromResult((IPStatus.TimedOut, (IPAddress?)null));
        }, CancellationToken.None);

        Assert.Null(hop);
        Assert.Equal(14, calls);
    }

    [Fact]
    public async Task IspFirstHopHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            DiagnosticSession.FindIspFirstHopAsync("192.168.1.1",
                (_, _) => Task.FromResult((IPStatus.TimedOut, (IPAddress?)null)), cancellation.Token));
    }

    [Fact]
    public void WifiUsesMoreTolerantRouterThresholdsThanWiredConnections()
    {
        var wired = DiagnosticSession.RouterThresholdsFor(new ConnectionInfo("Ethernet/cable", "eth", null));
        var wifi = DiagnosticSession.RouterThresholdsFor(new ConnectionInfo("Wi-Fi", "SSID: x", 80));

        Assert.True(wifi.Average > wired.Average);
        Assert.True(wifi.JitterProblem > wired.JitterProblem);
        Assert.True(wifi.P95 > wired.P95);
        Assert.True(wifi.P99 > wired.P99);
        Assert.True(wifi.Maximum > wired.Maximum);
    }

    [Fact]
    public void RouterLatencyBetweenWiredAndWifiLimitsOnlyFlagsWiredConnections()
    {
        var router = Summary(average: 25, jitter: 7, p95: 15, p99: 40);

        var wiredProblems = new List<DiagnosticFinding>();
        var wiredWarnings = new List<DiagnosticFinding>();
        DiagnosticSession.AddRouterDiagnosis(router, new ConnectionInfo("Ethernet/cable", "eth", null), wiredProblems, wiredWarnings);

        var wifiProblems = new List<DiagnosticFinding>();
        var wifiWarnings = new List<DiagnosticFinding>();
        DiagnosticSession.AddRouterDiagnosis(router, new ConnectionInfo("Wi-Fi", "SSID: x", 80), wifiProblems, wifiWarnings);

        Assert.NotEmpty(wiredProblems);
        Assert.NotEmpty(wiredWarnings);
        Assert.Empty(wifiProblems);
        Assert.Empty(wifiWarnings);
    }

    [Fact]
    public void RouterLossIsAProblemRegardlessOfConnectionType()
    {
        var router = Summary(lossPercent: 6);

        foreach (var type in new[] { "Ethernet/cable", "Wi-Fi" })
        {
            var problems = new List<DiagnosticFinding>();
            DiagnosticSession.AddRouterDiagnosis(router, new ConnectionInfo(type, "x", null), problems, []);

            Assert.Single(problems);
        }
    }

    [Fact]
    public void WarnsAboutWeakWifiSignalOnlyOnWifi()
    {
        var healthy = Summary();

        var wifiWarnings = new List<DiagnosticFinding>();
        DiagnosticSession.AddRouterDiagnosis(healthy, new ConnectionInfo("Wi-Fi", "x", 30), [], wifiWarnings);
        var wiredWarnings = new List<DiagnosticFinding>();
        DiagnosticSession.AddRouterDiagnosis(healthy, new ConnectionInfo("Ethernet/cable", "x", 30), [], wiredWarnings);

        Assert.Single(wifiWarnings);
        Assert.Empty(wiredWarnings);
    }

    [Fact]
    public void HealthyRouterProducesNoFindings()
    {
        var problems = new List<DiagnosticFinding>();
        var warnings = new List<DiagnosticFinding>();

        DiagnosticSession.AddRouterDiagnosis(Summary(), new ConnectionInfo("Wi-Fi", "x", 90), problems, warnings);

        Assert.Empty(problems);
        Assert.Empty(warnings);
    }

    [Fact]
    public void ParsesIpv4RowsFromTcpTableBuffer()
    {
        var buffer = Marshal.AllocHGlobal(4 + 24 * 2);
        try
        {
            Marshal.WriteInt32(buffer, 0, 2);
            WriteIpv4Row(buffer, 0, state: 5, remote: [93, 184, 216, 34], port: 443, pid: 42);
            WriteIpv4Row(buffer, 1, state: 2, remote: [10, 0, 0, 1], port: 80, pid: 7);

            var rows = DiagnosticSession.ParseTcpTable(buffer, System.Net.Sockets.AddressFamily.InterNetwork, 24);

            var row = Assert.Single(rows);
            Assert.Equal(42, row.Pid);
            Assert.Equal(IPAddress.Parse("93.184.216.34"), row.Address);
            Assert.Equal(443, row.Port);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void ParsesIpv6RowsFromTcpTableBuffer()
    {
        var buffer = Marshal.AllocHGlobal(4 + 56);
        try
        {
            Marshal.WriteInt32(buffer, 0, 1);
            var row = buffer + 4;
            var address = IPAddress.Parse("2606:4700:4700::1111").GetAddressBytes();
            Marshal.Copy(address, 0, row + 24, 16);
            Marshal.WriteInt32(row, 40, 0);
            Marshal.WriteInt32(row, 44, 0xBB01);
            Marshal.WriteInt32(row, 48, 5);
            Marshal.WriteInt32(row, 52, 99);

            var rows = DiagnosticSession.ParseTcpTable(buffer, System.Net.Sockets.AddressFamily.InterNetworkV6, 56);

            var parsed = Assert.Single(rows);
            Assert.Equal(99, parsed.Pid);
            Assert.Equal(IPAddress.Parse("2606:4700:4700::1111"), parsed.Address);
            Assert.Equal(443, parsed.Port);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void EmptyTcpTableProducesNoConnections()
    {
        var buffer = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(buffer, 0, 0);

            Assert.Empty(DiagnosticSession.ParseTcpTable(buffer, System.Net.Sockets.AddressFamily.InterNetwork, 24));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void ReadsLiveEstablishedConnectionsFromWindows()
    {
        var connections = DiagnosticSession.ReadEstablishedConnections().ToList();

        Assert.All(connections, connection =>
        {
            Assert.InRange(connection.Port, 0, 65535);
            Assert.True(connection.Pid >= 0);
        });
    }

    private static void WriteIpv4Row(IntPtr buffer, int index, int state, byte[] remote, int port, int pid)
    {
        var row = buffer + 4 + index * 24;
        Marshal.WriteInt32(row, 0, state);
        Marshal.WriteInt32(row, 12, BitConverter.ToInt32(remote));
        Marshal.WriteInt32(row, 16, ((port & 0xFF) << 8) | ((port >> 8) & 0xFF));
        Marshal.WriteInt32(row, 20, pid);
    }

    private static StatSummary Summary(
        double lossPercent = 0, double average = 5, double jitter = 1, int p95 = 5, int p99 = 8, int maximum = 10)
    {
        const int samples = 100;
        var lost = (int)Math.Round(samples * lossPercent / 100);
        return new StatSummary(samples, samples - lost, lost, average, 5, p95, p99, maximum, jitter, 0, 0, lossPercent);
    }
}
