using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace DiagnosticoLag;

internal sealed record ConnectionInfo(string Type, string Detail, int? WifiSignal);
internal sealed record ProbeTarget(
    string Key,
    string Name,
    string Address,
    ProbeType Type,
    int Port = 443,
    string? GameProfileId = null,
    string? GameProfileName = null,
    string? ProcessName = null,
    int? ObservedPort = null);
internal enum ProbeType { Icmp, Tcp }
internal sealed record TargetSnapshot(ProbeTarget Target, StatSummary Statistics);
internal sealed record GameObservedEndpoint(string ProfileId, string ProfileName, string ProcessName, string Address, int Port, string ProbeKey);
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
    IReadOnlyList<GameObservedEndpoint> GameEndpoints,
    IReadOnlyList<GameObservedEndpoint> ActiveGameEndpoints,
    DiagnosticResult Diagnosis,
    int SamplesUntilBaseline);

internal sealed class DiagnosticSession
{
    private const int MaximumGameProfiles = 8;
    private const int MaximumProcessNamesPerProfile = 8;
    private const int MaximumActiveEndpointsPerProfile = 2;
    private const int MaximumObservedEndpointsPerProfile = 8;

    private readonly DiagnosticSettings _settings;
    private readonly List<ProbeTarget> _targets;
    private readonly Dictionary<string, LatencyStatistics> _statistics = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<int>> _recentMeasurements = new(StringComparer.Ordinal);
    private readonly Queue<string> _recentEvents = new();
    private readonly Dictionary<string, List<DateTime>> _eventTimes = new(StringComparer.Ordinal);
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly DateTime _startedAt = DateTime.Now;
    private DateTime _lastPeriodicLog = DateTime.Now;
    private int _samples;
    private Baseline? _baseline;
    private readonly Dictionary<string, ProbeTarget> _gameTargets = new(StringComparer.Ordinal);
    private IReadOnlyList<GameObservedEndpoint> _activeGameEndpoints = Array.Empty<GameObservedEndpoint>();
    private DateTime _lastGameScanAt = DateTime.MinValue;

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetBestInterface(uint destinationAddress, out uint interfaceIndex);

    private DiagnosticSession(DiagnosticSettings settings, string routerAddress, string? ispAddress, ConnectionInfo connection, DateTime startedAt)
    {
        _settings = settings;
        RouterAddress = routerAddress;
        IspAddress = ispAddress;
        Connection = connection;
        _startedAt = startedAt;
        _targets = new List<ProbeTarget>
        {
            new("router", "Router/Modem", routerAddress, ProbeType.Icmp),
            new("cloudflare-icmp", "Destino 1 ICMP", settings.CloudflareAddress, ProbeType.Icmp),
            new("cloudflare-tcp", "Destino 1 TCP", settings.CloudflareAddress, ProbeType.Tcp),
            new("google-icmp", "Destino 2 ICMP", settings.GoogleAddress, ProbeType.Icmp),
            new("google-tcp", "Destino 2 TCP", settings.GoogleAddress, ProbeType.Tcp)
        };

        if (!string.IsNullOrWhiteSpace(ispAddress))
        {
            _targets.Insert(1, new ProbeTarget("isp", "ISP (primer salto)", ispAddress, ProbeType.Icmp));
        }

        foreach (var target in _targets)
        {
            _statistics.Add(target.Key, new LatencyStatistics());
            _recentMeasurements.Add(target.Key, new Queue<int>());
        }
    }

    public string RouterAddress { get; }
    public string? IspAddress { get; }
    public ConnectionInfo Connection { get; }
    public DateTime StartedAt => _startedAt;

    public static async Task<DiagnosticSession> CreateAsync(DiagnosticSettings settings, CancellationToken cancellationToken)
    {
        var startedAt = DateTime.Now;
        var (router, connection) = DetectConnection(settings.NetworkInterfaceId);
        var isp = await FindIspFirstHopAsync(router, settings.CloudflareAddress, cancellationToken);
        return new DiagnosticSession(settings, router, isp, connection, startedAt);
    }

    public async Task<MonitorSnapshot> SampleAsync(CancellationToken cancellationToken)
    {
        await RefreshGameEndpointsAsync(cancellationToken);

        var timestamp = DateTime.Now;
        var activeGameTargets = _activeGameEndpoints
            .Select(endpoint => _gameTargets[endpoint.ProbeKey])
            .DistinctBy(target => target.Key)
            .ToArray();
        var targetsToMeasure = _targets.Concat(activeGameTargets).ToArray();
        var measurements = await Task.WhenAll(targetsToMeasure.Select(target => MeasureAsync(target, cancellationToken)));
        _samples++;
        var lastMeasurements = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < targetsToMeasure.Length; index++)
        {
            var target = targetsToMeasure[index];
            var value = measurements[index];
            _statistics[target.Key].Add(value);
            var recent = _recentMeasurements[target.Key];
            recent.Enqueue(value);
            if (recent.Count > 60)
            {
                recent.Dequeue();
            }
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

        return CreateSnapshot(_elapsed.Elapsed, lastMeasurements);
    }

    public MonitorSnapshot CurrentSnapshot() => CreateSnapshot(_elapsed.Elapsed, new Dictionary<string, int>());

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
            $"Duración: {FormatDuration(snapshot.Duration)}",
            $"Conexión: {Connection.Type} ({Connection.Detail})",
            $"Router: {RouterAddress} | ISP (primer salto): {IspAddress ?? "no detectado"}",
            $"Destinos externos: { _settings.CloudflareAddress} | {_settings.GoogleAddress}",
            "Las mediciones de juegos son referencias ICMP aproximadas a endpoints TCP observados del proceso; no representan el ping real de la partida.",
            _baseline is null
                ? $"Línea base: pendiente ({Math.Max(0, 60 - _samples)} muestras restantes)"
                : $"Línea base inicial (primeros 60 sondeos): router {_baseline.Router.Average} ms / jitter {_baseline.Router.Jitter} ms; destino 1 {_baseline.Cloudflare.Average} ms; destino 2 {_baseline.Google.Average} ms",
            ""
        };

        foreach (var target in snapshot.Targets)
        {
            var s = target.Statistics;
            var lossLabel = target.Target.Type == ProbeType.Tcp ? "Fallos" : "Pérdida";
            var endpoint = target.Target.ObservedPort is int observedPort
                ? $"{target.Target.Address}:{observedPort} (TCP observado; referencia ICMP)"
                : target.Target.Type == ProbeType.Tcp
                    ? $"{target.Target.Address}:{target.Target.Port}"
                    : target.Target.Address;
            report.Add($"[{target.Target.Name} - {endpoint}] {s.Samples} muestras");
            report.Add($"  Promedio {s.Average} ms | Mediana {s.Median} ms | P95 {s.P95} ms | P99 {s.P99} ms | Máximo {s.Maximum} ms");
            report.Add($"  Jitter {s.Jitter} ms | Picos 80-119 ms: {s.Spikes80} | >=120 ms: {s.Spikes120} | {lossLabel}: {s.LossPercent}% ({s.Lost}/{s.Samples})");
        }

        if (snapshot.GameEndpoints.Count > 0)
        {
            report.Add("");
            report.Add("CONEXIONES DE JUEGOS OBSERVADAS:");
            report.AddRange(snapshot.GameEndpoints.Select(endpoint =>
                $"  {endpoint.ProfileName}: {endpoint.Address}:{endpoint.Port} (proceso {endpoint.ProcessName}); la ruta ICMP es una referencia, no la latencia de la partida."));
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
        report.Add($"NOTA: se requieren al menos 30 muestras por destino para incluirlo en el diagnóstico. Se sondea cada {_settings.SampleIntervalSeconds} segundo(s).");
        report.Add("NOTA: el primer salto del ISP es orientativo. Los endpoints de juegos se observan en conexiones TCP de sus procesos y pueden ser auxiliares, no el servidor de partida.");
        report.Add("NOTA: ICMP puede estar filtrado o recibir menor prioridad; las mediciones TCP en el puerto 443 aportan una referencia distinta.");
        if (snapshot.GameEndpoints.Count > 0)
        {
            report.Add("NOTA: la latencia ICMP a un endpoint de juego es una referencia aproximada de ruta, no el ping real del juego; muchos juegos usan UDP o protocolos propios.");
        }
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
        var targets = _targets.Concat(_gameTargets.Values)
            .Where(target => _statistics.ContainsKey(target.Key))
            .Select(target => new TargetSnapshot(target, Summary(target.Key)))
            .ToArray();
        var lookup = targets.ToDictionary(item => item.Target.Key, item => item.Statistics, StringComparer.Ordinal);
        var recentLookup = _recentMeasurements.ToDictionary(
            item => item.Key,
            item => LatencyStatistics.FromSamples(item.Value),
            StringComparer.Ordinal);
        var diagnosis = Diagnose(lookup, recentLookup);
        var gameEndpoints = _gameTargets.Values
            .Select(target => new GameObservedEndpoint(
                target.GameProfileId!,
                target.GameProfileName!,
                target.ProcessName!,
                target.Address,
                target.ObservedPort!.Value,
                target.Key))
            .ToArray();
        return new MonitorSnapshot(_startedAt, duration, targets, lastMeasurements, _recentEvents.ToArray(), Connection,
            RouterAddress, IspAddress, gameEndpoints, _activeGameEndpoints, diagnosis, Math.Max(0, 60 - _samples));
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

    private DiagnosticResult Diagnose(
        IReadOnlyDictionary<string, StatSummary> values,
        IReadOnlyDictionary<string, StatSummary> recentValues)
    {
        if (values["router"].Samples < 30)
        {
            var remaining = 30 - values["router"].Samples;
            return new DiagnosticResult(
                "DATOS_INSUFICIENTES",
                "Midiendo: todavía no hay muestras suficientes",
                $"Se necesitan al menos 30 muestras (faltan {remaining}) para evaluar los resultados. Una conclusión más confiable requiere dejar correr el monitoreo durante varios minutos.",
                "Mantené el monitoreo activo mientras experimentás el problema.",
                Array.Empty<string>());
        }

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

        AddInternetDiagnosis("Destino 1", "cloudflare-icmp", "cloudflare-tcp", values, ispProblems, warnings);
        AddInternetDiagnosis("Destino 2", "google-icmp", "google-tcp", values, ispProblems, warnings);

        foreach (var gameTarget in _gameTargets.Values)
        {
            if (!values.TryGetValue(gameTarget.Key, out var gameRoute) || gameRoute.Samples < 30)
            {
                continue;
            }

            if (gameRoute.LossPercent > 3 || gameRoute.Average > 150 || gameRoute.Jitter > 25 || HasFrequentSpikes(gameRoute))
            {
                warnings.Add($"{gameTarget.GameProfileName}: la referencia ICMP a {gameTarget.Address} presenta anomalías; esto no confirma problemas en el servidor ni mide el ping real de la partida.");
            }
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

        AddBaselineWarnings(recentValues, warnings);
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
        if (icmp.Samples < 30 || tcp.Samples < 30)
        {
            return;
        }

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

    private void AddBaselineWarnings(IReadOnlyDictionary<string, StatSummary> recentValues, ICollection<string> warnings)
    {
        if (_baseline is null || recentValues["router"].Samples < 30)
        {
            return;
        }

        CompareBaseline("Router (últimas muestras)", recentValues["router"], _baseline.Router, warnings);
        CompareBaseline("Destino 1 (últimas 60 muestras)", recentValues["cloudflare-icmp"], _baseline.Cloudflare, warnings);
        CompareBaseline("Destino 2 (últimas 60 muestras)", recentValues["google-icmp"], _baseline.Google, warnings);

        var baselineRouter = _baseline.Router;
        var currentRouter = recentValues["router"];
        var jitterThreshold = Math.Max(baselineRouter.Jitter * 3, baselineRouter.Jitter + 5);
        if (currentRouter.Jitter >= jitterThreshold && currentRouter.Jitter > 3)
            warnings.Add($"El jitter actual del router ({currentRouter.Jitter} ms) supera ampliamente la línea base ({baselineRouter.Jitter} ms).");
    }

    private static void CompareBaseline(string name, StatSummary current, StatSummary baseline, ICollection<string> warnings)
    {
        if (baseline.Average > 0 && current.Average >= baseline.Average * 3 && current.Average - baseline.Average >= 5)
            warnings.Add($"{name}: promedio actual ({current.Average} ms) muy superior a la línea base ({baseline.Average} ms).");
    }

    private static string FormatDuration(TimeSpan duration)
    {
        var totalHours = (int)duration.TotalHours;
        return duration.TotalDays >= 1
            ? $"{(int)duration.TotalDays}d {duration.Hours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{totalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
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

    private async Task RefreshGameEndpointsAsync(CancellationToken cancellationToken)
    {
        var enabledProfiles = _settings.GameProfiles.Where(profile => profile.Enabled).ToArray();
        if (enabledProfiles.Length == 0)
        {
            _activeGameEndpoints = Array.Empty<GameObservedEndpoint>();
            return;
        }

        if ((DateTime.Now - _lastGameScanAt).TotalSeconds < 5)
        {
            return;
        }

        _lastGameScanAt = DateTime.Now;
        _activeGameEndpoints = Array.Empty<GameObservedEndpoint>();
        try
        {
            var endpoints = await Task.Run(() => FindGameEndpoints(enabledProfiles), cancellationToken);
            var active = new List<GameObservedEndpoint>();
            foreach (var endpoint in endpoints)
            {
                var key = $"game:{endpoint.ProfileId}:{endpoint.Address}:{endpoint.Port}";
                var observed = endpoint with { ProbeKey = key };
                if (!_gameTargets.ContainsKey(key) &&
                    _gameTargets.Values.Count(target => target.GameProfileId == endpoint.ProfileId) >= MaximumObservedEndpointsPerProfile)
                {
                    continue;
                }

                active.Add(observed);
                if (!_gameTargets.ContainsKey(key))
                {
                    var profile = enabledProfiles.First(item => item.Id == endpoint.ProfileId);
                    var target = new ProbeTarget(
                        key,
                        $"{profile.Name} - ICMP aprox. a {endpoint.Address}:{endpoint.Port}",
                        endpoint.Address,
                        ProbeType.Icmp,
                        GameProfileId: profile.Id,
                        GameProfileName: profile.Name,
                        ProcessName: endpoint.ProcessName,
                        ObservedPort: endpoint.Port);
                    _gameTargets.Add(key, target);
                    _statistics.Add(key, new LatencyStatistics());
                    _recentMeasurements.Add(key, new Queue<int>());
                }
            }

            _activeGameEndpoints = active;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            Trace.TraceWarning($"No se pudieron detectar conexiones de juegos; el monitoreo general continúa: {exception.Message}");
        }
    }

    private static IReadOnlyList<GameObservedEndpoint> FindGameEndpoints(IReadOnlyList<GameMonitoringProfile> profiles)
    {
        var processNames = new Dictionary<int, string>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                processNames[process.Id] = process.ProcessName;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Trace.TraceWarning($"No se pudo inspeccionar un proceso durante la detección de juegos: {exception.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        if (processNames.Count == 0)
        {
            return Array.Empty<GameObservedEndpoint>();
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
            throw new InvalidOperationException("No se pudo iniciar netstat para inspeccionar conexiones TCP activas.");
        }

        var outputTask = netstat.StandardOutput.ReadToEndAsync();
        if (!netstat.WaitForExit(3000))
        {
            netstat.Kill();
            throw new IOException("La consulta de conexiones TCP excedió el tiempo límite.");
        }

        var results = new List<GameObservedEndpoint>();
        foreach (var line in outputTask.GetAwaiter().GetResult().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Regex.Match(line, @"^\s*TCP\s+\S+\s+(?<remote>\S+)\s+ESTABLISHED\s+(?<pid>\d+)", RegexOptions.IgnoreCase);
            if (!match.Success || !int.TryParse(match.Groups["pid"].Value, out var pid) ||
                !processNames.TryGetValue(pid, out var processName))
            {
                continue;
            }

            var endpointText = match.Groups["remote"].Value;
            var separator = endpointText.LastIndexOf(':');
            var addressText = separator <= 0 ? string.Empty : endpointText[..separator].Trim('[', ']');
            if (separator <= 0 || !int.TryParse(endpointText[(separator + 1)..], out var port) ||
                !IPAddress.TryParse(addressText, out var address) || IsPrivateAddress(address))
            {
                continue;
            }

            foreach (var profile in profiles.Where(profile =>
                         profile.ProcessNames.Any(candidate =>
                         {
                             var processPattern = Path.GetFileNameWithoutExtension(candidate.Trim());
                             return processPattern.Length > 0 &&
                                    processName.Contains(processPattern, StringComparison.OrdinalIgnoreCase);
                         })))
            {
                if (results.Count(endpoint => endpoint.ProfileId == profile.Id) >= MaximumActiveEndpointsPerProfile ||
                    results.Any(endpoint => endpoint.ProfileId == profile.Id &&
                                            endpoint.Address == address.ToString() && endpoint.Port == port))
                {
                    continue;
                }

                results.Add(new GameObservedEndpoint(profile.Id, profile.Name, processName, address.ToString(), port, ""));
            }
        }

        return results;
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

    private static (string Router, ConnectionInfo Connection) DetectConnection(string? selectedInterfaceId)
    {
        NetworkInterface? network;
        if (!string.IsNullOrWhiteSpace(selectedInterfaceId))
        {
            network = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(item => item.Id == selectedInterfaceId && item.OperationalStatus == OperationalStatus.Up);
            if (network is null)
            {
                throw new InvalidOperationException("La interfaz de red seleccionada ya no está disponible. Elegí otra en Configuración.");
            }
        }
        else
        {
            var destination = BitConverter.ToUInt32(IPAddress.Parse("1.1.1.1").GetAddressBytes());
            var result = GetBestInterface(destination, out var bestInterfaceIndex);
            if (result != 0)
            {
                throw new System.ComponentModel.Win32Exception((int)result, "No se pudo determinar la interfaz IPv4 que Windows usa para salir a Internet.");
            }

            network = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(item => item.OperationalStatus == OperationalStatus.Up &&
                                        item.GetIPProperties().GetIPv4Properties()?.Index == bestInterfaceIndex);
        }

        var gateway = network?.GetIPProperties().GatewayAddresses
            .Select(item => item.Address)
            .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork &&
                                       !address.Equals(IPAddress.Any));
        if (network is null || gateway is null)
        {
            throw new InvalidOperationException("Windows identificó la interfaz de salida, pero no se pudo obtener su puerta de enlace IPv4.");
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

        var outputTask = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(2000))
        {
            process.Kill();
        }

        return outputTask.GetAwaiter().GetResult();
    }

    private static async Task<string?> FindIspFirstHopAsync(string router, string destination, CancellationToken cancellationToken)
    {
        using var ping = new Ping();
        for (var ttl = 2; ttl <= 6; ttl++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var reply = await ping.SendPingAsync(destination, TimeSpan.FromMilliseconds(800),
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
