using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace DiagnosticoLag;

internal static class Localization
{
    private static readonly IReadOnlyDictionary<string, string> Spanish = Load("es");
    private static readonly IReadOnlyDictionary<string, string> English = Load("en");
    private static readonly IReadOnlyDictionary<string, string> EnglishToSpanish = English
        .GroupBy(pair => pair.Value, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.Ordinal);

    public static string Language { get; private set; } = "es";
    public static CultureInfo Culture => Language == "en" ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("es-AR");

    public static void SetLanguage(string language)
    {
        Language = language == "en" ? "en" : "es";
        CultureInfo.CurrentCulture = Culture;
        CultureInfo.CurrentUICulture = Culture;
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
    }

    public static string T(string spanishText)
    {
        var translations = Language == "en" ? English : Spanish;
        if (translations.TryGetValue(spanishText, out var translated))
        {
            return translated;
        }

        if (Language == "es")
        {
            if (EnglishToSpanish.TryGetValue(spanishText, out var source))
            {
                return source;
            }
        }

        return spanishText;
    }

    public static string F(string spanishFormat, params object?[] arguments) =>
        string.Format(Culture, T(spanishFormat), arguments);

    private static IReadOnlyDictionary<string, string> Load(string language)
    {
        var resourceName = $"DiagnosticoLag.Translations.{language}.json";
        using var stream = typeof(Localization).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"No se encontró el archivo de traducción requerido: {resourceName}");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidDataException($"El archivo de traducción está vacío: {resourceName}");
    }
}
