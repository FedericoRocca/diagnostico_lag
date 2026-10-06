using System.Drawing;
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

    [Fact]
    public void ReportsShowVisualChartsAndKeepTheDetailedTextReport()
    {
        RunOnStaThread(() =>
        {
            Localization.SetLanguage("es");
            var snapshot = new MonitorSnapshot(
                DateTime.Now,
                TimeSpan.FromMinutes(2),
                new[]
                {
                    new TargetSnapshot(
                        new ProbeTarget("router", "Router/Modem", "192.168.0.1", ProbeType.Icmp),
                        new StatSummary(30, 30, 0, 12, 10, 18, 22, 35, 2, 0, 0, 0))
                },
                new Dictionary<string, int>(),
                Array.Empty<string>(),
                new ConnectionInfo("Ethernet", "Ethernet", null),
                "192.168.0.1",
                null,
                Array.Empty<GameObservedEndpoint>(),
                Array.Empty<GameObservedEndpoint>(),
                new DiagnosticResult(
                    "ATENCION",
                    "Se detectaron anomalías aisladas",
                    "No hay evidencia suficiente de un problema sostenido.",
                    "Dejá correr el monitoreo mientras ocurre el lag y repetí la prueba por Ethernet.",
                    new[] { new DiagnosticFinding("Router: P95 de 18 ms.", "Puede señalar variabilidad local.") }),
                0);

            using var form = new ReportForm("Resumen final conservado.", snapshot);
            var controls = Descendants(form).ToArray();

            Assert.Equal(2, controls.OfType<SummaryChart>().Count());
            Assert.Contains(controls.OfType<TabPage>(), page => page.Text == "Resumen visual");
            Assert.Contains(controls.OfType<RichTextBox>(), box => box.Text == "Resumen final conservado.");
            Assert.Contains(controls.OfType<Label>(), label => label.Text.Contains("Puede señalar variabilidad local.", StringComparison.Ordinal));
            foreach (var chart in controls.OfType<SummaryChart>())
            {
                using var bitmap = new Bitmap(chart.Width, chart.Height);
                chart.DrawToBitmap(bitmap, new Rectangle(Point.Empty, chart.Size));
            }
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
