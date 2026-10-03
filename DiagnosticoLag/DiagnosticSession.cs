using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace DiagnosticoLag;

internal sealed record ConnectionInfo(string Type, string Detail, int? WifiSignal);
internal sealed record ProbeTarget(string Key, string Name, string Address, ProbeType Type, int Port = 443);
internal enum ProbeType { Icmp, Tcp }
internal sealed record TargetSnapshot(ProbeTarget Target, StatSummary Statistics);
internal sealed record DiagnosticResult(string Level, string Title, string Explanation, string Recommendation, IReadOnlyList<string> Reasons);
internal sealed record MonitorSnapshot(
    DateTime StartedAt,
    TimeSpan Duration,
    IReadOnlyList<TargetSnapshot> Targets,
    IReadOnlyDictionary<string, int> LastMeasurements,
    IReadOnlyList<string> RecentEvents,
    ConnectionInfo Connection,
    string RouterAddress,
    string? IspAddress,
    string? LolAddress,
    int? LolPort,
    DiagnosticResult Diagnosis,
    int SamplesUntilBaseline);

internal sealed class DiagnosticSession
{
    private const string Cloudflare = "1.1.1.1";
    private const string Google = "8.8.8.8";
    private readonly List<ProbeTarget> _targets;
    private readonly Dictionary<string, LatencyStatistics> _statistics = new(StringComparer.Ordinal);
    private readonly Queue<string> _recentEvents = new();
    private readonly Dictionary<string, List<DateTime>> _eventTimes = new(StringComparer.Ordinal);
    private readonly DateTime _startedAt = DateTime.Now;
    private DateTime _lastPeriodicLog = DateTime.Now;
    private int _samples;
    private Baseline? _baseline;
    private string? _lolAddress;
    private int? _lolPort;
    private DateTime _lastLolSearchAt = DateTime.MinValue;

    private DiagnosticSession(string routerAddress, string? ispAddress, ConnectionInfo connection, DateTime startedAt)
    {
        RouterAddress = routerAddress;
        IspAddress = ispAddress;
        Connection = connection;
        _startedAt = startedAt;
        _targets = new List<ProbeTarget>
        {
            new("router", "Router/Modem", routerAddress, ProbeType.Icmp),
            new("cloudflare-icmp", "Cloudflare ICMP", Cloudflare, ProbeType.Icmp),
            new("cloudflare-tcp", "Cloudflare TCP", Cloudflare, ProbeType.Tcp),
            new("google-icmp", "Google ICMP", Google, ProbeType.Icmp),
            new("google-tcp", "Google TCP", Google, ProbeType.Tcp)
        };

        if (!string.IsNullOrWhiteSpace(ispAddress))
        {
            _targets.Insert(1, new ProbeTarget("isp", "ISP (primer salto)", ispAddress, ProbeType.Icmp));
        }

        foreach (var target in _targets)
        {
            _statistics.Add(target.Key, new LatencyStatistics());
        }
    }

    public string RouterAddress { get; }
    public string? IspAddress { get; }
    public ConnectionInfo Connection { get; }
    public DateTime StartedAt => _startedAt;

    public static async Task<DiagnosticSession> CreateAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTime.Now;
        var (router, connection) = DetectConnection();
        var isp = await FindIspFirstHopAsync(router, cancellationToken);
        return new DiagnosticSession(router, isp, connection, startedAt);
    }

    public async Task<MonitorSnapshot> SampleAsync(CancellationToken cancellationToken)
    {
        await TryDetectLolAsync(cancellationToken);

        if (_lolAddress is not null && _lolPort.HasValue && !_targets.Any(target => target.Key == "lol-icmp"))
        {
            _targets.Add(new ProbeTarget("lol-icmp", "LoL (ICMP, aproximado)", _lolAddress, ProbeType.Icmp));
            _targets.Add(new ProbeTarget("lol-tcp", "LoL (TCP, aproximado)", _lolAddress, ProbeType.Tcp, _lolPort.Value));
            _statistics.Add("lol-icmp", new LatencyStatistics());
            _statistics.Add("lol-tcp", new LatencyStatistics());
        }

        var timestamp = DateTime.Now;
        var measurements = await Task.WhenAll(_targets.Select(target => MeasureAsync(target, cancellationToken)));
        _samples++;
        var lastMeasurements = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < _targets.Count; index++)
        {
            var target = _targets[index];
            var value = measurements[index];
            _statistics[target.Key].Add(value);
            lastMeasurements.Add(target.Key, value);
            if (target.Type == ProbeType.Icmp && (value < 0 || value >= 120))
            {
                var text = value < 0 ? "sin respuesta" : $"{value} ms";
                AddEvent(target.Name, text, timestamp);
            }
        }

        if (_samples >= 60 && _baseline is null)
        {
            _baseline = new Baseline(Summary("router"), Summary("cloudflare-icmp"), Summary("google-icmp"));
        }

        return CreateSnapshot(DateTime.Now - _startedAt, lastMeasurements);
    }

    public MonitorSnapshot CurrentSnapshot() => CreateSnapshot(DateTime.Now - _startedAt, new Dictionary<string, int>());

    public bool PeriodicLogIsDue()
    {
        if ((DateTime.Now - _lastPeriodicLog).TotalSeconds < 60)
        {
            return false;
        }

        _lastPeriodicLog = DateTime.Now;
        return true;
    }

    public string BuildReport(string reportType)
    {
        var snapshot = CurrentSnapshot();
        var report = new List<string>
        {
            "",
            "=======================================================================",
            $"INFORME {reportType.ToUpperInvariant()} - DIAGNÓSTICO DE RED",
            "=======================================================================",
            $"Fecha: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"Duración: {snapshot.Duration:hh\\:mm\\:ss}",
            $"Conexión: {Connection.Type} ({Connection.Detail})",
            $"Router: {RouterAddress} | ISP (primer salto): {IspAddress ?? "no detectado"}",
            $"LoL (destino aproximado): {(_lolAddress is null ? "no detectado" : $"{_lolAddress}:{_lolPort}")}",
            _baseline is null
                ? $"Línea base: pendiente ({Math.Max(0, 60 - _samples)} muestras restantes)"
                : $"Línea base inicial: router {_baseline.Router.Average} ms / jitter {_baseline.Router.Jitter} ms; Cloudflare {_baseline.Cloudflare.Average} ms; Google {_baseline.Google.Average} ms",
            ""
        };

        foreach (var target in snapshot.Targets)
        {
            var s = target.Statistics;
            var lossLabel = target.Target.Type == ProbeType.Tcp ? "Fallos" : "Pérdida";
            var endpoint = target.Target.Type == ProbeType.Tcp
                ? $"{target.Target.Address}:{target.Target.Port}"
                : target.Target.Address;
            report.Add($"[{target.Target.Name} - {endpoint}] {s.Samples} muestras");
            report.Add($"  Promedio {s.Average} ms | Mediana {s.Median} ms | P95 {s.P95} ms | P99 {s.P99} ms | Máximo {s.Maximum} ms");
            report.Add($"  Jitter {s.Jitter} ms | Picos 80-119 ms: {s.Spikes80} | >=120 ms: {s.Spikes120} | {lossLabel}: {s.LossPercent}% ({s.Lost}/{s.Samples})");
        }

        report.Add("");
        report.Add($"DIAGNÓSTICO: {snapshot.Diagnosis.Title}");
        report.Add(snapshot.Diagnosis.Explanation);
        report.AddRange(snapshot.Diagnosis.Reasons.Select(reason => $"- {reason}"));
        if (!string.IsNullOrWhiteSpace(snapshot.Diagnosis.Recommendation))
        {
            report.Add($"Recomendación: {snapshot.Diagnosis.Recommendation}");
        }

        report.Add("");
        report.Add("NOTA: el primer salto del ISP es orientativo. El destino LoL detectado es una conexión TCP del cliente y no necesariamente el servidor UDP de la partida.");
        report.Add("NOTA: ICMP puede estar filtrado o recibir menor prioridad; las mediciones TCP en el puerto 443 aportan una referencia distinta.");
        if (snapshot.RecentEvents.Count > 0)
        {
            report.Add("");
            report.Add("EVENTOS RECIENTES:");
            report.AddRange(snapshot.RecentEvents);
        }

        report.Add("=======================================================================");
        return string.Join(Environment.NewLine, report);
    }

    private MonitorSnapshot CreateSnapshot(TimeSpan duration, IReadOnlyDictionary<string, int> lastMeasurements)
    {
        var targets = _targets
            .Where(target => _statistics.ContainsKey(target.Key))
            .Select(target => new TargetSnapshot(target, Summary(target.Key)))
            .ToArray();
        var lookup = targets.ToDictionary(item => item.Target.Key, item => item.Statistics, StringComparer.Ordinal);
        var diagnosis = Diagnose(lookup);
        return new MonitorSnapshot(_startedAt, duration, targets, lastMeasurements, _recentEvents.ToArray(), Connection,
            RouterAddress, IspAddress, _lolAddress, _lolPort, diagnosis, Math.Max(0, 60 - _samples));
    }

    private StatSummary Summary(string key)
    {
        if (!_statistics.TryGetValue(key, out var statistics))
        {
            statistics = new LatencyStatistics();
            _statistics.Add(key, statistics);
        }

        return statistics.Snapshot();
    }

    private async Task<int> MeasureAsync(ProbeTarget target, CancellationToken cancellationToken)
    {
        return target.Type == ProbeType.Icmp
            ? await PingAsync(target.Address, cancellationToken)
            : await TcpAsync(target.Address, target.Port, cancellationToken);
    }

    private static async Task<int> PingAsync(string address, CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, TimeSpan.FromSeconds(1), cancellationToken: cancellationToken);
            return reply.Status == IPStatus.Success ? (int)reply.RoundtripTime : -1;
        }
        catch (PingException) { return -1; }
        catch (SocketException) { return -1; }
    }

    private static async Task<int> TcpAsync(string address, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        var timer = Stopwatch.StartNew();
        try
        {
            await client.ConnectAsync(address, port, timeout.Token);
            return (int)timer.ElapsedMilliseconds;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            return -1;
        }
    }

    private void AddEvent(string targetName, string detail, DateTime timestamp)
    {
        _recentEvents.Enqueue($"[{timestamp:HH:mm:ss}] {targetName}: {detail}");
        while (_recentEvents.Count > 30)
        {
            _recentEvents.Dequeue();
        }

        if (!_eventTimes.TryGetValue(targetName, out var times))
        {
            times = new List<DateTime>();
            _eventTimes.Add(targetName, times);
        }

        times.Add(timestamp);
        if (times.Count > 200)
        {
            times.RemoveAt(0);
        }
    }

    private DiagnosticResult Diagnose(IReadOnlyDictionary<string, StatSummary> values)
    {
        var localProblems = new List<string>();
        var ispProblems = new List<string>();
        var warnings = new List<string>();
        var router = values["router"];

        if (router.LossPercent >= 5) localProblems.Add($"Router: pérdida de paquetes ({router.Lost}/{router.Samples}, {router.LossPercent}%).");
        if (router.Average > 20) localProblems.Add($"Router: latencia media alta ({router.Average} ms).");
        if (router.Jitter > 8) localProblems.Add($"Router: jitter alto ({router.Jitter} ms).");
        if (HasFrequentSpikes(router)) localProblems.Add("Router: se registraron picos frecuentes de latencia.");
        if (router.Maximum >= 150) warnings.Add($"Router: pico máximo aislado de {router.Maximum} ms.");
        if (router.P95 > 10) warnings.Add($"Router: P95 de {router.P95} ms.");
        if (router.P99 > 30) warnings.Add($"Router: P99 de {router.P99} ms.");
        if (router.Jitter > 5 && router.Jitter <= 8) warnings.Add($"Router: jitter de {router.Jitter} ms, por encima de lo deseable.");
        if (router.LossPercent > 0.5 && router.LossPercent < 5) warnings.Add($"Router: pérdida de paquetes de {router.LossPercent}%.");
        if (Connection.Type == "Wi-Fi" && Connection.WifiSignal is < 50)
            warnings.Add($"Señal Wi-Fi baja ({Connection.WifiSignal}%); podría causar picos.");

        AddInternetDiagnosis("Cloudflare", "cloudflare-icmp", "cloudflare-tcp", values, ispProblems, warnings);
        AddInternetDiagnosis("Google", "google-icmp", "google-tcp", values, ispProblems, warnings);

        if (_lolAddress is not null && values.ContainsKey("lol-icmp"))
        {
            AddInternetDiagnosis($"LoL ({_lolAddress}, aproximado)", "lol-icmp", "lol-tcp", values, ispProblems, warnings);
        }

        if (values.TryGetValue("isp", out var isp) && ispProblems.Count > 0 &&
            (HasFrequentSpikes(isp) || isp.LossPercent > 3 || isp.Jitter > 15))
        {
            ispProblems.Add($"El primer salto del ISP también presenta anomalías (jitter {isp.Jitter} ms, pérdida {isp.LossPercent}%).");
        }

        if (HasFrequentSpikes(router) &&
            !HasFrequentSpikes(values["cloudflare-icmp"]) &&
            !HasFrequentSpikes(values["google-icmp"]) &&
            (!values.TryGetValue("isp", out isp) || !HasFrequentSpikes(isp)))
        {
            warnings.Add("Los picos del router no se repiten en destinos externos; apunta más a Wi-Fi o al router que al ISP.");
        }

        AddBaselineWarnings(values, warnings);
        AddPeriodicWarnings(warnings);

        if (localProblems.Count > 0)
        {
            return new DiagnosticResult("PROBLEMA", "Problema en la red local", "Se observan fallos hasta el router, antes de salir a Internet.",
                "Repetí la prueba por Ethernet. Si desaparece, revisá el Wi-Fi; si persiste, revisá el router.", localProblems.Concat(warnings).ToArray());
        }

        if (ispProblems.Count > 0)
        {
            return new DiagnosticResult("PROBLEMA", "Problema de conexión a Internet", "La red local parece estable, pero hay anomalías en conexiones externas.",
                "Guardá el registro y compartilo con tu proveedor si el problema persiste.", ispProblems.Concat(warnings).ToArray());
        }

        if (warnings.Count > 0)
        {
            return new DiagnosticResult("ATENCION", "Se detectaron anomalías aisladas", "No hay evidencia suficiente de un problema sostenido.",
                "Dejá correr el monitoreo mientras ocurre el lag y repetí la prueba por Ethernet.", warnings);
        }

        return new DiagnosticResult("OK", "Sin problemas relevantes en las mediciones", "No se observan pérdidas relevantes, jitter alto ni picos frecuentes.",
            "", Array.Empty<string>());
    }

    private static void AddInternetDiagnosis(
        string name,
        string icmpKey,
        string tcpKey,
        IReadOnlyDictionary<string, StatSummary> values,
        ICollection<string> problems,
        ICollection<string> warnings)
    {
        var icmp = values[icmpKey];
        var tcp = values[tcpKey];
        var icmpProblem = icmp.LossPercent > 3 || icmp.Average > 100 || icmp.Jitter > 15 || HasFrequentSpikes(icmp);
        var tcpProblem = tcp.LossPercent > 3 || tcp.Average > 150 || tcp.Jitter > 20 || HasFrequentSpikes(tcp);

        if (icmpProblem && tcpProblem)
            problems.Add($"{name}: anomalías tanto en ICMP como en TCP/443 (jitter TCP {tcp.Jitter} ms, fallos {tcp.LossPercent}%).");
        else if (tcpProblem)
            problems.Add($"{name}: la conexión TCP/443 presenta anomalías aunque ICMP no; es una referencia de tráfico real.");
        else if (icmpProblem)
            warnings.Add($"{name}: ICMP muestra anomalías, pero TCP/443 está limpio; podría ser priorización o filtrado de ping.");

        if (icmp.Maximum >= 200) warnings.Add($"{name} ICMP: pico aislado de {icmp.Maximum} ms.");
        if (tcp.Maximum >= 250) warnings.Add($"{name} TCP: conexión de hasta {tcp.Maximum} ms.");
        if (name.StartsWith("LoL", StringComparison.Ordinal) && icmp.LossPercent >= 100 && !tcpProblem)
            warnings.Add("El destino aproximado de LoL no responde a ICMP; es común que servidores de juego filtren ping.");
    }

    private void AddBaselineWarnings(IReadOnlyDictionary<string, StatSummary> values, ICollection<string> warnings)
    {
        if (_baseline is null)
        {
            return;
        }

        CompareBaseline("Router", values["router"], _baseline.Router, warnings);
        CompareBaseline("Cloudflare", values["cloudflare-icmp"], _baseline.Cloudflare, warnings);
        CompareBaseline("Google", values["google-icmp"], _baseline.Google, warnings);

        var baselineRouter = _baseline.Router;
        var currentRouter = values["router"];
        var jitterThreshold = Math.Max(baselineRouter.Jitter * 3, baselineRouter.Jitter + 5);
        if (currentRouter.Jitter >= jitterThreshold && currentRouter.Jitter > 3)
            warnings.Add($"El jitter actual del router ({currentRouter.Jitter} ms) supera ampliamente la línea base ({baselineRouter.Jitter} ms).");
    }

    private static void CompareBaseline(string name, StatSummary current, StatSummary baseline, ICollection<string> warnings)
    {
        if (baseline.Average > 0 && current.Average >= baseline.Average * 3 && current.Average - baseline.Average >= 5)
            warnings.Add($"{name}: promedio actual ({current.Average} ms) muy superior a la línea base ({baseline.Average} ms).");
    }

    private void AddPeriodicWarnings(ICollection<string> warnings)
    {
        foreach (var (name, times) in _eventTimes)
        {
            if (times.Count < 4)
            {
                continue;
            }

            var intervals = times.Zip(times.Skip(1), (first, second) => (second - first).TotalSeconds).ToArray();
            var average = intervals.Average();
            if (average < 10)
            {
                continue;
            }

            var deviation = Math.Sqrt(intervals.Sum(interval => Math.Pow(interval - average, 2)) / intervals.Length);
            if (deviation / average <= 0.25)
            {
                warnings.Add($"{name}: anomalías periódicas cada ~{average:F0} s; revisá tareas programadas, sincronización o escaneos DFS del Wi-Fi.");
            }
        }
    }

    private static bool HasFrequentSpikes(StatSummary stats)
    {
        if (stats.Samples == 0)
        {
            return false;
        }

        var allSpikes = stats.Spikes80 + stats.Spikes120;
        return (stats.Spikes120 >= 3 && (double)stats.Spikes120 / stats.Samples >= 0.005) ||
               (allSpikes >= 5 && (double)allSpikes / stats.Samples >= 0.01);
    }

    private sealed record Baseline(StatSummary Router, StatSummary Cloudflare, StatSummary Google);

    private async Task TryDetectLolAsync(CancellationToken cancellationToken)
    {
        if (_lolAddress is not null || (DateTime.Now - _lastLolSearchAt).TotalSeconds < 15)
        {
            return;
        }

        _lastLolSearchAt = DateTime.Now;
        var endpoint = await Task.Run(FindLolEndpoint, cancellationToken);
        if (endpoint is not null)
        {
            _lolAddress = endpoint.Value.Address;
            _lolPort = endpoint.Value.Port;
        }
    }

    private static (string Address, int Port)? FindLolEndpoint()
    {
        var processIds = new HashSet<int>();
        var processes = Process.GetProcesses();
        foreach (var process in processes)
        {
            try
            {
                if (process.ProcessName.Contains("League", StringComparison.OrdinalIgnoreCase))
                {
                    processIds.Add(process.Id);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                    Trace.TraceWarning($"No se pudo inspeccionar un proceso al detectar el cliente de LoL: {exception.Message}");
                }
            finally
            {
                process.Dispose();
            }
        }

        if (processIds.Count == 0)
        {
            return null;
        }

        using var netstat = Process.Start(new ProcessStartInfo
        {
            FileName = "netstat.exe",
            Arguments = "-ano -p tcp",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        });
        if (netstat is null)
        {
            return null;
        }

        var output = netstat.StandardOutput.ReadToEnd();
        if (!netstat.WaitForExit(3000))
        {
            netstat.Kill();
            return null;
        }

        foreach (var line in output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Regex.Match(line, @"^\s*TCP\s+\S+\s+(?<remote>\S+)\s+ESTABLISHED\s+(?<pid>\d+)", RegexOptions.IgnoreCase);
            if (!match.Success || !int.TryParse(match.Groups["pid"].Value, out var pid) || !processIds.Contains(pid))
            {
                continue;
            }

            var endpoint = match.Groups["remote"].Value;
            var separator = endpoint.LastIndexOf(':');
            var addressText = separator <= 0 ? string.Empty : endpoint[..separator].Trim('[', ']');
            if (separator <= 0 || !int.TryParse(endpoint[(separator + 1)..], out var port) ||
                !IPAddress.TryParse(addressText, out var address) || IsPrivateAddress(address))
            {
                continue;
            }

            return (address.ToString(), port);
        }

        return null;
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10 ||
                   bytes[0] == 127 ||
                   bytes[0] == 169 && bytes[1] == 254 ||
                   bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                   bytes[0] == 192 && bytes[1] == 168 ||
                   bytes[0] == 0;
        }

        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast ||
               (bytes[0] & 0xFE) == 0xFC;
    }

    private static (string Router, ConnectionInfo Connection) DetectConnection()
    {
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(item => item.OperationalStatus == OperationalStatus.Up))
        {
            var gateway = network.GetIPProperties().GatewayAddresses
                .Select(item => item.Address)
                .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork &&
                                           !address.Equals(IPAddress.Any));
            if (gateway is null)
            {
                continue;
            }

            var isWifi = network.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            var wifi = isWifi ? ReadWifiDetails() : (Ssid: null, Signal: (int?)null);
            var detail = network.Name;
            if (isWifi)
            {
                if (!string.IsNullOrWhiteSpace(wifi.Ssid))
                {
                    detail = $"SSID: {wifi.Ssid}";
                }
                if (wifi.Signal.HasValue)
                {
                    detail += $" (señal: {wifi.Signal.Value}%)";
                }
            }

            return (gateway.ToString(), new ConnectionInfo(isWifi ? "Wi-Fi" : "Ethernet/cable", detail, wifi.Signal));
        }

        throw new InvalidOperationException("No se pudo detectar una puerta de enlace IPv4. Conectá el equipo a una red y volvé a intentar.");
    }

    private static (string? Ssid, int? Signal) ReadWifiDetails()
    {
        var output = RunNetsh();
        var ssidMatch = Regex.Match(output, @"(?im)^\s*SSID\s*:\s*(?<ssid>.+?)\s*$");
        var signalMatch = Regex.Match(output, @"(?im)^\s*(?:Signal|Señal)\s*:\s*(?<value>\d{1,3})\s*%");
        var ssid = ssidMatch.Success ? ssidMatch.Groups["ssid"].Value.Trim() : null;
        int? signal = signalMatch.Success && int.TryParse(signalMatch.Groups["value"].Value, out var value) ? value : null;
        return (ssid, signal);
    }

    private static string RunNetsh()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "netsh.exe",
            Arguments = "wlan show interfaces",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        });
        if (process is null)
        {
            return string.Empty;
        }

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(2000);
        return output;
    }

    private static async Task<string?> FindIspFirstHopAsync(string router, CancellationToken cancellationToken)
    {
        using var ping = new Ping();
        for (var ttl = 2; ttl <= 6; ttl++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var reply = await ping.SendPingAsync(Cloudflare, TimeSpan.FromMilliseconds(800),
                    new byte[32], new PingOptions(ttl, true), cancellationToken);
                if (reply.Status == IPStatus.TtlExpired && reply.Address is not null &&
                    !string.Equals(reply.Address.ToString(), router, StringComparison.OrdinalIgnoreCase))
                {
                    return reply.Address.ToString();
                }
            }
            catch (PingException) { }
        }

        return null;
    }
}
