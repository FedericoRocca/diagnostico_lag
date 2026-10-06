using System.Runtime.ExceptionServices;
using DiagnosticoLag;
using System.Windows.Forms;

namespace DiagnosticoLag.Tests;

public sealed class WindowConstructionTests
{
    [Fact]
    public void MainWindowBuildsDashboardWithoutLoadingOrWritingUserSettings()
    {
        RunOnStaThread(() =>
        {
            using var form = new MainForm(DiagnosticSettings.Default);

            Assert.Equal("Network diagnostics for gaming", form.Text);
            var controls = Descendants(form).ToArray();
            Assert.Contains(controls, control => control is DataGridView grid && grid.Columns.Count >= 10);
            Assert.Contains(controls, control => control is LatencyChart);
        });
    }

    [Fact]
    public void SettingsWindowBuildsLanguageAndGameProfileControls()
    {
        RunOnStaThread(() =>
        {
            Localization.SetLanguage("es");
            using var form = new SettingsForm(DiagnosticSettings.Default);
            var controls = Descendants(form).ToArray();
            var languagePicker = Assert.Single(controls.OfType<ComboBox>()
                .Where(combo => combo.Items.Cast<object>().Any(item => item.ToString() == "Español")));

            Assert.Equal("Configuración", form.Text);
            Assert.Contains("Español", languagePicker.Items.Cast<object>().Select(item => item.ToString()));
            Assert.Contains("English", languagePicker.Items.Cast<object>().Select(item => item.ToString()));
            Assert.Contains(controls, control => control is CheckedListBox);
        });
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception caught)
            {
                exception = caught;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }
}
