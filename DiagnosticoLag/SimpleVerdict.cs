namespace DiagnosticoLag;

internal enum VerdictLevel { Measuring, Good, Unstable, Bad }

internal sealed record SimpleVerdict(VerdictLevel Level, string Headline, string Detail)
{
    internal static SimpleVerdict From(MonitorSnapshot snapshot)
    {
        var rated = snapshot.Targets
            .Select(item => (item.Target, Level: ReportExplainer.Rate(item.Target, item.Statistics)))
            .Where(item => item.Level != QualityLevel.Insufficient)
            .ToArray();
        if (rated.Length == 0)
        {
            return new SimpleVerdict(VerdictLevel.Measuring,
                Localization.T("Midiendo…"),
                Localization.T("Todavía no hay datos suficientes para opinar; dejá correr el análisis un rato."));
        }

        var problems = rated.Where(item => item.Level is QualityLevel.Regular or QualityLevel.Bad).ToArray();
        if (problems.Length == 0)
        {
            return new SimpleVerdict(VerdictLevel.Good,
                Localization.T("Tu conexión está bien para jugar"),
                Localization.T("No se detectaron problemas en ningún punto medido."));
        }

        var level = problems.Any(item => item.Level == QualityLevel.Bad) ? VerdictLevel.Bad : VerdictLevel.Unstable;
        var headline = level == VerdictLevel.Bad
            ? Localization.T("Tu conexión tiene problemas")
            : Localization.T("Tu conexión está inestable");
        var detail = problems.Any(item => item.Target.Key == "router")
            ? Localization.T("El problema parece estar en tu red local (Wi-Fi o router).")
            : problems.Any(item => item.Target.Key == "isp")
                ? Localization.T("El problema parece estar en tu proveedor de Internet.")
                : Localization.T("El problema aparece lejos de tu casa, en el camino a los servidores.");
        return new SimpleVerdict(level, headline, detail);
    }
}

internal sealed record LagIncident(DateTime Start, DateTime End, int Losses, int Spikes, int Peak, IReadOnlyList<LagEvent> Events)
{
    internal const int MaximumGapSeconds = 10;

    internal IReadOnlyList<string> TargetNames => Events.Select(item => item.TargetName).Distinct().ToArray();

    internal static IReadOnlyList<LagIncident> Group(IReadOnlyList<LagEvent>? events)
    {
        var result = new List<LagIncident>();
        if (events is null || events.Count == 0)
        {
            return result;
        }

        var current = new List<LagEvent>();
        foreach (var item in events.OrderBy(item => item.Time))
        {
            if (current.Count > 0 && (item.Time - current[^1].Time).TotalSeconds > MaximumGapSeconds)
            {
                result.Add(Create(current));
                current = new List<LagEvent>();
            }

            current.Add(item);
        }

        result.Add(Create(current));
        return result;
    }

    private static LagIncident Create(List<LagEvent> items) =>
        new(items[0].Time, items[^1].Time,
            items.Count(item => item.Value < 0),
            items.Count(item => item.Value >= 0),
            items.Select(item => item.Value).DefaultIfEmpty(0).Max(),
            items);

    internal string Describe()
    {
        var range = Start == End || Start.ToString("HH:mm:ss") == End.ToString("HH:mm:ss")
            ? $"[{Start:HH:mm:ss}]"
            : $"[{Start:HH:mm:ss} - {End:HH:mm:ss}]";
        var what = new List<string>();
        if (Losses > 0)
        {
            what.Add(Localization.F("{0} pérdidas", Losses));
        }

        if (Spikes > 0)
        {
            what.Add(Localization.F("{0} picos (máx. {1} ms)", Spikes, Peak));
        }

        return $"{range} {string.Join(Localization.T(" y "), what)}: {Explain()}";
    }

    private string Explain()
    {
        var keys = Events.Select(item => item.TargetKey).Distinct().ToArray();
        var names = string.Join(", ", TargetNames.Select(Localization.T));
        if (keys.Contains("router"))
        {
            return Localization.F("afectó a tu red local ({0}); suele ser Wi-Fi, interferencia o el router.", names);
        }

        if (keys.Contains("isp"))
        {
            return Localization.F("afectó a tu proveedor ({0}); puede ser un corte o congestión del ISP.", names);
        }

        return keys.Length >= 2
            ? Localization.F("afectó a varios destinos a la vez ({0}); apunta a un problema general de la conexión.", names)
            : Localization.F("solo afectó a {0}; probablemente fue algo puntual de ese servidor, no de tu conexión.", names);
    }
}
