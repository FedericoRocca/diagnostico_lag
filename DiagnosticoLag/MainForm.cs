using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace DiagnosticoLag;

internal sealed class MainForm : Form
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly BufferedDataGridView _grid = new();
    private readonly LatencyChart _chart = new();
    private readonly BufferedLabel _status = new();
    private readonly BufferedLabel _diagnosis = new();
    private readonly BufferedRichTextBox _details = new();
    private readonly Button _startButton = new();
    private readonly Button _pauseButton = new();
    private readonly Button _resumeButton = new();
    private readonly Button _finishButton = new();
    private readonly Button _reportButton = new();
    private readonly Button _exportReportButton = new();
    private readonly Button _exportCsvButton = new();
    private readonly Button _settingsButton = new();
    private readonly Icon _applicationIcon;
    private readonly string _logDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiagnosticoLag", "sesiones");
    private DiagnosticSession? _session;
    private DiagnosticSettings _settings = DiagnosticSettings.Default;
    private CancellationTokenSource? _samplingCancellation;
    private Task? _activeSampleTask;
    private string? _sessionLogPath;
    private string? _sessionCsvPath;
    private string? _lastReport;
    private bool _sampling;
    private bool _loggingAvailable = true;
    private bool _csvAvailable = true;
    private bool _closing;

    public MainForm()
    {
        Text = "Diagnóstico de red para gaming";
        _applicationIcon = LoadApplicationIcon();
        Icon = _applicationIcon;
        MinimumSize = new Size(980, 680);
        Size = new Size(1220, 850);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.FromArgb(245, 247, 250);

        try
        {
            _settings = DiagnosticSettings.Load();
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"{exception.Message}{Environment.NewLine}{Environment.NewLine}Se usarán los valores predeterminados. Podés revisarlos en Configuración.",
                "No se pudo cargar la configuración", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        BuildInterface();
        _timer.Tick += async (_, _) => await SampleOnceAsync();
        FormClosing += OnFormClosing;
        SetMonitoringControls(false, false);
    }

    private static Icon LoadApplicationIcon()
    {
        const string resourceName = "DiagnosticoLag.Assets.diagnostico-lag.ico";
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"No se encontró el recurso de icono de la aplicación: {resourceName}");
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }

    private static string ApplicationName =>
        typeof(MainForm).Assembly.GetCustomAttribute<System.Reflection.AssemblyProductAttribute>()?.Product
        ?? throw new InvalidOperationException("No se pudo determinar el nombre de la aplicación compilada.");

    private static Version ApplicationVersion =>
        typeof(MainForm).Assembly.GetName().Version
        ?? throw new InvalidOperationException("No se pudo determinar la versión compilada de la aplicación.");

    private void BuildInterface()
    {
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 5
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        Controls.Add(layout);

        var heading = new Panel { Dock = DockStyle.Fill };
        var title = new Label
        {
            Text = $"{ApplicationName} · Beta {ApplicationVersion.ToString(3)}",
            Font = new Font("Segoe UI Semibold", 15F, FontStyle.Bold),
            ForeColor = Color.FromArgb(24, 39, 61),
            AutoSize = true,
            Location = new Point(0, 0)
        };
        var subtitle = new Label
        {
            Text = "DIAGNÓSTICO DE RED",
            Font = new Font("Segoe UI", 8.5F),
            ForeColor = Color.FromArgb(112, 126, 145),
            AutoSize = true,
            Location = new Point(1, 34)
        };
        var helpButton = new Button
        {
            Text = "?",
            Size = new Size(28, 28),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold),
            ForeColor = Color.FromArgb(57, 91, 145),
            BackColor = Color.White,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Location = new Point(heading.ClientSize.Width - 32, 15),
            Cursor = Cursors.Hand
        };
        helpButton.FlatAppearance.BorderColor = Color.FromArgb(200, 211, 224);
        helpButton.FlatAppearance.BorderSize = 1;
        _status.Text = "Listo para iniciar";
        _status.AutoSize = true;
        _status.ForeColor = Color.FromArgb(88, 104, 126);
        _status.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _status.Location = new Point(heading.Width - _status.Width - helpButton.Width - 20, 20);
        heading.Resize += (_, _) =>
        {
            helpButton.Location = new Point(heading.ClientSize.Width - helpButton.Width - 2, 15);
            _status.Location = new Point(heading.ClientSize.Width - _status.Width - helpButton.Width - 14, 20);
        };
        heading.Controls.Add(title);
        heading.Controls.Add(subtitle);
        heading.Controls.Add(_status);
        heading.Controls.Add(helpButton);
        layout.Controls.Add(heading, 0, 0);

        ConfigureGrid();
        layout.Controls.Add(_grid, 0, 1);

        _chart.Dock = DockStyle.Fill;
        _chart.BackColor = Color.White;
        _chart.Margin = new Padding(0, 12, 0, 8);
        layout.Controls.Add(_chart, 0, 2);

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 5, 0, 0)
        };
        ConfigureButton(_startButton, "Iniciar", Color.FromArgb(28, 121, 91));
        ConfigureButton(_pauseButton, "Pausar", Color.FromArgb(78, 96, 120));
        ConfigureButton(_resumeButton, "Continuar", Color.FromArgb(28, 121, 91));
        ConfigureButton(_finishButton, "Finalizar", Color.FromArgb(175, 59, 59));
        ConfigureButton(_reportButton, "Informe parcial", Color.FromArgb(57, 91, 145));
        _exportReportButton.Text = "Exportar informe";
        _exportReportButton.AutoSize = true;
        _exportReportButton.Height = 34;
        _exportReportButton.Margin = new Padding(0, 0, 8, 0);
        _exportCsvButton.Text = "Exportar CSV";
        _exportCsvButton.AutoSize = true;
        _exportCsvButton.Height = 34;
        _exportCsvButton.Margin = new Padding(0, 0, 8, 0);
        _settingsButton.Text = "Configuración";
        _settingsButton.AutoSize = true;
        _settingsButton.Height = 34;
        _settingsButton.Margin = new Padding(0, 0, 8, 0);
        var openLogButton = new Button { Text = "Carpeta de sesiones", AutoSize = true, Height = 34, Margin = new Padding(0, 0, 0, 0) };
        toolbar.Controls.AddRange([_startButton, _pauseButton, _resumeButton, _finishButton, _reportButton,
            _exportReportButton, _exportCsvButton, _settingsButton, openLogButton]);
        layout.Controls.Add(toolbar, 0, 3);

        var bottom = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 6, 0, 0) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
        _diagnosis.Dock = DockStyle.Fill;
        _diagnosis.Text = "Iniciá el monitoreo para medir la red.";
        _diagnosis.Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold);
        _diagnosis.ForeColor = Color.FromArgb(49, 67, 89);
        _diagnosis.Padding = new Padding(12);
        _diagnosis.BackColor = Color.White;
        _details.Dock = DockStyle.Fill;
        _details.ReadOnly = true;
        _details.BorderStyle = BorderStyle.None;
        _details.BackColor = Color.White;
        _details.Font = new Font("Segoe UI", 9F);
        _details.Margin = new Padding(8, 0, 0, 0);
        bottom.Controls.Add(_diagnosis, 0, 0);
        bottom.Controls.Add(_details, 1, 0);
        layout.Controls.Add(bottom, 0, 4);

        _startButton.Click += async (_, _) => await StartMonitoringAsync();
        _pauseButton.Click += (_, _) => PauseMonitoring();
        _resumeButton.Click += (_, _) => ResumeMonitoring();
        _finishButton.Click += async (_, _) => await FinishMonitoringAsync();
        _reportButton.Click += (_, _) => ShowReport("parcial");
        _exportReportButton.Click += (_, _) => ExportReport();
        _exportCsvButton.Click += (_, _) => ExportCsv();
        _settingsButton.Click += (_, _) => ShowSettings();
        openLogButton.Click += (_, _) => OpenLog();
        helpButton.Click += (_, _) => ShowAbout();
    }

    private void ShowAbout()
    {
        var message = $"{ApplicationName} · Beta {ApplicationVersion.ToString(3)}{Environment.NewLine}{Environment.NewLine}" +
                      "Herramienta de diagnóstico de red para gaming. Mide la latencia y la pérdida de paquetes " +
                      "hacia el router y destinos de Internet, muestra su evolución en tiempo real y ofrece " +
                      $"estadísticas y un diagnóstico para ayudar a detectar problemas de conexión.{Environment.NewLine}{Environment.NewLine}" +
                      "Las sesiones pueden pausarse, generar informes y exportarse a archivos de texto o CSV.";
        MessageBox.Show(this, message, $"Acerca de {ApplicationName}", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.BackgroundColor = Color.White;
        _grid.BorderStyle = BorderStyle.None;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.ReadOnly = true;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.ColumnHeadersHeight = 36;
        _grid.RowTemplate.Height = 33;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(230, 236, 243);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(45, 62, 84);
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold);
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(226, 238, 250);
        _grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(24, 39, 61);
        _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(249, 251, 253);
        _grid.Columns.Add("target", "Destino");
        _grid.Columns.Add("samples", "Muestras");
        _grid.Columns.Add("average", "Prom. ms");
        _grid.Columns.Add("median", "Mediana");
        _grid.Columns.Add("p95", "P95");
        _grid.Columns.Add("p99", "P99");
        _grid.Columns.Add("maximum", "Máx.");
        _grid.Columns.Add("jitter", "Jitter");
        _grid.Columns.Add("spikes", "Picos ≥80 ms");
        _grid.Columns.Add("loss", "Pérdida");
        _grid.Columns["target"]!.FillWeight = 145;
    }

    private static void ConfigureButton(Button button, string text, Color color)
    {
        button.Text = text;
        button.AutoSize = true;
        button.Height = 34;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = color;
        button.ForeColor = Color.White;
        button.Padding = new Padding(9, 0, 9, 0);
        button.Margin = new Padding(0, 0, 8, 0);
        button.Cursor = Cursors.Hand;
    }

    private async Task StartMonitoringAsync()
    {
        _startButton.Enabled = false;
        _status.Text = "Detectando router y ruta al ISP…";
        _samplingCancellation = new CancellationTokenSource();
        try
        {
            _session = await DiagnosticSession.CreateAsync(_settings, _samplingCancellation.Token);
            Directory.CreateDirectory(_logDirectory);
            var sessionName = $"sesion_{_session.StartedAt:yyyyMMdd_HHmmss_fff}";
            _sessionLogPath = Path.Combine(_logDirectory, $"{sessionName}.txt");
            _sessionCsvPath = Path.Combine(_logDirectory, $"{sessionName}.csv");
            _timer.Interval = _settings.SampleIntervalSeconds * 1000;
            _grid.Rows.Clear();
            _chart.StartSession(_session.StartedAt);
            _details.Clear();
            AppendLog($"--- NUEVA SESIÓN: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---{Environment.NewLine}" +
                      $"Conexión: {_session.Connection.Type} ({_session.Connection.Detail}) | Router: {_session.RouterAddress} | " +
                      $"Primer salto ISP: {_session.IspAddress ?? "no detectado"}{Environment.NewLine}" +
                      $"Registro: {_sessionLogPath}{Environment.NewLine}" +
                      $"Datos CSV: {_sessionCsvPath}{Environment.NewLine}");
            AppendCsv("hora_local,destino,protocolo,direccion,puerto,latencia_ms,resultado");
            _status.Text = $"Router {_session.RouterAddress} | ISP {_session.IspAddress ?? "no detectado"}";
            SetMonitoringControls(true, false);
            await SampleOnceAsync();
            if (_session is not null && !_closing)
            {
                _timer.Start();
            }
        }
        catch (OperationCanceledException)
        {
            _samplingCancellation?.Dispose();
            _samplingCancellation = null;
            _session = null;
            _status.Text = "Inicio cancelado";
            SetMonitoringControls(false, false);
        }
        catch (Exception exception)
        {
            _samplingCancellation?.Dispose();
            _samplingCancellation = null;
            _session = null;
            _status.Text = "No se pudo iniciar";
            SetMonitoringControls(false, false);
            MessageBox.Show(this, exception.Message, "Error al iniciar el diagnóstico", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private Task SampleOnceAsync()
    {
        if (_session is null || _samplingCancellation is null || _sampling || _closing)
        {
            return Task.CompletedTask;
        }

        _sampling = true;
        var session = _session;
        var cancellation = _samplingCancellation;
        var task = SampleCoreAsync(session, cancellation);
        _activeSampleTask = task;
        return AwaitSampleTaskAsync(task, cancellation);
    }

    private async Task AwaitSampleTaskAsync(Task task, CancellationTokenSource cancellation)
    {
        try
        {
            await task;
        }
        finally
        {
            if (ReferenceEquals(_activeSampleTask, task))
            {
                _activeSampleTask = null;
            }

            if (_closing)
            {
                cancellation.Dispose();
            }
        }
    }

    private async Task SampleCoreAsync(DiagnosticSession session, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            var snapshot = await session.SampleAsync(token);
            UpdateDashboard(snapshot);
            AppendLog(FormatLiveLog(snapshot));
            if (session.PeriodicLogIsDue())
            {
                AppendLog(session.BuildReport("periódico"));
            }
            AppendCsv(FormatCsv(snapshot));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _timer.Stop();
            _status.Text = "Error durante la medición";
            SetMonitoringControls(true, true);
            MessageBox.Show(this, exception.Message, "Error durante el diagnóstico", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _sampling = false;
        }
    }

    private void UpdateDashboard(MonitorSnapshot snapshot)
    {
        _grid.SuspendLayout();
        try
        {
            while (_grid.Rows.Count < snapshot.Targets.Count)
            {
                _grid.Rows.Add();
            }

            while (_grid.Rows.Count > snapshot.Targets.Count)
            {
                _grid.Rows.RemoveAt(_grid.Rows.Count - 1);
            }

            for (var index = 0; index < snapshot.Targets.Count; index++)
            {
                var target = snapshot.Targets[index];
                var stats = target.Statistics;
                var row = _grid.Rows[index];
                row.Cells["target"].Value = target.Target.Name;
                row.Cells["samples"].Value = stats.Samples;
                row.Cells["average"].Value = stats.Average.ToString("F1");
                row.Cells["median"].Value = stats.Median;
                row.Cells["p95"].Value = stats.P95;
                row.Cells["p99"].Value = stats.P99;
                row.Cells["maximum"].Value = stats.Maximum;
                row.Cells["jitter"].Value = stats.Jitter.ToString("F1");
                row.Cells["spikes"].Value = stats.Spikes80 + stats.Spikes120;
                row.Cells["loss"].Value = $"{stats.LossPercent:F1}%";
                var lossColor = stats.LossPercent >= 5
                    ? Color.Firebrick
                    : stats.LossPercent > 0.5 ? Color.DarkGoldenrod : Color.DarkGreen;
                if (row.Cells["loss"].Style.ForeColor != lossColor)
                {
                    row.Cells["loss"].Style.ForeColor = lossColor;
                }
            }
        }
        finally
        {
            _grid.ResumeLayout();
        }

        var lossHeader = snapshot.Targets.Any(target => target.Target.Type == ProbeType.Tcp)
            ? "Pérdida / fallos TCP"
            : "Pérdida";
        if (_grid.Columns["loss"]!.HeaderText != lossHeader)
        {
            _grid.Columns["loss"]!.HeaderText = lossHeader;
        }

        _chart.AddSample(DateTime.Now, snapshot.LastMeasurements);
        var diagnosis = snapshot.Diagnosis;
        var diagnosisText = $"{diagnosis.Title}{Environment.NewLine}{diagnosis.Explanation}" +
                            (diagnosis.Reasons.Count == 0 ? "" : $"{Environment.NewLine}{Environment.NewLine}• {string.Join($"{Environment.NewLine}• ", diagnosis.Reasons.Take(4))}");
        if (_diagnosis.Text != diagnosisText)
        {
            _diagnosis.Text = diagnosisText;
        }

        var diagnosisColor = diagnosis.Level switch
        {
            "PROBLEMA" => Color.Firebrick,
            "ATENCION" => Color.DarkGoldenrod,
            "DATOS_INSUFICIENTES" => Color.FromArgb(57, 91, 145),
            _ => Color.DarkGreen
        };
        if (_diagnosis.ForeColor != diagnosisColor)
        {
            _diagnosis.ForeColor = diagnosisColor;
        }
        _details.SetTextWithoutFlicker(
            $"Duración: {FormatDuration(snapshot.Duration)} | Línea base: " +
            (snapshot.SamplesUntilBaseline == 0 ? "lista" : $"en {snapshot.SamplesUntilBaseline} muestras") +
            $"{Environment.NewLine}{Environment.NewLine}Eventos recientes{Environment.NewLine}" +
            (snapshot.RecentEvents.Count == 0 ? "Sin pérdidas ni picos ≥120 ms." : string.Join(Environment.NewLine, snapshot.RecentEvents.TakeLast(10))));
    }

    private static string FormatDuration(TimeSpan duration)
    {
        var totalHours = (int)duration.TotalHours;
        return duration.TotalDays >= 1
                        ? $"{(int)duration.TotalDays}d {duration.Hours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
                        : $"{totalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private static string FormatLiveLog(MonitorSnapshot snapshot)
    {
        var values = snapshot.Targets
            .Where(target => snapshot.LastMeasurements.ContainsKey(target.Target.Key))
            .Select(target =>
            {
                var value = snapshot.LastMeasurements[target.Target.Key];
                return $"{target.Target.Name}: {(value < 0 ? "sin respuesta" : $"{value} ms")}";
            });
        return $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {string.Join(" | ", values)}{Environment.NewLine}";
    }

    private void PauseMonitoring()
    {
        _timer.Stop();
        _status.Text = "Monitoreo pausado";
        SetMonitoringControls(true, true);
    }

    private void ResumeMonitoring()
    {
        if (_session is null)
        {
            return;
        }

        _status.Text = "Monitoreo activo";
        SetMonitoringControls(true, false);
        _timer.Start();
    }

    private async Task FinishMonitoringAsync()
    {
        _timer.Stop();
        var cancellation = _samplingCancellation;
        cancellation?.Cancel();
        if (_activeSampleTask is not null)
        {
            await _activeSampleTask;
        }

        if (_session is not null)
        {
            ShowReport("final");
            _session = null;
        }

        cancellation?.Dispose();
        _samplingCancellation = null;
        _status.Text = "Monitoreo finalizado";
        SetMonitoringControls(false, false);
    }

    private void ShowReport(string type)
    {
        if (_session is null)
        {
            return;
        }

        var report = _session.BuildReport(type);
        if (type == "final")
        {
            _lastReport = report;
        }
        AppendLog(report);
        using var window = new ReportForm(report);
        window.ShowDialog(this);
    }

    private void OpenLog()
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);
            Process.Start(new ProcessStartInfo { FileName = _logDirectory, UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, $"No se pudo abrir el registro:{Environment.NewLine}{exception.Message}",
                "Error al abrir el registro", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void AppendLog(string text)
    {
        if (!_loggingAvailable)
        {
            return;
        }

        try
        {
            if (_sessionLogPath is null)
            {
                return;
            }

            File.AppendAllText(_sessionLogPath, text + Environment.NewLine, new UTF8Encoding(false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _loggingAvailable = false;
            _status.Text = "No se pudo escribir el registro";
            MessageBox.Show(this, $"El monitoreo sigue, pero no se pudo guardar el registro en:{Environment.NewLine}{_sessionLogPath}{Environment.NewLine}{Environment.NewLine}{exception.Message}",
                "Error de registro", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void AppendCsv(string text)
    {
        if (!_csvAvailable || _sessionCsvPath is null)
        {
            return;
        }

        try
        {
            File.AppendAllText(_sessionCsvPath, text + Environment.NewLine, new UTF8Encoding(true));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _csvAvailable = false;
            MessageBox.Show(this, $"El monitoreo sigue, pero no se pudo guardar el CSV:{Environment.NewLine}{_sessionCsvPath}{Environment.NewLine}{Environment.NewLine}{exception.Message}",
                "Error al guardar CSV", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string FormatCsv(MonitorSnapshot snapshot)
    {
        return string.Join(Environment.NewLine, snapshot.Targets.Select(target =>
        {
            var value = snapshot.LastMeasurements[target.Target.Key];
            var result = value < 0 ? "sin respuesta" : "correcto";
            return string.Join(",",
                CsvField(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)),
                CsvField(target.Target.Name),
                CsvField(target.Target.Type.ToString()),
                CsvField(target.Target.Address),
                target.Target.Type == ProbeType.Tcp ? target.Target.Port.ToString(CultureInfo.InvariantCulture) : "",
                value < 0 ? "" : value.ToString(CultureInfo.InvariantCulture),
                CsvField(result));
        }));
    }

    private static string CsvField(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private void ShowSettings()
    {
        using var dialog = new SettingsForm(_settings);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _settings = dialog.Settings;
        _status.Text = "Configuración guardada; se aplicará en la próxima sesión";
    }

    private void ExportReport()
    {
        var report = _session?.BuildReport("exportado") ?? _lastReport;
        if (string.IsNullOrWhiteSpace(report))
        {
            MessageBox.Show(this, "Todavía no hay un informe para exportar.", "Exportar informe",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Filter = "Informe de texto (*.txt)|*.txt",
            FileName = $"diagnostico_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, report, new UTF8Encoding(true));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, exception.Message, "No se pudo exportar el informe", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ExportCsv()
    {
        if (_sessionCsvPath is null || !File.Exists(_sessionCsvPath))
        {
            MessageBox.Show(this, "Todavía no hay datos CSV para exportar. Iniciá una sesión y esperá la primera medición.",
                "Exportar CSV", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Filter = "Datos CSV (*.csv)|*.csv",
            FileName = Path.GetFileName(_sessionCsvPath)
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            File.Copy(_sessionCsvPath, dialog.FileName, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, exception.Message, "No se pudo exportar el CSV", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetMonitoringControls(bool active, bool paused)
    {
        _startButton.Enabled = !active;
        _pauseButton.Enabled = active && !paused;
        _resumeButton.Enabled = active && paused;
        _finishButton.Enabled = active;
        _reportButton.Enabled = active && _session is not null;
        _exportReportButton.Enabled = active || _lastReport is not null;
        _exportCsvButton.Enabled = _sessionCsvPath is not null;
        _settingsButton.Enabled = !active;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _closing = true;
        _timer.Stop();
        if (_session is not null)
        {
            AppendLog(_session.BuildReport("final (al cerrar la aplicación)"));
        }

        _samplingCancellation?.Cancel();
        if (_activeSampleTask is null)
        {
            _samplingCancellation?.Dispose();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _applicationIcon.Dispose();
        }

        base.Dispose(disposing);
    }
}

internal sealed class BufferedDataGridView : DataGridView
{
    public BufferedDataGridView()
    {
        DoubleBuffered = true;
    }
}

internal sealed class BufferedLabel : Label
{
    public BufferedLabel()
    {
        DoubleBuffered = true;
    }
}

internal sealed class BufferedTableLayoutPanel : TableLayoutPanel
{
    public BufferedTableLayoutPanel()
    {
        DoubleBuffered = true;
    }
}

internal sealed class BufferedRichTextBox : RichTextBox
{
    private const int WmSetRedraw = 0x000B;
    private const int EmGetFirstVisibleLine = 0x00CE;
    private const int EmLineScroll = 0x00B6;

    public BufferedRichTextBox()
    {
        DoubleBuffered = true;
    }

    public void SetTextWithoutFlicker(string text)
    {
        if (Text == text)
        {
            return;
        }

        if (!IsHandleCreated)
        {
            Text = text;
            return;
        }

        var selectionStart = SelectionStart;
        var selectionLength = SelectionLength;
        var firstVisibleLine = SendMessage(Handle, EmGetFirstVisibleLine, IntPtr.Zero, IntPtr.Zero).ToInt32();
        SendMessage(Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
        try
        {
            Text = text;
            Select(Math.Min(selectionStart, TextLength), Math.Min(selectionLength, Math.Max(0, TextLength - selectionStart)));
            var currentFirstVisibleLine = SendMessage(Handle, EmGetFirstVisibleLine, IntPtr.Zero, IntPtr.Zero).ToInt32();
            SendMessage(Handle, EmLineScroll, IntPtr.Zero, new IntPtr(firstVisibleLine - currentFirstVisibleLine));
        }
        finally
        {
            SendMessage(Handle, WmSetRedraw, new IntPtr(1), IntPtr.Zero);
            Invalidate();
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);
}

internal sealed class ReportForm : Form
{
    public ReportForm(string report)
    {
        Text = "Informe de diagnóstico";
        Size = new Size(900, 700);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(650, 450);

        var text = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            WordWrap = false,
            Font = new Font("Consolas", 10F),
            Text = report,
            BackColor = Color.White
        };
        var copy = new Button { Text = "Copiar informe", AutoSize = true, Height = 34, Margin = new Padding(0, 6, 8, 6) };
        var close = new Button { Text = "Cerrar", AutoSize = true, Height = 34, Margin = new Padding(0, 6, 8, 6) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(close);
        buttons.Controls.Add(copy);
        Controls.Add(text);
        Controls.Add(buttons);
        copy.Click += (_, _) => Clipboard.SetText(report);
        close.Click += (_, _) => Close();
    }
}

internal sealed class LatencyChart : Control
{
    private sealed record SamplePoint(DateTime Timestamp, int? Value);
    private sealed record ChartEvent(DateTime Timestamp, string Label);
    private const int MaximumPointsPerSeries = 8192;
    private const int MaximumEventMarkers = 2048;

    private readonly Dictionary<string, List<SamplePoint>> _series = new(StringComparer.Ordinal)
    {
        ["router"] = new List<SamplePoint>(),
        ["cloudflare-icmp"] = new List<SamplePoint>(),
        ["google-icmp"] = new List<SamplePoint>()
    };
    private readonly List<ChartEvent> _events = new();
    private readonly Dictionary<string, (string Name, Color Color)> _labels = new(StringComparer.Ordinal)
    {
        ["router"] = ("Router", Color.FromArgb(31, 132, 97)),
        ["cloudflare-icmp"] = ("Cloudflare", Color.FromArgb(61, 117, 190)),
        ["google-icmp"] = ("Google", Color.FromArgb(218, 137, 51))
    };
    private readonly ToolTip _hoverTip = new() { InitialDelay = 250, ReshowDelay = 100, AutoPopDelay = 10000, ShowAlways = true };
    private readonly ContextMenuStrip _viewMenu = new();
    private DateTime? _sessionStartedAt;
    private string? _lastHoverText;
    private TimeSpan? _viewDuration;

    public LatencyChart()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        _viewMenu.Items.Add("Toda la sesión", null, (_, _) => SetViewDuration(null));
        _viewMenu.Items.Add("Últimos 5 minutos", null, (_, _) => SetViewDuration(TimeSpan.FromMinutes(5)));
        _viewMenu.Items.Add("Últimos 15 minutos", null, (_, _) => SetViewDuration(TimeSpan.FromMinutes(15)));
        _viewMenu.Items.Add("Última hora", null, (_, _) => SetViewDuration(TimeSpan.FromHours(1)));
        _viewMenu.Items.Add("Últimas 6 horas", null, (_, _) => SetViewDuration(TimeSpan.FromHours(6)));
        ContextMenuStrip = _viewMenu;
        MouseMove += ShowHoveredSeries;
        MouseLeave += (_, _) =>
        {
            _hoverTip.Hide(this);
            _lastHoverText = null;
        };
    }

    public void StartSession(DateTime startedAt)
    {
        foreach (var values in _series.Values)
        {
            values.Clear();
        }

        _events.Clear();
        _sessionStartedAt = startedAt;
        _viewDuration = null;
        _lastHoverText = null;
        _hoverTip.Hide(this);
        Invalidate();
    }

    public void AddSample(DateTime timestamp, IReadOnlyDictionary<string, int> measurements)
    {
        foreach (var key in _series.Keys)
        {
            var values = _series[key];
            var value = measurements.TryGetValue(key, out var measured) && measured >= 0 ? measured : (int?)null;
            values.Add(new SamplePoint(timestamp, value));
            if (value is null || value >= 120)
            {
                _events.Add(new ChartEvent(timestamp, $"{_labels[key].Name}: {(value is null ? "sin respuesta" : $"{value} ms")}"));
                if (_events.Count > MaximumEventMarkers)
                {
                    _events.RemoveAt(0);
                }
            }

            if (values.Count > MaximumPointsPerSeries)
            {
                ReducePoints(values);
            }
        }

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.White);

        using var textBrush = new SolidBrush(Color.FromArgb(58, 73, 92));
        using var gridPen = new Pen(Color.FromArgb(232, 237, 242));
        using var borderPen = new Pen(Color.FromArgb(220, 227, 235));
        using var font = new Font("Segoe UI", 8.5F);
        using var titleFont = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold);
        var sessionStart = _sessionStartedAt ?? DateTime.Now;
        var now = DateTime.Now;
        var viewStart = _viewDuration.HasValue
            ? Max(sessionStart, now - _viewDuration.Value)
            : sessionStart;
        var durationSeconds = Math.Max(0.001, (now - viewStart).TotalSeconds);
        var latencyScale = GetLatencyScale();
        var viewName = _viewDuration.HasValue ? $"últimos {FormatViewDuration(_viewDuration.Value)}" : "toda la sesión";
        graphics.DrawString($"Latencia ICMP en vivo ({viewName}; clic derecho para cambiar vista; escala 0-{latencyScale} ms)",
            titleFont, textBrush, 12, 10);

        var plot = GetPlotRectangle();
        var latencyStep = latencyScale / 4;
        for (var value = 0; value <= latencyScale; value += latencyStep)
        {
            var y = plot.Bottom - (int)(value / (float)latencyScale * plot.Height);
            graphics.DrawLine(gridPen, plot.Left, y, plot.Right, y);
            graphics.DrawString(value.ToString(CultureInfo.InvariantCulture), font, textBrush, 8, y - 8);
        }

        DrawTimeAxis(graphics, plot, viewStart, now, durationSeconds, gridPen, textBrush, font);
        graphics.DrawRectangle(borderPen, plot);
        var legendX = plot.Left + 6;
        foreach (var (key, label) in _labels)
        {
            using var legendBrush = new SolidBrush(label.Color);
            graphics.FillEllipse(legendBrush, legendX, 26, 8, 8);
            graphics.DrawString(label.Name, font, textBrush, legendX + 12, 22);
            legendX += 95;
            var values = _series[key];
            var points = new List<PointF>();
            var startIndex = FindFirstAtOrAfter(values, viewStart);
            if (startIndex > 0)
            {
                startIndex--;
            }

            for (var index = startIndex; index < values.Count; index++)
            {
                var sample = values[index];
                if (sample.Timestamp > now)
                {
                    break;
                }

                if (!sample.Value.HasValue)
                {
                    DrawSegments(graphics, points, label.Color);
                    points.Clear();
                    continue;
                }

                var x = plot.Left + (float)((sample.Timestamp - viewStart).TotalSeconds / durationSeconds * plot.Width);
                var y = plot.Bottom - Math.Min(latencyScale, sample.Value.Value) / (float)latencyScale * plot.Height;
                points.Add(new PointF(x, y));
            }

            DrawSegments(graphics, points, label.Color);
        }

        using var eventPen = new Pen(Color.FromArgb(160, 204, 68, 52), 1F) { DashStyle = DashStyle.Dash };
        foreach (var marker in _events.Where(item => item.Timestamp >= viewStart && item.Timestamp <= now))
        {
            var x = plot.Left + (int)((marker.Timestamp - viewStart).TotalSeconds / durationSeconds * plot.Width);
            graphics.DrawLine(eventPen, x, plot.Top, x, plot.Bottom);
        }
    }

    private static void DrawTimeAxis(
        Graphics graphics,
        Rectangle plot,
        DateTime startedAt,
        DateTime now,
        double durationSeconds,
        Pen gridPen,
        Brush textBrush,
        Font font)
    {
        var availableTicks = Math.Clamp(plot.Width / 110, 2, 6);
        var targetInterval = durationSeconds / availableTicks;
        var intervalSeconds = new[]
        {
            1d, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 21600, 43200, 86400, 172800, 604800
        }.FirstOrDefault(interval => interval >= targetInterval);
        if (intervalSeconds == 0)
        {
            intervalSeconds = Math.Ceiling(targetInterval / 604800d) * 604800d;
        }

        var ticks = new List<DateTime> { startedAt };
        var nextTick = Math.Ceiling(durationSeconds / intervalSeconds) * intervalSeconds;
        for (var offset = intervalSeconds; offset < nextTick; offset += intervalSeconds)
        {
            if (offset < durationSeconds)
            {
                ticks.Add(startedAt.AddSeconds(offset));
            }
        }

        if (ticks[^1] != now)
        {
            ticks.Add(now);
        }

        var format = durationSeconds < 60 ? "HH:mm:ss" : durationSeconds < 86400 ? "HH:mm" : "dd/MM HH:mm";
        foreach (var tick in ticks)
        {
            var x = plot.Left + (int)((tick - startedAt).TotalSeconds / durationSeconds * plot.Width);
            graphics.DrawLine(gridPen, x, plot.Top, x, plot.Bottom);
            var label = tick.ToString(format, CultureInfo.CurrentCulture);
            var labelSize = graphics.MeasureString(label, font);
            var labelX = Math.Clamp(x - (int)(labelSize.Width / 2), plot.Left, Math.Max(plot.Left, plot.Right - (int)labelSize.Width));
            graphics.DrawString(label, font, textBrush, labelX, plot.Bottom + 5);
        }
    }

    private void ShowHoveredSeries(object? sender, MouseEventArgs e)
    {
        if (_sessionStartedAt is null || _series.Values.All(points => points.Count == 0))
        {
            return;
        }

        var plot = GetPlotRectangle();
        if (!plot.Contains(e.Location))
        {
            _hoverTip.Hide(this);
            _lastHoverText = null;
            return;
        }

        var now = DateTime.Now;
        var sessionStart = _sessionStartedAt.Value;
        var viewStart = _viewDuration.HasValue
            ? Max(sessionStart, now - _viewDuration.Value)
            : sessionStart;
        var durationSeconds = Math.Max(0.001, (now - viewStart).TotalSeconds);
        var cursorTime = viewStart.AddSeconds(Math.Clamp(
            (e.X - plot.Left) / (double)plot.Width * durationSeconds, 0, durationSeconds));
        var latencyScale = GetLatencyScale();
        var closestDistance = 12d;
        string? hoverText = null;

        foreach (var (key, samples) in _series)
        {
            if (samples.Count == 0)
            {
                continue;
            }

            var index = FindNearestSample(samples, cursorTime);
            var startIndex = Math.Max(0, index - 1);
            var endIndex = Math.Min(samples.Count - 2, index);
            if (samples[index].Timestamp < viewStart || samples[index].Timestamp > now)
            {
                continue;
            }

            if (samples.Count == 1)
            {
                startIndex = 0;
                endIndex = 0;
            }

            for (var segmentIndex = startIndex; segmentIndex <= endIndex; segmentIndex++)
            {
                var first = samples[segmentIndex];
                var second = samples[Math.Min(segmentIndex + 1, samples.Count - 1)];
                if (!first.Value.HasValue || !second.Value.HasValue)
                {
                    continue;
                }

                var firstX = plot.Left + (first.Timestamp - viewStart).TotalSeconds / durationSeconds * plot.Width;
                var secondX = plot.Left + (second.Timestamp - viewStart).TotalSeconds / durationSeconds * plot.Width;
                var firstY = plot.Bottom - Math.Min(latencyScale, first.Value.Value) / (double)latencyScale * plot.Height;
                var secondY = plot.Bottom - Math.Min(latencyScale, second.Value.Value) / (double)latencyScale * plot.Height;
                var fraction = secondX == firstX ? 0 : Math.Clamp((e.X - firstX) / (secondX - firstX), 0, 1);
                var pointX = firstX + (secondX - firstX) * fraction;
                var pointY = firstY + (secondY - firstY) * fraction;
                var distance = Math.Sqrt(Math.Pow(pointX - e.X, 2) + Math.Pow(pointY - e.Y, 2));
                if (distance >= closestDistance)
                {
                    continue;
                }

                closestDistance = distance;
                var label = _labels[key].Name;
                var latency = Math.Round(first.Value.Value + (second.Value.Value - first.Value.Value) * fraction);
                hoverText = $"{label} | Hora: {cursorTime:HH:mm:ss}{Environment.NewLine}" +
                            $"Latencia aproximada: {latency} ms";
            }
        }

        var hoveredEvent = _events
            .Where(item => Math.Abs((item.Timestamp - cursorTime).TotalSeconds) <= Math.Max(1, durationSeconds / plot.Width * 6))
            .OrderBy(item => Math.Abs((item.Timestamp - cursorTime).Ticks))
            .FirstOrDefault();
        if (hoveredEvent is not null)
        {
            hoverText = $"{hoveredEvent.Label} | Hora: {hoveredEvent.Timestamp:HH:mm:ss}{Environment.NewLine}Evento de latencia";
        }

        if (hoverText is null)
        {
            _hoverTip.Hide(this);
            _lastHoverText = null;
            return;
        }

        if (!string.Equals(hoverText, _lastHoverText, StringComparison.Ordinal))
        {
            _lastHoverText = hoverText;
            _hoverTip.Show(hoverText, this, e.Location.X + 14, e.Location.Y + 18, 10000);
        }
    }

    private Rectangle GetPlotRectangle()
    {
        const int left = 48;
        const int right = 16;
        const int top = 42;
        const int bottom = 40;
        return new Rectangle(left, top, Math.Max(1, ClientSize.Width - left - right),
            Math.Max(1, ClientSize.Height - top - bottom));
    }

    private void SetViewDuration(TimeSpan? duration)
    {
        _viewDuration = duration;
        _lastHoverText = null;
        _hoverTip.Hide(this);
        Invalidate();
    }

    private int GetLatencyScale()
    {
        var maximum = _series.Values.SelectMany(values => values)
            .Where(point => point.Value.HasValue)
            .Select(point => point.Value!.Value)
            .DefaultIfEmpty(0)
            .Max();
        if (maximum <= 200)
        {
            return 200;
        }

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(maximum / 4d)));
        var step = Math.Ceiling(maximum / (magnitude * 4)) * magnitude;
        return Math.Max(200, (int)(step * 4));
    }

    private static int FindFirstAtOrAfter(IReadOnlyList<SamplePoint> samples, DateTime timestamp)
    {
        var low = 0;
        var high = samples.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (samples[middle].Timestamp < timestamp)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static void ReducePoints(List<SamplePoint> samples)
    {
        const int bucketSize = 4;
        var reduced = new List<SamplePoint>(samples.Count / 2 + 2) { samples[0] };
        for (var offset = 1; offset < samples.Count; offset += bucketSize)
        {
            var count = Math.Min(bucketSize, samples.Count - offset);
            var bucket = samples.GetRange(offset, count);
            var candidates = new List<SamplePoint>();
            var missing = bucket.FirstOrDefault(point => !point.Value.HasValue);
            if (missing is not null)
            {
                candidates.Add(missing);
            }

            var successful = bucket.Where(point => point.Value.HasValue).ToArray();
            if (successful.Length > 0)
            {
                candidates.Add(successful.MinBy(point => point.Value) !);
                var maximum = successful.MaxBy(point => point.Value) !;
                if (maximum.Timestamp != candidates[^1].Timestamp)
                {
                    candidates.Add(maximum);
                }
            }

            foreach (var candidate in candidates.OrderBy(point => point.Timestamp))
            {
                if (candidate.Timestamp != reduced[^1].Timestamp)
                {
                    reduced.Add(candidate);
                }
            }
        }

        samples.Clear();
        samples.AddRange(reduced);
    }

    private static string FormatViewDuration(TimeSpan duration)
    {
        return duration.TotalHours >= 1 ? $"{(int)duration.TotalHours} h" : $"{(int)duration.TotalMinutes} min";
    }

    private static DateTime Max(DateTime first, DateTime second) => first > second ? first : second;

    private static int FindNearestSample(IReadOnlyList<SamplePoint> samples, DateTime timestamp)
    {
        var low = 0;
        var high = samples.Count - 1;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (samples[middle].Timestamp < timestamp)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        if (low > 0 && Math.Abs((samples[low - 1].Timestamp - timestamp).Ticks) <
            Math.Abs((samples[low].Timestamp - timestamp).Ticks))
        {
            return low - 1;
        }

        return low;
    }

    private static void DrawSegments(Graphics graphics, IReadOnlyList<PointF> points, Color color)
    {
        if (points.Count < 2)
        {
            return;
        }

        using var pen = new Pen(color, 2F);
        graphics.DrawLines(pen, points.ToArray());
    }
}
