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
internal sealed record DiagnosticFinding(string Text, string Explanation);
internal sealed record DiagnosticResult(string Level, string Title, string Explanation, string Recommendation, IReadOnlyList<DiagnosticFinding> Findings);
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
    private readonly Dictionary<string, StatSummary> _recentSummaryCache = new(StringComparer.Ordinal);
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
    private DiagnosticResult? _diagnosisCache;
    private int _diagnosisCacheSampleCount = -1;

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
        var (router, connection) = await Task.Run(() => DetectConnection(settings.NetworkInterfaceId, settings.CloudflareAddress), cancellationToken);
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
            _recentSummaryCache.Clear();
            var recent = _recentMeasurements[target.Key];
            recent.Enqueue(value);
            if (recent.Count > 60)
            {
                recent.Dequeue();
            }
            lastMeasurements.Add(target.Key, value);
            if (target.Type == ProbeType.Icmp && (value < 0 || value >= 120))
            {
                var text = value < 0 ? Localization.T("sin respuesta") : $"{value} {Localization.T("ms")}";
                AddEvent(Localization.T(target.Name), text, timestamp);
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
            Localization.F("INFORME {0} - DIAGNÓSTICO DE RED", Localization.T(reportType).ToUpper(Localization.Culture)),
            "=======================================================================",
            $"{Localization.T("Fecha")}: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"{Localization.T("Duración")}: {FormatDuration(snapshot.Duration)}",
            $"{Localization.T("Conexión")}: {Localization.T(Connection.Type)} ({Connection.Detail})",
            $"{Localization.T("Router")}: {RouterAddress} | {Localization.T("ISP (primer salto)")}: {IspAddress ?? Localization.T("no detectado")}",
            $"{Localization.T("Destinos externos")}: { _settings.CloudflareAddress} | {_settings.GoogleAddress}",
            Localization.T("Las mediciones de juegos son referencias ICMP aproximadas a endpoints TCP observados del proceso; no representan el ping real de la partida."),
            _baseline is null
                ? $"{Localization.T("Línea base")}: {Localization.F("pendiente ({0} muestras restantes)", Math.Max(0, 60 - _samples))}"
                : Localization.F("Línea base inicial (primeros 60 sondeos): router {0} ms / jitter {1} ms; destino 1 {2} ms; destino 2 {3} ms",
                    _baseline.Router.Average, _baseline.Router.Jitter, _baseline.Cloudflare.Average, _baseline.Google.Average),
            ""
        };

        foreach (var target in snapshot.Targets)
        {
            var s = target.Statistics;
            var lossLabel = Localization.T(target.Target.Type == ProbeType.Tcp ? "Fallos" : "Pérdida");
            var endpoint = target.Target.ObservedPort is int observedPort
                ? $"{target.Target.Address}:{observedPort} (TCP observado; referencia ICMP)"
                : target.Target.Type == ProbeType.Tcp
                    ? $"{target.Target.Address}:{target.Target.Port}"
                    : target.Target.Address;
            report.Add($"[{Localization.T(target.Target.Name)} - {endpoint}] {Localization.F("{0} muestras", s.Samples)}");
            report.Add(Localization.F("Promedio {0} ms | Mediana {1} ms | P95 {2} ms | P99 {3} ms | Máximo {4} ms",
                s.Average, s.Median, s.P95, s.P99, s.Maximum));
            report.Add(Localization.F("Jitter {0} ms | Picos 80-119 ms: {1} | >=120 ms: {2} | {3}: {4}% ({5}/{6})",
                s.Jitter, s.Spikes80, s.Spikes120, lossLabel, s.LossPercent, s.Lost, s.Samples));
        }

        if (snapshot.GameEndpoints.Count > 0)
        {
            report.Add("");
            report.Add(Localization.T("CONEXIONES DE JUEGOS OBSERVADAS:"));
            report.AddRange(snapshot.GameEndpoints.Select(endpoint =>
                Localization.F("  {0}: {1}:{2} (proceso {3}); la ruta ICMP es una referencia, no la latencia de la partida.",
                    endpoint.ProfileName, endpoint.Address, endpoint.Port, endpoint.ProcessName)));
        }

        report.Add("");
        report.Add(Localization.F("DIAGNÓSTICO: {0}", Localization.T(snapshot.Diagnosis.Title)));
        report.Add(Localization.T(snapshot.Diagnosis.Explanation));
        report.AddRange(snapshot.Diagnosis.Findings.Select(finding =>
            $"- {finding.Text}{Environment.NewLine}  {Localization.T("Qué puede estar pasando")}: {finding.Explanation}"));
        if (!string.IsNullOrWhiteSpace(snapshot.Diagnosis.Recommendation))
        {
            report.Add(Localization.F("Recomendación: {0}", Localization.T(snapshot.Diagnosis.Recommendation)));
        }

        report.Add("");
        report.Add(Localization.F("NOTA: se requieren al menos 30 muestras por destino para incluirlo en el diagnóstico. Se sondea cada {0} segundo(s).", _settings.SampleIntervalSeconds));
        report.Add(Localization.T("NOTA: el primer salto del ISP es orientativo. Los endpoints de juegos se observan en conexiones TCP de sus procesos y pueden ser auxiliares, no el servidor de partida."));
        report.Add(Localization.T("NOTA: ICMP puede estar filtrado o recibir menor prioridad; las mediciones TCP en el puerto 443 aportan una referencia distinta."));
        if (snapshot.GameEndpoints.Count > 0)
        {
            report.Add(Localization.T("NOTA: la latencia ICMP a un endpoint de juego es una referencia aproximada de ruta, no el ping real del juego; muchos juegos usan UDP o protocolos propios."));
        }
        if (snapshot.RecentEvents.Count > 0)
        {
            report.Add("");
            report.Add(Localization.T("EVENTOS RECIENTES:"));
            report.AddRange(snapshot.RecentEvents.Select(Localization.T));
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
        var recentLookup = GetRecentSummaries();
        var diagnosis = _diagnosisCacheSampleCount == _samples && _diagnosisCache is not null
            ? _diagnosisCache
            : _diagnosisCache = Diagnose(lookup, recentLookup);
        _diagnosisCacheSampleCount = _samples;
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

    private IReadOnlyDictionary<string, StatSummary> GetRecentSummaries()
    {
        if (_recentSummaryCache.Count == _recentMeasurements.Count)
        {
            return _recentSummaryCache;
        }

        foreach (var item in _recentMeasurements)
        {
            _recentSummaryCache[item.Key] = LatencyStatistics.FromSamples(item.Value);
        }

        return _recentSummaryCache;
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
                Localization.T("Midiendo: todavía no hay muestras suficientes"),
                Localization.F("Se necesitan al menos 30 muestras (faltan {0}) para evaluar los resultados. Una conclusión más confiable requiere dejar correr el monitoreo durante varios minutos.", remaining),
                Localization.T("Mantené el monitoreo activo mientras experimentás el problema."),
                Array.Empty<DiagnosticFinding>());
        }

        var localProblems = new List<DiagnosticFinding>();
        var ispProblems = new List<DiagnosticFinding>();
        var warnings = new List<DiagnosticFinding>();
        var router = values["router"];

        AddRouterDiagnosis(router, Connection, localProblems, warnings);

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
                warnings.Add(Finding(Localization.F("{0}: la referencia ICMP a {1} presenta anomalías; esto no confirma problemas en el servidor ni mide el ping real de la partida.", gameTarget.GameProfileName, gameTarget.Address), "Esta es una referencia ICMP a una IP observada en una conexión TCP del proceso; puede ser un servicio auxiliar y no el servidor de la partida. No permite concluir que el juego tenga ese ping."));
            }
        }

        if (values.TryGetValue("isp", out var isp) && ispProblems.Count > 0 &&
            (HasFrequentSpikes(isp) || isp.LossPercent > 3 || isp.Jitter > 15))
        {
            ispProblems.Add(Finding(Localization.F("El primer salto del ISP también presenta anomalías (jitter {0} ms, pérdida {1}%).", isp.Jitter, isp.LossPercent), "Las anomalías aparecen en el primer salto identificado fuera de tu router, lo que puede apuntar al enlace o a la red del proveedor. La ruta detectada es orientativa y conviene compartir el registro con el ISP."));
        }

        if (HasFrequentSpikes(router) &&
            !HasFrequentSpikes(values["cloudflare-icmp"]) &&
            !HasFrequentSpikes(values["google-icmp"]) &&
            (!values.TryGetValue("isp", out isp) || !HasFrequentSpikes(isp)))
        {
            warnings.Add(Finding(Localization.T("Los picos del router no se repiten en destinos externos; apunta más a Wi-Fi o al router que al ISP."), "Los destinos externos no muestran los mismos picos, así que la evidencia se concentra en el tramo local. El sondeo ICMP puede recibir un tratamiento distinto al tráfico de otras aplicaciones."));
        }

        AddBaselineWarnings(recentValues, warnings);
        AddPeriodicWarnings(warnings);

        if (localProblems.Count > 0)
        {
            return new DiagnosticResult("PROBLEMA", Localization.T("Problema en la red local"), Localization.T("Se observan fallos hasta el router, antes de salir a Internet."),
                Localization.T("Repetí la prueba por Ethernet. Si desaparece, revisá el Wi-Fi; si persiste, revisá el router."), localProblems.Concat(ispProblems).Concat(warnings).ToArray());
        }

        if (ispProblems.Count > 0)
        {
            return new DiagnosticResult("PROBLEMA", Localization.T("Problema de conexión a Internet"), Localization.T("La red local parece estable, pero hay anomalías en conexiones externas."),
                Localization.T("Guardá el registro y compartilo con tu proveedor si el problema persiste."), ispProblems.Concat(warnings).ToArray());
        }

        if (warnings.Count > 0)
        {
            return new DiagnosticResult("ATENCION", Localization.T("Se detectaron anomalías aisladas"), Localization.T("No hay evidencia suficiente de un problema sostenido."),
                Localization.T("Dejá correr el monitoreo mientras ocurre el lag y repetí la prueba por Ethernet."), warnings);
        }

        return new DiagnosticResult("OK", Localization.T("Sin problemas relevantes en las mediciones"), Localization.T("No se observan pérdidas relevantes, jitter alto ni picos frecuentes."),
            "", Array.Empty<DiagnosticFinding>());
    }

    internal static void AddInternetDiagnosis(
        string name,
        string icmpKey,
        string tcpKey,
        IReadOnlyDictionary<string, StatSummary> values,
        ICollection<DiagnosticFinding> problems,
        ICollection<DiagnosticFinding> warnings)
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
            problems.Add(Finding(Localization.F("{0}: anomalías tanto en ICMP como en TCP/443 (jitter TCP {1} ms, fallos {2}%).", Localization.T(name), tcp.Jitter, tcp.LossPercent), "Dos tipos de sondeo independientes muestran anomalías hacia este destino. Esto refuerza que hay degradación en el trayecto, pero no localiza por sí solo el equipo o proveedor responsable."));
        else if (tcpProblem)
            problems.Add(Finding(Localization.F("{0}: la conexión TCP/443 presenta anomalías aunque ICMP no; es una referencia de tráfico real.", Localization.T(name)), "La conexión TCP al puerto 443 tarda o falla aunque ICMP no lo haga. Puede reflejar congestión o problemas de establecimiento TCP; esta prueba no mide una sesión HTTPS completa."));
        else if (icmpProblem)
            warnings.Add(Finding(Localization.F("{0}: ICMP muestra anomalías, pero TCP/443 está limpio; podría ser priorización o filtrado de ping.", Localization.T(name)), "La anomalía se ve en ICMP pero no en TCP/443. Algunos routers y destinos limitan o priorizan distinto ICMP, así que esto no necesariamente afecta al tráfico normal."));

        if (icmp.Maximum >= 200) warnings.Add(Finding(Localization.F("{0} ICMP: pico aislado de {1} ms.", Localization.T(name), icmp.Maximum), "Es una demora máxima puntual de ICMP; revisá si aparece repetida en P95/P99 y si también ocurre en TCP antes de considerarla un problema sostenido."));
        if (tcp.Maximum >= 250) warnings.Add(Finding(Localization.F("{0} TCP: conexión de hasta {1} ms.", Localization.T(name), tcp.Maximum), "Una conexión TCP tardó mucho en establecerse en un sondeo. Un máximo aislado no equivale a la latencia de cada paquete de una conexión ya establecida."));
        if (name.StartsWith("LoL", StringComparison.Ordinal) && icmp.LossPercent >= 100 && !tcpProblem)
            warnings.Add(Finding(Localization.T("El destino aproximado de LoL no responde a ICMP; es común que servidores de juego filtren ping."), "El destino puede bloquear o limitar ICMP. La falta de respuesta no demuestra pérdida de paquetes en la partida; esta IP es solo una referencia aproximada."));
    }

    private void AddBaselineWarnings(IReadOnlyDictionary<string, StatSummary> recentValues, ICollection<DiagnosticFinding> warnings)
    {
        if (_baseline is null || recentValues["router"].Samples < 30)
        {
            return;
        }

        CompareBaseline(Localization.T("Router (últimas muestras)"), recentValues["router"], _baseline.Router, warnings);
        CompareBaseline(Localization.T("Destino 1 (últimas 60 muestras)"), recentValues["cloudflare-icmp"], _baseline.Cloudflare, warnings);
        CompareBaseline(Localization.T("Destino 2 (últimas 60 muestras)"), recentValues["google-icmp"], _baseline.Google, warnings);

        var baselineRouter = _baseline.Router;
        var currentRouter = recentValues["router"];
        var jitterThreshold = Math.Max(baselineRouter.Jitter * 3, baselineRouter.Jitter + 5);
        if (currentRouter.Jitter >= jitterThreshold && currentRouter.Jitter > 3)
            warnings.Add(Finding(Localization.F("El jitter actual del router ({0} ms) supera ampliamente la línea base ({1} ms).", currentRouter.Jitter, baselineRouter.Jitter), "La variación reciente aumentó frente a las primeras muestras. Puede indicar que algo cambió durante la sesión; compará la hora con la gráfica y los eventos."));
    }

    private static void CompareBaseline(string name, StatSummary current, StatSummary baseline, ICollection<DiagnosticFinding> warnings)
    {
        if (baseline.Average > 0 && current.Average >= baseline.Average * 3 && current.Average - baseline.Average >= 5)
            warnings.Add(Finding(Localization.F("{0}: promedio actual ({1} ms) muy superior a la línea base ({2} ms).", name, current.Average, baseline.Average), "El promedio reciente empeoró claramente respecto del inicio de esta misma sesión. La comparación indica un cambio temporal, no identifica por sí sola su causa."));
    }

    private static string FormatDuration(TimeSpan duration)
    {
        var totalHours = (int)duration.TotalHours;
        return duration.TotalDays >= 1
            ? $"{(int)duration.TotalDays}d {duration.Hours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{totalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private void AddPeriodicWarnings(ICollection<DiagnosticFinding> warnings)
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
                warnings.Add(Finding(Localization.F("{0}: anomalías periódicas cada ~{1} s; revisá tareas programadas, sincronización o escaneos DFS del Wi-Fi.", name, average.ToString("F0", Localization.Culture)), "La repetición con intervalos regulares puede coincidir con tareas programadas, sincronización, tráfico de fondo o cambios de canal Wi-Fi. Contrastá las marcas de tiempo; no permite confirmar una causa concreta."));
            }
        }
    }

    internal sealed record RouterThresholds(
        double Average, double JitterProblem, double JitterWarning, int P95, int P99, int Maximum);

    internal static readonly RouterThresholds WiredRouterThresholds = new(20, 8, 5, 10, 30, 150);
    internal static readonly RouterThresholds WifiRouterThresholds = new(30, 12, 8, 20, 50, 200);

    internal static RouterThresholds RouterThresholdsFor(ConnectionInfo connection) =>
        connection.Type == "Wi-Fi" ? WifiRouterThresholds : WiredRouterThresholds;

    internal static void AddRouterDiagnosis(
        StatSummary router,
        ConnectionInfo connection,
        List<DiagnosticFinding> localProblems,
        List<DiagnosticFinding> warnings)
    {
        var limits = RouterThresholdsFor(connection);
        if (router.LossPercent >= 5) localProblems.Add(Finding(Localization.F("Router: pérdida de paquetes ({0}/{1}, {2}%).", router.Lost, router.Samples, router.LossPercent), "La pérdida ya aparece en el salto entre el equipo y el router. Puede deberse a señal Wi-Fi/interferencias, adaptador de red, cable o router; por sí sola no identifica cuál."));
        if (router.Average > limits.Average) localProblems.Add(Finding(Localization.F("Router: latencia media alta ({0} ms).", router.Average), "La latencia elevada hacia el router ocurre dentro de la red local. Revisá Wi-Fi, carga del router y enlace Ethernet; no apunta por sí sola a un problema del proveedor."));
        if (router.Jitter > limits.JitterProblem) localProblems.Add(Finding(Localization.F("Router: jitter alto ({0} ms).", router.Jitter), "Las respuestas al router varían mucho entre sondeos, lo que puede sentirse como microcortes. Interferencias Wi-Fi, congestión local o carga del router son causas posibles."));
        if (HasFrequentSpikes(router)) localProblems.Add(Finding(Localization.T("Router: se registraron picos frecuentes de latencia."), "Los picos repetidos ya están presentes en el primer salto local. Compará Ethernet con Wi-Fi y revisá si hay tráfico o tareas que coinciden con esos momentos."));
        if (router.Maximum >= limits.Maximum) warnings.Add(Finding(Localization.F("Router: pico máximo aislado de {0} ms.", router.Maximum), "Se observó una respuesta muy lenta aislada, pero un máximo puntual no demuestra un problema sostenido. Contrastalo con P95, jitter y la gráfica."));
        if (router.P95 > limits.P95) warnings.Add(Finding(Localization.F("Router: P95 de {0} ms.", router.P95), "Al menos el 5% de las respuestas al router fue igual o más lenta. Puede señalar variabilidad local aunque el promedio sea bajo."));
        if (router.P99 > limits.P99) warnings.Add(Finding(Localization.F("Router: P99 de {0} ms.", router.P99), "La cola más lenta de las respuestas al router presenta demoras. Mirá la frecuencia y el momento de los picos antes de atribuirlos a un problema continuo."));
        if (router.Jitter > limits.JitterWarning && router.Jitter <= limits.JitterProblem) warnings.Add(Finding(Localization.F("Router: jitter de {0} ms, por encima de lo deseable.", router.Jitter), "Hay variación moderada entre respuestas locales. Si coincide con el lag, probá Ethernet para distinguir Wi-Fi de otros factores."));
        if (router.LossPercent > 0.5 && router.LossPercent < 5) warnings.Add(Finding(Localization.F("Router: pérdida de paquetes de {0}%.", router.LossPercent), "Algunas sondas al router no respondieron. Una cantidad baja puede ser transitoria; si persiste, revisá señal, cable y adaptador."));
        if (connection.Type == "Wi-Fi" && connection.WifiSignal is < 50)
            warnings.Add(Finding(Localization.F("Señal Wi-Fi baja ({0}%); podría causar picos.", connection.WifiSignal), "Una señal débil puede provocar retransmisiones, variaciones y pérdida en la red local. Acercate al punto de acceso o compará con una conexión Ethernet."));
    }

    private static DiagnosticFinding Finding(string text, string explanation) =>
        new(text, Localization.T(explanation));

    internal static bool HasFrequentSpikes(StatSummary stats)
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
        var connections = ReadEstablishedConnections()
            .Where(connection => !IsPrivateAddress(connection.Address))
            .ToList();
        if (connections.Count == 0)
        {
            return Array.Empty<GameObservedEndpoint>();
        }

        var processNames = new Dictionary<int, string>();
        foreach (var pid in connections.Select(connection => connection.Pid).Distinct())
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                processNames[pid] = process.ProcessName;
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
            {
                Trace.TraceWarning($"No se pudo inspeccionar un proceso durante la detección de juegos: {exception.Message}");
            }
        }

        return MatchGameEndpoints(connections, processNames, profiles);
    }

    internal sealed record TcpConnection(int Pid, IPAddress Address, int Port);

    private const int TcpTableOwnerPidAll = 5;
    private const uint TcpStateEstablished = 5;

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);

    internal static IEnumerable<TcpConnection> ReadEstablishedConnections()
    {
        foreach (var connection in ReadTcpTable(AddressFamily.InterNetwork, 24))
        {
            yield return connection;
        }

        foreach (var connection in ReadTcpTable(AddressFamily.InterNetworkV6, 56))
        {
            yield return connection;
        }
    }

    private static List<TcpConnection> ReadTcpTable(AddressFamily family, int rowSize)
    {
        var familyValue = family == AddressFamily.InterNetwork ? 2 : 23;
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, familyValue, TcpTableOwnerPidAll, 0);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var result = GetExtendedTcpTable(buffer, ref size, false, familyValue, TcpTableOwnerPidAll, 0);
                if (result == 122)
                {
                    continue;
                }

                if (result != 0)
                {
                    throw new System.ComponentModel.Win32Exception((int)result, "No se pudo consultar la tabla de conexiones TCP.");
                }

                return ParseTcpTable(buffer, family, rowSize);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        throw new IOException("La tabla de conexiones TCP cambió durante la consulta.");
    }

    internal static List<TcpConnection> ParseTcpTable(IntPtr buffer, AddressFamily family, int rowSize)
    {
        var connections = new List<TcpConnection>();
        var count = Marshal.ReadInt32(buffer);
        for (var index = 0; index < count; index++)
        {
            var row = buffer + 4 + index * rowSize;
            IPAddress address;
            uint state;
            int portRaw;
            int pid;
            if (family == AddressFamily.InterNetwork)
            {
                state = (uint)Marshal.ReadInt32(row, 0);
                address = new IPAddress((uint)Marshal.ReadInt32(row, 12));
                portRaw = Marshal.ReadInt32(row, 16);
                pid = Marshal.ReadInt32(row, 20);
            }
            else
            {
                var addressBytes = new byte[16];
                Marshal.Copy(row + 24, addressBytes, 0, 16);
                address = new IPAddress(addressBytes, Marshal.ReadInt32(row, 40));
                portRaw = Marshal.ReadInt32(row, 44);
                state = (uint)Marshal.ReadInt32(row, 48);
                pid = Marshal.ReadInt32(row, 52);
            }

            if (state != TcpStateEstablished)
            {
                continue;
            }

            var port = ((portRaw & 0xFF) << 8) | ((portRaw >> 8) & 0xFF);
            connections.Add(new TcpConnection(pid, address, port));
        }

        return connections;
    }

    internal static IReadOnlyList<GameObservedEndpoint> ParseGameEndpoints(
        string output,
        IReadOnlyDictionary<int, string> processNames,
        IReadOnlyList<GameMonitoringProfile> profiles)
    {
        var connections = new List<TcpConnection>();
        foreach (var line in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Regex.Match(line, @"^\s*TCP\s+\S+\s+(?<remote>\S+)\s+ESTABLISHED\s+(?<pid>\d+)", RegexOptions.IgnoreCase);
            if (!match.Success || !int.TryParse(match.Groups["pid"].Value, out var pid))
            {
                continue;
            }

            var endpointText = match.Groups["remote"].Value;
            var separator = endpointText.LastIndexOf(':');
            var addressText = separator <= 0 ? string.Empty : endpointText[..separator].Trim('[', ']');
            if (separator <= 0 || !int.TryParse(endpointText[(separator + 1)..], out var port) ||
                !IPAddress.TryParse(addressText, out var address))
            {
                continue;
            }

            connections.Add(new TcpConnection(pid, address, port));
        }

        return MatchGameEndpoints(connections, processNames, profiles);
    }

    private static IReadOnlyList<GameObservedEndpoint> MatchGameEndpoints(
        IEnumerable<TcpConnection> connections,
        IReadOnlyDictionary<int, string> processNames,
        IReadOnlyList<GameMonitoringProfile> profiles)
    {
        var results = new List<GameObservedEndpoint>();
        foreach (var connection in connections)
        {
            if (!processNames.TryGetValue(connection.Pid, out var processName) || IsPrivateAddress(connection.Address))
            {
                continue;
            }

            var address = connection.Address;
            var port = connection.Port;

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

    internal static bool IsPrivateAddress(IPAddress address)
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

    private static (string Router, ConnectionInfo Connection) DetectConnection(string? selectedInterfaceId, string destinationHost)
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
            var destination = BitConverter.ToUInt32(ResolveIPv4(destinationHost).GetAddressBytes());
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
        var wifi = isWifi ? ReadWifiDetails(network.Id) : (Ssid: null, Signal: (int?)null);
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

    private static (string? Ssid, int? Signal) ReadWifiDetails(string interfaceId)
    {
        if (Guid.TryParse(interfaceId, out var interfaceGuid))
        {
            try
            {
                var native = ReadWifiDetailsNative(interfaceGuid);
                if (native.Signal.HasValue || !string.IsNullOrWhiteSpace(native.Ssid))
                {
                    return native;
                }
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or
                                                  System.ComponentModel.Win32Exception or ArgumentException)
            {
                Trace.TraceWarning($"La API nativa de Wi-Fi no está disponible; se usa netsh: {exception.Message}");
            }
        }

        return ParseWifiDetails(RunNetsh(), interfaceId);
    }

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceGuid, int opCode,
        IntPtr reserved, out int dataSize, out IntPtr data, IntPtr opcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);

    private const int WlanIntfOpcodeCurrentConnection = 7;
    private const int WlanAssociationOffset = 520;
    private const int WlanSignalQualityOffset = WlanAssociationOffset + 56;

    private static (string? Ssid, int? Signal) ReadWifiDetailsNative(Guid interfaceGuid)
    {
        var openResult = WlanOpenHandle(2, IntPtr.Zero, out _, out var handle);
        if (openResult != 0)
        {
            throw new System.ComponentModel.Win32Exception((int)openResult);
        }

        try
        {
            var result = WlanQueryInterface(handle, ref interfaceGuid, WlanIntfOpcodeCurrentConnection,
                IntPtr.Zero, out var size, out var data, IntPtr.Zero);
            if (result != 0)
            {
                return (null, null);
            }

            try
            {
                if (size < WlanSignalQualityOffset + 4)
                {
                    return (null, null);
                }

                var ssidLength = Math.Clamp(Marshal.ReadInt32(data, WlanAssociationOffset), 0, 32);
                var ssidBytes = new byte[ssidLength];
                Marshal.Copy(data + WlanAssociationOffset + 4, ssidBytes, 0, ssidLength);
                var signal = Marshal.ReadInt32(data, WlanSignalQualityOffset);
                return (ssidLength == 0 ? null : System.Text.Encoding.UTF8.GetString(ssidBytes),
                    signal is >= 0 and <= 100 ? signal : null);
            }
            finally
            {
                WlanFreeMemory(data);
            }
        }
        finally
        {
            _ = WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    internal static (string? Ssid, int? Signal) ParseWifiDetails(string output, string? interfaceId = null)
    {
        var scope = output;
        var guidMatches = Regex.Matches(output, @"(?im)^\s*GUID\s*:\s*(?<guid>\S+)\s*$");
        if (guidMatches.Count > 1 && !string.IsNullOrWhiteSpace(interfaceId))
        {
            for (var index = 0; index < guidMatches.Count; index++)
            {
                if (!string.Equals(guidMatches[index].Groups["guid"].Value.Trim('{', '}'),
                        interfaceId.Trim('{', '}'), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var start = guidMatches[index].Index;
                var end = index + 1 < guidMatches.Count ? guidMatches[index + 1].Index : output.Length;
                scope = output[start..end];
                break;
            }
        }

        var ssidMatch = Regex.Match(scope, @"(?im)^\s*SSID\s*:\s*(?<ssid>.+?)\s*$");
        var signalMatch = Regex.Match(scope, @"(?m)^\s*[^:\r\n]+:\s*(?<value>\d{1,3})\s*%\s*$");
        var ssid = ssidMatch.Success ? ssidMatch.Groups["ssid"].Value.Trim() : null;
        int? signal = signalMatch.Success && int.TryParse(signalMatch.Groups["value"].Value, out var value) ? value : null;
        return (ssid, signal);
    }

    private static IPAddress ResolveIPv4(string host)
    {
        if (IPAddress.TryParse(host, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            return parsed;
        }

        try
        {
            var resolved = Dns.GetHostAddresses(host).FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork);
            if (resolved is not null)
            {
                return resolved;
            }
        }
        catch (SocketException)
        {
        }

        return IPAddress.Parse("1.1.1.1");
    }

    private static string RunNetsh()
    {
        try
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
                TryKill(process);
                return string.Empty;
            }

            return outputTask.GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            Trace.TraceWarning($"No se pudo consultar los detalles Wi-Fi: {exception.Message}");
            return string.Empty;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }

    private const int IspProbeFirstTtl = 2;
    private const int IspProbeLastTtl = 8;
    private const int IspProbeAttemptsPerHop = 2;

    private static async Task<string?> FindIspFirstHopAsync(string router, string destination, CancellationToken cancellationToken)
    {
        using var ping = new Ping();
        return await FindIspFirstHopAsync(router, async (ttl, token) =>
        {
            try
            {
                var reply = await ping.SendPingAsync(destination, TimeSpan.FromMilliseconds(800),
                    new byte[32], new PingOptions(ttl, true), token);
                return (reply.Status, reply.Address);
            }
            catch (PingException)
            {
                return (IPStatus.Unknown, null);
            }
        }, cancellationToken);
    }

    internal static async Task<string?> FindIspFirstHopAsync(
        string router,
        Func<int, CancellationToken, Task<(IPStatus Status, IPAddress? Address)>> probe,
        CancellationToken cancellationToken)
    {
        for (var ttl = IspProbeFirstTtl; ttl <= IspProbeLastTtl; ttl++)
        {
            for (var attempt = 0; attempt < IspProbeAttemptsPerHop; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (status, address) = await probe(ttl, cancellationToken);
                if (status == IPStatus.Success)
                {
                    return null;
                }

                if (status != IPStatus.TtlExpired || address is null)
                {
                    continue;
                }

                if (string.Equals(address.ToString(), router, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                return address.ToString();
            }
        }

        return null;
    }
}
