namespace DiagnosticoLag;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var startInTray = args.Contains(WindowsStartup.TrayArgument, StringComparer.OrdinalIgnoreCase);
        Application.Run(new MainForm { StartInTray = startInTray });
    }
}
