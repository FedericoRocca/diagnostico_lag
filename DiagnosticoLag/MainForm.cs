using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;

namespace DiagnosticoLag;

internal sealed class MainForm : Form
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly DataGridView _grid = new();
    private readonly LatencyChart _chart = new();
    private readonly Label _status = new();
    private readonly Label _diagnosis = new();
    private readonly RichTextBox _details = new();
    private readonly Button _startButton = new();
    private readonly Button _pauseButton = new();
    private readonly Button _resumeButton = new();
    private readonly Button _finishButton = new();
    private readonly Button _reportButton = new();
    private readonly string _logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiagnosticoLag", "registro_latencia.txt");
    private DiagnosticSession? _session;
    private CancellationTokenSource? _samplingCancellation;
    private bool _sampling;
    private bool _loggingAvailable = true;
    private bool _closing;

    public MainForm()
    {
        Text = "Diagnóstico de red para gaming";
        MinimumSize = new Size(980, 680);
        Size = new Size(1220, 850);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.FromArgb(245, 247, 250);

        BuildInterface();
        _timer.Tick += async (_, _) => await SampleOnceAsync();
        FormClosing += OnFormClosing;
        SetMonitoringControls(false, false);
    }

    private void BuildInterface()
    {
        var layout = new TableLayoutPanel
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
            Text = "DIAGNÓSTICO DE RED",
            Font = new Font("Segoe UI Semibold", 15F, FontStyle.Bold),
            ForeColor = Color.FromArgb(24, 39, 61),
            AutoSize = true,
            Location = new Point(0, 2)
        };
        _status.Text = "Listo para iniciar";
        _status.AutoSize = true;
        _status.ForeColor = Color.FromArgb(88, 104, 126);
        _status.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _status.Location = new Point(heading.Width - 300, 12);
        heading.Resize += (_, _) => _status.Location = new Point(heading.ClientSize.Width - _status.Width - 8, 13);
        heading.Controls.Add(title);
        heading.Controls.Add(_status);
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
        var openLogButton = new Button { Text = "Abrir registro", AutoSize = true, Height = 34, Margin = new Padding(6, 0, 0, 0) };
        toolbar.Controls.AddRange([_startButton, _pauseButton, _resumeButton, _finishButton, _reportButton, openLogButton]);
        layout.Controls.Add(toolbar, 0, 3);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 6, 0, 0) };
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
        _finishButton.Click += (_, _) => FinishMonitoring();
        _reportButton.Click += (_, _) => ShowReport("parcial");
        openLogButton.Click += (_, _) => OpenLog();
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
            _session = await DiagnosticSession.CreateAsync(_samplingCancellation.Token);
            _grid.Rows.Clear();
            _chart.StartSession(_session.StartedAt);
            _details.Clear();
            AppendLog($"--- NUEVA SESIÓN: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---{Environment.NewLine}" +
                      $"Conexión: {_session.Connection.Type} ({_session.Connection.Detail}) | Router: {_session.RouterAddress} | " +
                      $"Primer salto ISP: {_session.IspAddress ?? "no detectado"}{Environment.NewLine}" +
                      $"Registro: {_logPath}{Environment.NewLine}");
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
            _status.Text = "Inicio cancelado";
            SetMonitoringControls(false, false);
        }
        catch (Exception exception)
        {
            _samplingCancellation.Dispose();
            _samplingCancellation = null;
            _status.Text = "No se pudo iniciar";
            SetMonitoringControls(false, false);
            MessageBox.Show(this, exception.Message, "Error al iniciar el diagnóstico", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task SampleOnceAsync()
    {
        if (_session is null || _samplingCancellation is null || _sampling || _closing)
        {
            return;
        }

        _sampling = true;
        try
        {
            var snapshot = await _session.SampleAsync(_samplingCancellation.Token);
            UpdateDashboard(snapshot);
            AppendLog(FormatLiveLog(snapshot));
            if (_session.PeriodicLogIsDue())
            {
                AppendLog(_session.BuildReport("periódico"));
            }
        }
        catch (OperationCanceledException) when (_samplingCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _timer.Stop();
            _status.Text = "Error durante la medición";
            MessageBox.Show(this, exception.Message, "Error durante el diagnóstico", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _sampling = false;
        }
    }

    private void UpdateDashboard(MonitorSnapshot snapshot)
    {
        _grid.Rows.Clear();
        foreach (var target in snapshot.Targets)
        {
            var stats = target.Statistics;
            var row = _grid.Rows.Add(
                target.Target.Name,
                stats.Samples,
                stats.Average.ToString("F1"),
                stats.Median,
                stats.P95,
                stats.P99,
                stats.Maximum,
                stats.Jitter.ToString("F1"),
                stats.Spikes80 + stats.Spikes120,
                $"{stats.LossPercent:F1}%");
            _grid.Rows[row].Cells["loss"].Style.ForeColor = stats.LossPercent >= 5
                ? Color.Firebrick
                : stats.LossPercent > 0.5 ? Color.DarkGoldenrod : Color.DarkGreen;
        }

        _chart.AddSample(DateTime.Now, snapshot.LastMeasurements);
        var diagnosis = snapshot.Diagnosis;
        _diagnosis.Text = $"{diagnosis.Title}{Environment.NewLine}{diagnosis.Explanation}" +
                          (diagnosis.Reasons.Count == 0 ? "" : $"{Environment.NewLine}{Environment.NewLine}• {string.Join($"{Environment.NewLine}• ", diagnosis.Reasons.Take(4))}");
        _diagnosis.ForeColor = diagnosis.Level switch
        {
            "PROBLEMA" => Color.Firebrick,
            "ATENCION" => Color.DarkGoldenrod,
            _ => Color.DarkGreen
        };
        _details.Text = $"Duración: {snapshot.Duration:hh\\:mm\\:ss} | Línea base: " +
                        (snapshot.SamplesUntilBaseline == 0 ? "lista" : $"en {snapshot.SamplesUntilBaseline} muestras") +
                        $"{Environment.NewLine}{Environment.NewLine}Eventos recientes{Environment.NewLine}" +
                        (snapshot.RecentEvents.Count == 0 ? "Sin pérdidas ni picos ≥120 ms." : string.Join(Environment.NewLine, snapshot.RecentEvents.TakeLast(10)));
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

    private void FinishMonitoring()
    {
        _timer.Stop();
        if (_session is not null)
        {
            ShowReport("final");
            _session = null;
        }

        _samplingCancellation?.Cancel();
        _samplingCancellation?.Dispose();
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
        AppendLog(report);
        using var window = new ReportForm(report);
        window.ShowDialog(this);
    }

    private void OpenLog()
    {
        if (!File.Exists(_logPath))
        {
            MessageBox.Show(this, "Todavía no hay un registro. Iniciá el monitoreo primero.", "Registro de diagnóstico",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = _logPath, UseShellExecute = true });
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
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllText(_logPath, text + Environment.NewLine, new UTF8Encoding(false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _loggingAvailable = false;
            _status.Text = "No se pudo escribir el registro";
            MessageBox.Show(this, $"El monitoreo sigue, pero no se pudo guardar el registro en:{Environment.NewLine}{_logPath}{Environment.NewLine}{Environment.NewLine}{exception.Message}",
                "Error de registro", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetMonitoringControls(bool active, bool paused)
    {
        _startButton.Enabled = !active;
        _pauseButton.Enabled = active && !paused;
        _resumeButton.Enabled = active && paused;
        _finishButton.Enabled = active;
        _reportButton.Enabled = active && _session is not null;
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
        _samplingCancellation?.Dispose();
    }
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

    private readonly Dictionary<string, List<SamplePoint>> _series = new(StringComparer.Ordinal)
    {
        ["router"] = new List<SamplePoint>(),
        ["cloudflare-icmp"] = new List<SamplePoint>(),
        ["google-icmp"] = new List<SamplePoint>()
    };
    private readonly Dictionary<string, (string Name, Color Color)> _labels = new(StringComparer.Ordinal)
    {
        ["router"] = ("Router", Color.FromArgb(31, 132, 97)),
        ["cloudflare-icmp"] = ("Cloudflare", Color.FromArgb(61, 117, 190)),
        ["google-icmp"] = ("Google", Color.FromArgb(218, 137, 51))
    };
    private readonly ToolTip _hoverTip = new() { InitialDelay = 250, ReshowDelay = 100, AutoPopDelay = 10000, ShowAlways = true };
    private DateTime? _sessionStartedAt;
    private string? _lastHoverText;

    public LatencyChart()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
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

        _sessionStartedAt = startedAt;
        _lastHoverText = null;
        _hoverTip.Hide(this);
        Invalidate();
    }

    public void AddSample(DateTime timestamp, IReadOnlyDictionary<string, int> measurements)
    {
        foreach (var key in _series.Keys)
        {
            var values = _series[key];
            values.Add(new SamplePoint(timestamp,
                measurements.TryGetValue(key, out var value) && value >= 0 ? value : null));
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
        graphics.DrawString("Latencia ICMP en vivo (desde el inicio de la sesión; escala hasta 200 ms)", titleFont, textBrush, 12, 10);

        const int left = 48;
        const int right = 16;
        const int top = 42;
        const int bottom = 40;
        var plot = new Rectangle(left, top, Math.Max(1, ClientSize.Width - left - right), Math.Max(1, ClientSize.Height - top - bottom));
        for (var value = 0; value <= 200; value += 50)
        {
            var y = plot.Bottom - (int)(value / 200f * plot.Height);
            graphics.DrawLine(gridPen, plot.Left, y, plot.Right, y);
            graphics.DrawString(value.ToString(CultureInfo.InvariantCulture), font, textBrush, 8, y - 8);
        }

        var now = DateTime.Now;
        var startedAt = _sessionStartedAt ?? now;
        var durationSeconds = Math.Max(1, (now - startedAt).TotalSeconds);
        DrawTimeAxis(graphics, plot, startedAt, now, durationSeconds, gridPen, textBrush, font);
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
            foreach (var sample in values)
            {
                if (!sample.Value.HasValue)
                {
                    DrawSegments(graphics, points, label.Color);
                    points.Clear();
                    continue;
                }

                var x = plot.Left + (float)((sample.Timestamp - startedAt).TotalSeconds / durationSeconds * plot.Width);
                var y = plot.Bottom - Math.Min(200, sample.Value.Value) / 200f * plot.Height;
                points.Add(new PointF(x, y));
            }

            DrawSegments(graphics, points, label.Color);
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

        var startedAt = _sessionStartedAt.Value;
        var now = DateTime.Now;
        var durationSeconds = Math.Max(1, (now - startedAt).TotalSeconds);
        var cursorTime = startedAt.AddSeconds(Math.Clamp(
            (e.X - plot.Left) / (double)plot.Width * durationSeconds, 0, durationSeconds));
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

                var firstX = plot.Left + (first.Timestamp - startedAt).TotalSeconds / durationSeconds * plot.Width;
                var secondX = plot.Left + (second.Timestamp - startedAt).TotalSeconds / durationSeconds * plot.Width;
                var firstY = plot.Bottom - Math.Min(200, first.Value.Value) / 200d * plot.Height;
                var secondY = plot.Bottom - Math.Min(200, second.Value.Value) / 200d * plot.Height;
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
