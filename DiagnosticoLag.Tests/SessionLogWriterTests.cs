using System.Text;
using DiagnosticoLag;

namespace DiagnosticoLag.Tests;

public sealed class SessionLogWriterTests
{
    [Fact]
    public void AppendsTextWithoutBom()
    {
        var path = CreatePath();
        try
        {
            new SessionLogWriter().AppendText(path, "registro");

            var bytes = File.ReadAllBytes(path);
            Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
            Assert.Equal("registro" + Environment.NewLine, File.ReadAllText(path, Encoding.UTF8));
        }
        finally
        {
            DeleteFile(path);
        }
    }

    [Fact]
    public void AppendsCsvWithUtf8Bom()
    {
        var path = CreatePath();
        try
        {
            new SessionLogWriter().AppendCsv(path, "destino,latencia");

            Assert.True(File.ReadAllBytes(path).AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
            Assert.Equal("destino,latencia" + Environment.NewLine, File.ReadAllText(path, Encoding.UTF8));
        }

        finally
        {
            DeleteFile(path);
        }
    }

    [Fact]
    public void SessionMetadataIncludesTraceabilityFields()
    {
        var metadata = SessionMetadata.Create(
            new DateTime(2026, 10, 10, 13, 33, 4, DateTimeKind.Local),
            "Ethernet",
            1);

        Localization.SetLanguage("es");
        Assert.False(string.IsNullOrWhiteSpace(metadata.ApplicationName));
        Assert.Equal(2, metadata.SchemaVersion);
        Assert.False(string.IsNullOrWhiteSpace(metadata.SessionId));
        Assert.Contains(metadata.ToCsvLines(), line => line.StartsWith("# application_version=", StringComparison.Ordinal));
        Assert.Contains(metadata.ToCsvLines(), line => line.StartsWith("# sample_interval_milliseconds=", StringComparison.Ordinal));
        Assert.Contains(metadata.ToTextLines(), line => line.StartsWith("Aplicativo:", StringComparison.Ordinal));
        Assert.Contains(metadata.ToTextLines(), line => line.Contains("Ethernet", StringComparison.Ordinal));

        Localization.SetLanguage("en");
        Assert.Contains(metadata.ToTextLines(), line => line.StartsWith("Application:", StringComparison.Ordinal));
        Assert.Contains(metadata.ToTextLines(), line => line.StartsWith("Sampling interval:", StringComparison.Ordinal));
        Assert.Contains(metadata.ToTextLines(), line => line.StartsWith("Status: In progress", StringComparison.Ordinal));
        Localization.SetLanguage("es");
    }

    private static string CreatePath() =>
        Path.Combine(Path.GetTempPath(), $"diagnostico-lag-{Guid.NewGuid():N}.log");

    private static void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
