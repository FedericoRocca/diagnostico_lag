using System.Drawing;
using System.Runtime.ExceptionServices;
using DiagnosticoLag;
using System.Windows.Forms;

namespace DiagnosticoLag.Tests;

public sealed class WindowConstructionTests
{
    [Fact]
    public void SupportLinkPointsToCafecito()
    {
        Assert.Equal("https://cafecito.app/magusman", SupportLink.Url);
    }

    [Fact]
    public void MainWindowBuildsDashboardWithoutLoadingOrWritingUserSettings()
    {
        RunOnStaThread(() =>
        {
            using var form = new MainForm(DiagnosticSettings.Default);

            Assert.Equal("Network diagnostics for gaming", form.Text);
            var controls = Descendants(form).ToArray();
            var grid = Assert.Single(controls.OfType<DataGridView>());
            Assert.True(grid.Columns.Count >= 11);
            Assert.IsType<DataGridViewCheckBoxColumn>(grid.Columns["visible"]);
            Assert.Contains(controls, control => control is LatencyChart);
            var durations = Assert.Single(controls.OfType<ComboBox>(), combo => combo.Items.Count == 5);
            Assert.Equal(["No limit", "30 seconds", "1 minute", "5 minutes", "15 minutes"],
                durations.Items.Cast<object>().Select(item => item.ToString() ?? string.Empty).ToArray());
            var menu = Assert.Single(controls.OfType<MenuStrip>());
            Assert.Equal(
                ["File", "Settings", "Export", "Sessions", "Help"],
                menu.Items.Cast<ToolStripItem>().Select(item => item.Text ?? string.Empty).ToArray());
            var export = Assert.Single(menu.Items.Cast<ToolStripMenuItem>(), item => item.Text == "Export");
            Assert.Equal(["Export report", "Export CSV"], export.DropDownItems.Cast<ToolStripItem>().Select(item => item.Text ?? string.Empty).ToArray());
            var sessions = Assert.Single(menu.Items.Cast<ToolStripMenuItem>(), item => item.Text == "Sessions");
            Assert.Equal(["Session folder", "Delete sessions"], sessions.DropDownItems.Cast<ToolStripItem>().Select(item => item.Text ?? string.Empty).ToArray());
            var help = Assert.Single(menu.Items.Cast<ToolStripMenuItem>(), item => item.Text == "Help");
            Assert.Equal(["Cafecito", "About"], help.DropDownItems.Cast<ToolStripItem>().Select(item => item.Text ?? string.Empty).ToArray());
        });
    }

    [Fact]
    public void DurationChoicesRefreshWhenLanguageChanges()
    {
        RunOnStaThread(() =>
        {
            try
            {
                Localization.SetLanguage("es");
                using var form = new MainForm(DiagnosticSettings.Default with { Language = "es" });
                var durations = Assert.Single(Descendants(form).OfType<ComboBox>(), combo => combo.Items.Count == 5);
                durations.SelectedIndex = 3;

                Localization.SetLanguage("en");
                form.ApplyLocalization();

                Assert.Equal("5 minutes", durations.SelectedItem?.ToString());
                Assert.Equal(["No limit", "30 seconds", "1 minute", "5 minutes", "15 minutes"],
                    durations.Items.Cast<object>().Select(item => item.ToString() ?? string.Empty).ToArray());
            }
            finally
            {
                Localization.SetLanguage("es");
            }
        });
    }

    [Fact]
    public void LatencyChartOffersIndividuallyToggleableVisibleConnections()
    {
        RunOnStaThread(() =>
        {
            Localization.SetLanguage("es");
            using var form = new MainForm(DiagnosticSettings.Default);
            var chart = Assert.Single(Descendants(form).OfType<LatencyChart>());
            var menuItems = chart.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>().ToArray();

            Assert.Contains(menuItems, item => item.Text == "Router" && item.Checked);
            var router = Assert.Single(menuItems, item => item.Text == "Router");
            router.PerformClick();
            Assert.False(router.Checked);
            router.PerformClick();
            Assert.True(router.Checked);
            Assert.True(chart.SupportsSeries("router"));
            Assert.False(chart.SupportsSeries("isp"));
            Assert.False(chart.SupportsSeries("target-1-tcp"));

            var timestamp = DateTime.Now;
            chart.AddSample(timestamp, new Dictionary<string, int>
            {
                ["router"] = 150,
                ["cloudflare-icmp"] = 150
            }, new[]
            {
                new TargetSnapshot(new ProbeTarget("router", "Router", "192.168.0.1", ProbeType.Icmp),
                    new StatSummary(1, 1, 0, 150, 150, 150, 150, 150, 1, 0, 0, 0)),
                new TargetSnapshot(new ProbeTarget("cloudflare-icmp", "Cloudflare", "1.1.1.1", ProbeType.Icmp),
                    new StatSummary(1, 1, 0, 150, 150, 150, 150, 150, 1, 0, 0, 0))
            });
            chart.SetSeriesVisibility("cloudflare-icmp", false);
            Assert.Equal(1, chart.VisibleEventCount);
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
            var languagePicker = Assert.Single(controls.OfType<ComboBox>(),
                combo => combo.Items.Cast<object>().Any(item => item.ToString() == "Español"));

            Assert.Equal("Configuración", form.Text);
            Assert.Contains("Español", languagePicker.Items.Cast<object>().Select(item => item.ToString()));
            Assert.Contains("English", languagePicker.Items.Cast<object>().Select(item => item.ToString()));
            Assert.Contains(controls, control => control is CheckedListBox);
        });
    }

    [Fact]
    public void SamplingIntervalAllowsFiftyMillisecondStepsAndManualInput()
    {
        RunOnStaThread(() =>
        {
            using var form = new SettingsForm(DiagnosticSettings.Default);
            var interval = Assert.Single(Descendants(form).OfType<NumericUpDown>());

            Assert.Equal(50, interval.Minimum);
            Assert.Equal(50, interval.Increment);
            Assert.False(interval.ReadOnly);
            interval.Value = 125;
            Assert.Equal(125, interval.Value);
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
                        new StatSummary(30, 30, 0, 12, 10, 18, 22, 35, 2, 0, 0, 0)),
                    new TargetSnapshot(
                        new ProbeTarget(
                            "game:lol",
                            "League of Legends - ICMP aprox. a 203.0.113.42:443",
                            "203.0.113.42",
                            ProbeType.Icmp,
                            GameProfileName: "League of Legends",
                            ObservedPort: 443),
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
            Assert.Equal($"League of Legends{Environment.NewLine}203.0.113.42",
                SummaryChart.FormatTargetLabel(snapshot.Targets[1]));
            Assert.All(controls.OfType<SummaryChart>(), chart => Assert.True(chart.Height >= 88 + snapshot.Targets.Count * 48));
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
