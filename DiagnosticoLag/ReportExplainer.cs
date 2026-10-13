namespace DiagnosticoLag;

internal enum QualityLevel { Insufficient, Good, Acceptable, Regular, Bad }

internal static class ReportExplainer
{
    internal const int MinimumSamples = 30;

    internal static QualityLevel Rate(ProbeTarget target, StatSummary stats)
    {
        if (stats.Samples < MinimumSamples)
        {
            return QualityLevel.Insufficient;
        }

        var (acceptable, regular, bad) = target.Key switch
        {
            "router" => (10d, 30d, 80d),
            var key when key.EndsWith("-tcp", StringComparison.Ordinal) => (90d, 150d, 250d),
            _ => (60d, 100d, 150d)
        };

        if (stats.LossPercent > 3 || stats.Average > bad || stats.Jitter > 30)
        {
            return QualityLevel.Bad;
        }

        if (stats.LossPercent > 1 || stats.Average > regular || stats.Jitter > 15 || DiagnosticSession.HasFrequentSpikes(stats))
        {
            return QualityLevel.Regular;
        }

        return stats.Average > acceptable || stats.Jitter > 8 || stats.Spikes120 > 0
            ? QualityLevel.Acceptable
            : QualityLevel.Good;
    }

    internal static string LevelName(QualityLevel level) => Localization.T(level switch
    {
        QualityLevel.Good => "Buena",
        QualityLevel.Acceptable => "Aceptable",
        QualityLevel.Regular => "Regular",
        QualityLevel.Bad => "Mala",
        _ => "Sin datos suficientes"
    });

    internal static IReadOnlyList<string> Build(MonitorSnapshot snapshot, ConnectionHealth health)
    {
        var lines = new List<string>
        {
            "",
            Localization.T("EXPLICACIÓN DETALLADA DEL ANÁLISIS:"),
            "",
            Localization.F("RESUMEN RÁPIDO: {0}. {1}", SimpleVerdict.From(snapshot).Headline, SimpleVerdict.From(snapshot).Detail),
            "",
            Localization.T("1) QUÉ SE MIDIÓ"),
            Localization.T("  Cada segundo (o el intervalo configurado) la aplicación envía una consulta de red mínima a varios puntos y mide cuánto tarda la respuesta. Así se puede ver en qué tramo del camino aparece una demora o una pérdida."),
            Localization.T("  - Router: el equipo de tu casa. Si falla acá, el problema está en tu red local (Wi-Fi, cable o router)."),
            Localization.T("  - ISP (primer salto): el primer equipo de tu proveedor de Internet. Si el router está bien pero acá hay problemas, apunta al proveedor."),
            Localization.T("  - Destinos de Internet (ICMP y TCP/443): servidores públicos usados como referencia del estado general de Internet."),
            Localization.T("  - Juegos: referencia aproximada hacia una dirección que usa el proceso del juego; no es el ping real de la partida."),
            "",
            Localization.T("2) CÓMO LEER LOS NÚMEROS"),
            Localization.T("  - Promedio y mediana: la demora típica en milisegundos (ms). Menos es mejor."),
            Localization.T("  - P95 y P99: la demora que se supera solo el 5% o el 1% de las veces; muestran los peores momentos sin contar un único pico."),
            Localization.T("  - Jitter: cuánto varía la demora de una muestra a otra. Un jitter alto se siente como tirones aunque el promedio sea bueno."),
            Localization.T("  - Picos: muestras muy lentas (80-119 ms y de 120 ms o más). Pocos picos aislados son normales; muchos seguidos se sienten como lag."),
            Localization.T("  - Pérdida o fallos: consultas que no recibieron respuesta. Más de 1% ya puede notarse; más de 3% suele causar cortes o rubber-banding."),
            "",
            Localization.T("3) QUÉ SE VIO EN CADA PUNTO")
        };

        foreach (var item in snapshot.Targets)
        {
            lines.AddRange(DescribeTarget(item.Target, item.Statistics));
        }

        lines.Add("");
        lines.Add(Localization.T("4) LÍNEA DE TIEMPO DE INCIDENTES"));
        var incidents = LagIncident.Group(snapshot.Events);
        if (incidents.Count == 0)
        {
            lines.Add(Localization.T("  No hubo pérdidas ni picos de 120 ms o más en los destinos medidos con ICMP."));
        }
        else
        {
            lines.Add(Localization.T("  Un incidente es un grupo de pérdidas o picos seguidos; se indica a qué puntos afectó y qué puede significar."));
            lines.AddRange(incidents.Take(20).Select(item => "  - " + item.Describe()));
            if (incidents.Count > 20)
            {
                lines.Add(Localization.F("  … y {0} incidentes más.", incidents.Count - 20));
            }
        }

        lines.Add("");
        lines.Add(Localization.T("5) CONCLUSIÓN Y QUÉ HACER"));
        lines.AddRange(Conclude(snapshot, health));
        return lines;
    }

    internal static IReadOnlyList<string> DescribeTarget(ProbeTarget target, StatSummary s)
    {
        var level = Rate(target, s);
        var name = Localization.T(target.Name);
        var lines = new List<string> { $"  - {name}: {LevelName(level)}" };
        if (level == QualityLevel.Insufficient)
        {
            lines.Add(Localization.F("    Solo hay {0} muestras; se necesitan al menos {1} para opinar con confianza.", s.Samples, MinimumSamples));
            return lines;
        }

        lines.Add(Localization.F("    Respondió en {0} ms en promedio (mediana {1} ms); en el 95% de los casos tardó {2} ms o menos y lo peor fue {3} ms.",
            s.Average, s.Median, s.P95, s.Maximum));

        var variation = s.Jitter > 15
            ? Localization.F("La variación entre muestras fue alta (jitter {0} ms), lo que se siente como tirones.", s.Jitter)
            : s.Jitter > 8
                ? Localization.F("La variación entre muestras fue moderada (jitter {0} ms).", s.Jitter)
                : Localization.F("La variación entre muestras fue baja (jitter {0} ms).", s.Jitter);
        var spikes = s.Spikes120 + s.Spikes80 == 0
            ? Localization.T("No hubo picos de demora.")
            : Localization.F("Hubo {0} picos de 80-119 ms y {1} de 120 ms o más.", s.Spikes80, s.Spikes120);
        lines.Add($"    {variation} {spikes}");

        var lossWord = target.Type == ProbeType.Tcp ? Localization.T("conexiones fallidas") : Localization.T("respuestas perdidas");
        lines.Add(s.Lost == 0
            ? Localization.T("    No se perdió ninguna respuesta.")
            : Localization.F("    Se perdieron {0} de {1} ({2}%): {3}.", s.Lost, s.Samples, s.LossPercent, lossWord));

        lines.Add("    " + Localization.T(Meaning(target, level)));
        return lines;
    }

    private static string Meaning(ProbeTarget target, QualityLevel level)
    {
        var isLocal = target.Key == "router";
        var isIsp = target.Key == "isp";
        var isGame = target.GameProfileId is not null;
        if (level is QualityLevel.Good)
        {
            return "Qué significa: este tramo funcionó bien durante el análisis.";
        }

        if (isLocal)
        {
            return level == QualityLevel.Acceptable
                ? "Qué significa: tu red local anduvo bien, con alguna variación leve que probablemente no moleste."
                : "Qué significa: hay problemas entre tu equipo y el router. Probá por cable Ethernet, acercate al router o reiniciá el router; si usás Wi-Fi, evitá interferencias y probá la banda de 5 GHz.";
        }

        if (isIsp)
        {
            return level == QualityLevel.Acceptable
                ? "Qué significa: el primer salto del proveedor anduvo bien, con alguna variación leve."
                : "Qué significa: el tramo hacia tu proveedor presenta demoras o pérdidas; si el router está bien, el origen puede estar en la línea o en la red del ISP.";
        }

        if (isGame)
        {
            return "Qué significa: es solo una referencia hacia una dirección auxiliar del juego; si el resto está bien, no alcanza para culpar al juego o a su servidor.";
        }

        return level == QualityLevel.Acceptable
            ? "Qué significa: el camino a este destino anduvo bien, con alguna variación leve."
            : "Qué significa: el camino a este destino mostró degradación; compará con el router y el ISP para ubicar dónde empieza.";
    }

    private static IReadOnlyList<string> Conclude(MonitorSnapshot snapshot, ConnectionHealth health)
    {
        var lines = new List<string>
        {
            Localization.F("  Salud general de la conexión: {0}/100 ({1}).", health.Score, Localization.T(health.Status)),
            "  " + Localization.T(snapshot.Diagnosis.Title) + ". " + Localization.T(snapshot.Diagnosis.Explanation)
        };

        var rated = snapshot.Targets
            .Select(item => (item.Target, Level: Rate(item.Target, item.Statistics)))
            .ToArray();
        var problems = rated.Where(item => item.Level is QualityLevel.Regular or QualityLevel.Bad).ToArray();
        if (problems.Length == 0)
        {
            lines.Add(Localization.T("  Ningún punto medido mostró problemas relevantes: si notaste lag, probablemente no vino de la red hasta estos puntos (revisá el equipo, el juego o el servidor)."));
        }
        else
        {
            lines.Add(Localization.F("  Puntos con problemas: {0}.", string.Join(", ", problems.Select(item => Localization.T(item.Target.Name)))));
            var routerBad = problems.Any(item => item.Target.Key == "router");
            var ispBad = problems.Any(item => item.Target.Key == "isp");
            lines.Add(routerBad
                ? Localization.T("  El problema empieza en tu red local: es lo primero que conviene arreglar, porque afecta a todo lo que sigue.")
                : ispBad
                    ? Localization.T("  Tu red local está bien pero hay problemas al salir: apunta a tu proveedor de Internet.")
                    : Localization.T("  Tu red local y tu proveedor están bien; los problemas aparecen más lejos, en el camino a los destinos."));
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Diagnosis.Recommendation))
        {
            lines.Add(Localization.F("  Qué hacer: {0}", Localization.T(snapshot.Diagnosis.Recommendation)));
        }

        if (snapshot.Duration < TimeSpan.FromMinutes(5))
        {
            lines.Add(Localization.T("  Aviso: el análisis duró pocos minutos; para conclusiones más confiables repetilo durante una sesión de juego completa."));
        }

        return lines;
    }
}
