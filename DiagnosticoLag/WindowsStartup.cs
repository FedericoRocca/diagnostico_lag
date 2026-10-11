using Microsoft.Win32;

namespace DiagnosticoLag;

internal static class WindowsStartup
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Lagnostics";

    internal static void SetEnabled(bool enabled, string executablePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("No se pudo abrir la configuración de inicio de Windows.");

        if (enabled)
        {
            key.SetValue(ValueName, BuildCommand(executablePath), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    internal static string BuildCommand(string executablePath) => $"\"{executablePath}\"";
}
