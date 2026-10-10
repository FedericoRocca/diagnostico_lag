using System.Diagnostics;

namespace DiagnosticoLag;

internal static class SupportLink
{
    public const string Url = "https://cafecito.app/magusman";
    private const string ButtonText = "☕ Invitame un café";

    public static Button CreateButton()
    {
        var button = new Button
        {
            Text = Localization.T(ButtonText),
            AutoSize = true,
            Height = 34,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(255, 244, 214),
            ForeColor = Color.FromArgb(120, 72, 0),
            Cursor = Cursors.Hand,
            Padding = new Padding(8, 0, 8, 0)
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(232, 190, 96);
        button.Click += (_, _) => Open(button.FindForm());
        return button;
    }

    public static void Localize(Button button) => button.Text = Localization.T(ButtonText);

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
