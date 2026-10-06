using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiagnosticoLag;

internal sealed record GameMonitoringProfile(string Id, string Name, IReadOnlyList<string> ProcessNames, bool Enabled)
{
    public override string ToString() => Name;

    public static GameMonitoringProfile LeagueOfLegends { get; } =
        new("league-of-legends", "League of Legends", ["League of Legends"], false);
}

internal sealed record DiagnosticSettings(
    string CloudflareAddress,
    string GoogleAddress,
    int SampleIntervalSeconds,
    string? NetworkInterfaceId)
{
    public IReadOnlyList<GameMonitoringProfile> GameProfiles { get; init; } = [GameMonitoringProfile.LeagueOfLegends];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool DetectLeague { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IncludeLeagueInDiagnosis { get; init; }
    public string LogDirectory { get; init; } = DefaultOutputDirectory;
    public string CsvDirectory { get; init; } = DefaultOutputDirectory;
    public string Language { get; init; } = "en";

    public static string DefaultOutputDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiagnosticoLag", "sesiones");

    public static DiagnosticSettings Default { get; } = new("1.1.1.1", "8.8.8.8", 1, null)
    {
        Language = "en"
    };

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiagnosticoLag", "configuracion.json");

    public static DiagnosticSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            var initialSettings = Default with { Language = DetectInitialLanguage() };
            initialSettings.Save();
            return initialSettings;
        }

        try
        {
            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<DiagnosticSettings>(json)
                ?? throw new InvalidDataException(Localization.T("El archivo de configuración está vacío."));
            using var document = JsonDocument.Parse(json);
            var hasSavedLanguage = document.RootElement.TryGetProperty(nameof(Language), out _);
            var hasValidLanguage = settings.Language is "es" or "en";
            if (!document.RootElement.TryGetProperty(nameof(GameProfiles), out _))
            {
                var detectLeague = document.RootElement.TryGetProperty(nameof(DetectLeague), out var detectValue) &&
                                   detectValue.ValueKind == JsonValueKind.True;
                var legacyDiagnosis = document.RootElement.TryGetProperty(nameof(IncludeLeagueInDiagnosis), out var diagnosisValue) &&
                                      diagnosisValue.ValueKind == JsonValueKind.True;
                settings = settings with
                {
                    GameProfiles =
                    [
                        GameProfileSettingsWithEnabled(GameMonitoringProfile.LeagueOfLegends, detectLeague || legacyDiagnosis)
                    ],
                    DetectLeague = false,
                    IncludeLeagueInDiagnosis = false
                };
            }

            if (!hasValidLanguage)
            {
                settings = settings with { Language = hasSavedLanguage ? "en" : DetectInitialLanguage() };
            }

            Validate(settings);
            if (!hasSavedLanguage || !hasValidLanguage)
            {
                settings.Save();
            }

            return settings;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(Localization.F("La configuración guardada no tiene un formato válido: {0}", SettingsPath), exception);
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
            throw new InvalidDataException(Localization.T("El intervalo de medición debe estar entre 1 y 10 segundos."));
        }

        if (!IsValidHost(settings.CloudflareAddress) || !IsValidHost(settings.GoogleAddress))
        {
            throw new InvalidDataException(Localization.T("Ingresá una dirección IP o un nombre de host válido para cada destino."));
        }

        if (settings.GameProfiles is null)
        {
            throw new InvalidDataException(Localization.T("La lista de perfiles de juegos no es válida."));
        }

        if (settings.GameProfiles.Count > 8)
        {
            throw new InvalidDataException(Localization.T("Se pueden configurar hasta 8 perfiles de juegos."));
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in settings.GameProfiles)
        {
            if (profile is null || string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.Name) ||
                profile.Name.Length > 80 || !ids.Add(profile.Id) ||
                profile.ProcessNames is null || profile.ProcessNames.Count is 0 or > 8 ||
                profile.ProcessNames.Any(processName => string.IsNullOrWhiteSpace(processName) || processName.Length > 128))
            {
                throw new InvalidDataException(Localization.T("Cada perfil debe tener un nombre, un identificador único y de 1 a 8 nombres de proceso válidos."));
            }
        }

        ValidateDirectory(settings.LogDirectory, "registro");
        ValidateDirectory(settings.CsvDirectory, "CSV");
    }

    private static GameMonitoringProfile GameProfileSettingsWithEnabled(GameMonitoringProfile profile, bool enabled) =>
        profile with { Enabled = enabled };

    private static string DetectInitialLanguage()
    {
        try
        {
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("es", StringComparison.OrdinalIgnoreCase)
                ? "es"
                : "en";
        }
        catch (Exception exception)
        {
            Trace.TraceWarning($"No se pudo detectar el idioma del sistema; se usará inglés: {exception}");
            return "en";
        }
    }

    private static void ValidateDirectory(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidDataException(Localization.F("Seleccioná una carpeta válida para guardar el archivo de {0}.", Localization.T(label)));
        }

        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                throw new InvalidDataException(Localization.F("La carpeta para el archivo de {0} debe ser una ruta absoluta.", Localization.T(label)));
            }

            _ = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException(Localization.F("La carpeta para el archivo de {0} no es válida: {1}", Localization.T(label), exception.Message), exception);
        }
    }

    private static bool IsValidHost(string value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               (IPAddress.TryParse(value.Trim(), out _) || Uri.CheckHostName(value.Trim()) != UriHostNameType.Unknown);
    }
}
