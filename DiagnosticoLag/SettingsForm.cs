using System.Net.NetworkInformation;

namespace DiagnosticoLag;

internal sealed class SettingsForm : Form
{
    private sealed record InterfaceChoice(string? Id, string Name, bool Automatic)
    {
        public override string ToString() => Name;
    }

    private readonly TextBox _cloudflareAddress = new();
    private readonly TextBox _googleAddress = new();
    private readonly NumericUpDown _sampleInterval = new();
    private readonly ComboBox _networkInterface = new();
    private readonly CheckBox _includeLeagueInDiagnosis = new();

    public DiagnosticSettings Settings { get; private set; }

    public SettingsForm(DiagnosticSettings settings)
    {
        Settings = settings;
        Text = "Configuración de medición";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(540, 310);
        Font = new Font("Segoe UI", 9F);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 2,
            RowCount = 6
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        _cloudflareAddress.Text = settings.CloudflareAddress;
        _googleAddress.Text = settings.GoogleAddress;
        _sampleInterval.Minimum = 1;
        _sampleInterval.Maximum = 10;
        _sampleInterval.Value = settings.SampleIntervalSeconds;
        _sampleInterval.Width = 100;

        layout.Controls.Add(CreateLabel("Destino 1 (ICMP/TCP):"), 0, 0);
        layout.Controls.Add(_cloudflareAddress, 1, 0);
        layout.Controls.Add(CreateLabel("Destino 2 (ICMP/TCP):"), 0, 1);
        layout.Controls.Add(_googleAddress, 1, 1);
        layout.Controls.Add(CreateLabel("Intervalo entre muestras:"), 0, 2);
        layout.Controls.Add(_sampleInterval, 1, 2);
        layout.Controls.Add(CreateLabel("Interfaz de red:"), 0, 3);
        _networkInterface.DropDownStyle = ComboBoxStyle.DropDownList;
        _networkInterface.Dock = DockStyle.Fill;
        _networkInterface.Items.Add(new InterfaceChoice(null, "Automática (ruta de Windows)", true));
        foreach (var adapter in DiagnosticSettings.AvailableInterfaces())
        {
            _networkInterface.Items.Add(new InterfaceChoice(adapter.Id, $"{adapter.Name} ({adapter.NetworkInterfaceType})", false));
        }

        var selectedIndex = 0;
        for (var index = 1; index < _networkInterface.Items.Count; index++)
        {
            if (_networkInterface.Items[index] is InterfaceChoice choice &&
                string.Equals(choice.Id, settings.NetworkInterfaceId, StringComparison.Ordinal))
            {
                selectedIndex = index;
                break;
            }
        }
        _networkInterface.SelectedIndex = selectedIndex;
        layout.Controls.Add(_networkInterface, 1, 3);

        _includeLeagueInDiagnosis.Text = "Incluir destino TCP aproximado de League en el diagnóstico";
        _includeLeagueInDiagnosis.AutoSize = true;
        _includeLeagueInDiagnosis.Checked = settings.IncludeLeagueInDiagnosis;
        layout.Controls.Add(_includeLeagueInDiagnosis, 0, 4);
        layout.SetColumnSpan(_includeLeagueInDiagnosis, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var save = new Button { Text = "Guardar", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancelar", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        layout.Controls.Add(buttons, 0, 5);
        layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);

        AcceptButton = save;
        CancelButton = cancel;
        save.Click += SaveSettings;
    }

    private void SaveSettings(object? sender, EventArgs e)
    {
        var selectedInterface = (InterfaceChoice?)_networkInterface.SelectedItem;
        var updated = new DiagnosticSettings(
            _cloudflareAddress.Text.Trim(),
            _googleAddress.Text.Trim(),
            (int)_sampleInterval.Value,
            selectedInterface?.Id,
            _includeLeagueInDiagnosis.Checked);
        try
        {
            updated.Save();
            Settings = updated;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            DialogResult = DialogResult.None;
            MessageBox.Show(this, exception.Message, "No se pudo guardar la configuración",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static Label CreateLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        TextAlign = ContentAlignment.MiddleLeft
    };
}
