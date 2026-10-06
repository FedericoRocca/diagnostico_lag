using System.Globalization;
using System.Reflection;
using System.Text.Json;
using DiagnosticoLag;

namespace DiagnosticoLag.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void TranslationResourcesContainMatchingNonEmptyEntries()
    {
        var assembly = typeof(Localization).Assembly;
        var spanish = ReadResource(assembly, "DiagnosticoLag.Translations.es.json");
        var english = ReadResource(assembly, "DiagnosticoLag.Translations.en.json");

        Assert.NotEmpty(spanish);
        Assert.Equal(spanish.Keys.Order(), english.Keys.Order());
        Assert.All(spanish.Values, value => Assert.False(string.IsNullOrWhiteSpace(value)));
        Assert.All(english.Values, value => Assert.False(string.IsNullOrWhiteSpace(value)));
    }

    [Fact]
    public void SwitchesLanguagesAndCultureTogether()
    {
        Localization.SetLanguage("en");
        Assert.Equal("Settings", Localization.T("Configuración"));
        Assert.Equal("en-US", CultureInfo.CurrentCulture.Name);
        Assert.Equal("About Lagnostics", Localization.F("Acerca de {0}", "Lagnostics"));

        Localization.SetLanguage("es");
        Assert.Equal("Configuración", Localization.T("Configuración"));
        Assert.Equal("es-AR", CultureInfo.CurrentCulture.Name);
    }

    [Fact]
    public void UnknownLanguageFallsBackToSpanish()
    {
        Localization.SetLanguage("fr");

        Assert.Equal("es", Localization.Language);
        Assert.Equal("Configuración", Localization.T("Configuración"));
    }

    private static Dictionary<string, string> ReadResource(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream!)!;
    }
}
