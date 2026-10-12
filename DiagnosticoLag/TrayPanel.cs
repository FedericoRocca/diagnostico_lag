namespace DiagnosticoLag;

internal sealed class TrayPanel : Form
{
    private readonly Button _start = new();
    private readonly Button _pause = new();
    private readonly Button _resume = new();
    private readonly Button _partial = new();
    private readonly Button _finish = new();
    private readonly Label _status = new();

    public TrayPanel(Func<Task> start, Action pause, Action resume, Action partial, Func<Task> finish)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(420, 250);
        BackColor = Color.White;
        Padding = new Padding(1);

        Chart.Dock = DockStyle.Fill;
        Chart.BackColor = Color.White;
        Chart.CompactTimeAxis = true;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(4, 4, 0, 0)
        };
        Configure(_start, async () => await start(), Color.FromArgb(28, 121, 91));
        Configure(_pause, pause, Color.FromArgb(78, 96, 120));
        Configure(_resume, resume, Color.FromArgb(28, 121, 91));
        Configure(_partial, partial, Color.FromArgb(57, 91, 145));
        Configure(_finish, async () => await finish(), Color.FromArgb(175, 59, 59));
        buttons.Controls.AddRange([_start, _pause, _resume, _partial, _finish]);

        _status.Dock = DockStyle.Top;
        _status.Height = 24;
        _status.Padding = new Padding(6, 4, 0, 0);
        _status.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold);

        Controls.Add(Chart);
        Controls.Add(buttons);
        Controls.Add(_status);
        ApplyLocalization();
    }

    public LatencyChart Chart { get; } = new();

    protected override bool ShowWithoutActivation => false;

    public void ApplyLocalization()
    {
        _start.Text = Localization.T("Iniciar");
        _pause.Text = Localization.T("Pausar");
        _resume.Text = Localization.T("Continuar");
        _partial.Text = Localization.T("Parcial");
        _finish.Text = Localization.T("Finalizar");
        Chart.ApplyLocalization();
    }

    public void SetState(string status, bool start, bool pause, bool resume, bool partial, bool finish)
    {
        _status.Text = status;
        _start.Enabled = start;
        _pause.Enabled = pause;
        _resume.Enabled = resume;
        _partial.Enabled = partial;
        _finish.Enabled = finish;
    }

    public void ShowNearTray()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Right - Width - 8, area.Bottom - Height - 8);
        Show();
        Activate();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Hide();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.DrawRectangle(Pens.SlateGray, 0, 0, Width - 1, Height - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Chart.Dispose();
        }

        base.Dispose(disposing);
    }

    private static void Configure(Button button, Action action, Color color)
    {
        button.AutoSize = true;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = color;
        button.ForeColor = Color.White;
        button.Margin = new Padding(2, 0, 2, 0);
        button.Padding = new Padding(4, 0, 4, 0);
        button.Click += (_, _) => action();
    }
}
