namespace DiagnosticoLag;

internal sealed record ConnectionHealth(int Score, string Status)
{
    public static ConnectionHealth FromDiagnosis(DiagnosticResult diagnosis) =>
        diagnosis.Level switch
        {
            "PROBLEMA" => new(25, "Salud crítica"),
            "ATENCION" => new(65, "Salud inestable"),
            "DATOS_INSUFICIENTES" => new(50, "Salud indeterminada"),
            _ => new(100, "Salud buena")
        };
}
