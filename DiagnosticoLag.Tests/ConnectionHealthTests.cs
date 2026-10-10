using DiagnosticoLag;

namespace DiagnosticoLag.Tests;

public sealed class ConnectionHealthTests
{
    [Theory]
    [InlineData("OK", 100, "Salud buena")]
    [InlineData("ATENCION", 65, "Salud inestable")]
    [InlineData("PROBLEMA", 25, "Salud crítica")]
    [InlineData("DATOS_INSUFICIENTES", 50, "Salud indeterminada")]
    public void MapsDiagnosticLevelToVisibleHealthSummary(string level, int expectedScore, string expectedStatus)
    {
        var diagnosis = new DiagnosticResult(level, "", "", "", Array.Empty<DiagnosticFinding>());

        var health = ConnectionHealth.FromDiagnosis(diagnosis);

        Assert.Equal(expectedScore, health.Score);
        Assert.Equal(expectedStatus, health.Status);
    }
}
