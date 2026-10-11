using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DiagnosticoLag;

internal sealed record SessionMetadata(
    string ApplicationName,
    string ApplicationVersion,
    int SchemaVersion,
    string SessionId,
    DateTime StartedAt,
    string OperatingSystem,
    string Architecture,
    string Culture,
    string ConnectionType,
    int SampleIntervalMilliseconds,
    string CompletionStatus)
{
    public static SessionMetadata Create(
        DateTime startedAt,
        string connectionType,
        int sampleIntervalMilliseconds)
    {
        var assembly = typeof(SessionMetadata).Assembly;
        var applicationName = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product
            ?? assembly.GetName().Name
            ?? "DiagnosticoLag";
        var applicationVersion = assembly.GetName().Version?.ToString(3) ?? "desconocida";

        return new SessionMetadata(
            applicationName,
            applicationVersion,
            2,
            Guid.NewGuid().ToString("D"),
            startedAt,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            CultureInfo.CurrentCulture.Name,
            connectionType,
            sampleIntervalMilliseconds,
            "En curso");
    }

    public SessionMetadata WithStatus(string status) => this with { CompletionStatus = status };

    public IEnumerable<string> ToTextLines()
    {
        yield return $"{Localization.T("Aplicativo")}: {ApplicationName}";
        yield return $"{Localization.T("Versión")}: {ApplicationVersion}";
        yield return $"{Localization.T("Esquema de datos")}: {SchemaVersion}";
        yield return $"{Localization.T("ID de sesión")}: {SessionId}";
        yield return $"{Localization.T("Inicio")}: {StartedAt:O}";
        yield return $"{Localization.T("Sistema operativo")}: {OperatingSystem}";
        yield return $"{Localization.T("Arquitectura")}: {Architecture}";
        yield return $"{Localization.T("Cultura")}: {Culture}";
        yield return $"{Localization.T("Tipo de conexión")}: {ConnectionType}";
        yield return $"{Localization.T("Intervalo de muestreo")}: {SampleIntervalMilliseconds} {Localization.T("milisegundos")}";
        yield return $"{Localization.T("Estado")}: {Localization.T(CompletionStatus)}";
    }

    public IEnumerable<string> ToCsvLines()
    {
        yield return $"# application_name={ApplicationName}";
        yield return $"# application_version={ApplicationVersion}";
        yield return $"# schema_version={SchemaVersion}";
        yield return $"# session_id={SessionId}";
        yield return $"# started_at={StartedAt:O}";
        yield return $"# operating_system={OperatingSystem}";
        yield return $"# architecture={Architecture}";
        yield return $"# culture={Culture}";
        yield return $"# connection_type={ConnectionType}";
        yield return $"# sample_interval_milliseconds={SampleIntervalMilliseconds}";
        yield return $"# completion_status={Localization.T(CompletionStatus)}";
    }
}
