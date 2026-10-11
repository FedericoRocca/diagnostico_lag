using System.Diagnostics;

namespace DiagnosticoLag;

internal static class SupportLink
{
    public const string Url = "https://cafecito.app/magusman";

    public static void Open(IWin32Window? owner)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(owner, $"{Localization.T("No se pudo abrir el navegador. Visitá:")}{Environment.NewLine}{Url}",
                Localization.T("Invitame un café"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
