using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace DiagnosticoLag;

internal sealed record DiagnosticSettings(
    string CloudflareAddress,
    string GoogleAddress,
    int SampleIntervalSeconds,
    string? NetworkInterfaceId,
    bool IncludeLeagueInDiagnosis)
{
    public static DiagnosticSettings Default { get; } = new("1.1.1.1", "8.8.8.8", 1, null, false);

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiagnosticoLag", "configuracion.json");

    public static DiagnosticSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return Default;
        }

        try
        {
            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<DiagnosticSettings>(json)
                ?? throw new InvalidDataException("El archivo de configuración está vacío.");
            Validate(settings);
            return settings;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"La configuración guardada no tiene un formato válido: {SettingsPath}", exception);
        }
    }

    public void Save()
    {
        Validate(this);
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static IReadOnlyList<NetworkInterface> AvailableInterfaces()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.OperationalStatus == OperationalStatus.Up &&
                           item.GetIPProperties().GatewayAddresses.Any(gateway =>
                               gateway.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                               !gateway.Address.Equals(IPAddress.Any)))
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static void Validate(DiagnosticSettings settings)
    {
        if (settings.SampleIntervalSeconds is < 1 or > 10)
        {
            throw new InvalidDataException("El intervalo de medición debe estar entre 1 y 10 segundos.");
        }

        if (!IsValidHost(settings.CloudflareAddress) || !IsValidHost(settings.GoogleAddress))
        {
            throw new InvalidDataException("Ingresá una dirección IP o un nombre de host válido para cada destino.");
        }
    }

    private static bool IsValidHost(string value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               (IPAddress.TryParse(value.Trim(), out _) || Uri.CheckHostName(value.Trim()) != UriHostNameType.Unknown);
    }
}
