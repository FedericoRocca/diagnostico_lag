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
