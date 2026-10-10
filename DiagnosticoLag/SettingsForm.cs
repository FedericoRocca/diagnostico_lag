namespace DiagnosticoLag;

internal sealed class SettingsForm : Form
{
    private sealed record InterfaceChoice(string? Id, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record LanguageChoice(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    private readonly TextBox _cloudflareAddress = new();
    private readonly TextBox _googleAddress = new();
    private readonly NumericUpDown _sampleInterval = new();
    private readonly ComboBox _networkInterface = new();
    private readonly ComboBox _language = new();
    private readonly CheckedListBox _gameProfiles = new();
    private readonly TextBox _logDirectory = new();
    private readonly TextBox _csvDirectory = new();

    public DiagnosticSettings Settings { get; private set; }

    public SettingsForm(DiagnosticSettings settings)
    {
        Settings = settings;
        Text = Localization.T("Configuración");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(720, 552);
        Font = new Font("Segoe UI", 9F);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 3,
            RowCount = 9
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 185));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 145));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        _cloudflareAddress.Text = settings.CloudflareAddress;
        _cloudflareAddress.Dock = DockStyle.Fill;
        _googleAddress.Text = settings.GoogleAddress;
        _googleAddress.Dock = DockStyle.Fill;
        _sampleInterval.Minimum = 50;
        _sampleInterval.Maximum = 10000;
        _sampleInterval.Increment = 50;
        _sampleInterval.ReadOnly = false;
        _sampleInterval.Value = settings.SampleIntervalMilliseconds;
        _sampleInterval.Width = 100;

        layout.Controls.Add(CreateLabel("Idioma:"), 0, 0);
        _language.DropDownStyle = ComboBoxStyle.DropDownList;
        _language.Dock = DockStyle.Fill;
        _language.Items.Add(new LanguageChoice("es", "Español"));
        _language.Items.Add(new LanguageChoice("en", "English"));
        _language.SelectedIndex = settings.Language == "en" ? 1 : 0;
        layout.Controls.Add(_language, 1, 0);
        layout.SetColumnSpan(_language, 2);

        layout.Controls.Add(CreateLabel("Destino 1 (ICMP/TCP):"), 0, 1);
        layout.Controls.Add(_cloudflareAddress, 1, 1);
        layout.SetColumnSpan(_cloudflareAddress, 2);
        layout.Controls.Add(CreateLabel("Destino 2 (ICMP/TCP):"), 0, 2);
        layout.Controls.Add(_googleAddress, 1, 2);
        layout.SetColumnSpan(_googleAddress, 2);
        layout.Controls.Add(CreateLabel("Intervalo entre muestras (ms):"), 0, 3);
        layout.Controls.Add(_sampleInterval, 1, 3);
        layout.Controls.Add(CreateLabel("Interfaz de red:"), 0, 4);
        _networkInterface.DropDownStyle = ComboBoxStyle.DropDownList;
        _networkInterface.Dock = DockStyle.Fill;
        _networkInterface.Items.Add(new InterfaceChoice(null, Localization.T("Automática (ruta de Windows)")));
        foreach (var adapter in DiagnosticSettings.AvailableInterfaces())
        {
            _networkInterface.Items.Add(new InterfaceChoice(adapter.Id, $"{adapter.Name} ({adapter.NetworkInterfaceType})"));
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
        layout.Controls.Add(_networkInterface, 1, 4);
        layout.SetColumnSpan(_networkInterface, 2);

        var games = new GroupBox
        {
            Text = Localization.T("Perfiles de juegos (ICMP es referencia, no ping real de partida)"),
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 8, 12, 6)
        };
        _gameProfiles.Dock = DockStyle.Fill;
        _gameProfiles.CheckOnClick = true;
        _gameProfiles.IntegralHeight = false;
        foreach (var profile in settings.GameProfiles)
        {
            _gameProfiles.Items.Add(profile, profile.Enabled);
        }

        var profileButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 104,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false
        };
        var addProfile = new Button { Text = Localization.T("Agregar…"), Width = 96, Height = 30 };
        var editProfile = new Button { Text = Localization.T("Editar…"), Width = 96, Height = 30 };
        var removeProfile = new Button { Text = Localization.T("Quitar"), Width = 96, Height = 30 };
        profileButtons.Controls.Add(addProfile);
        profileButtons.Controls.Add(editProfile);
        profileButtons.Controls.Add(removeProfile);
        games.Controls.Add(_gameProfiles);
        games.Controls.Add(profileButtons);
        layout.Controls.Add(games, 0, 5);
        layout.SetColumnSpan(games, 3);
        addProfile.Click += (_, _) => AddProfile();
        editProfile.Click += (_, _) => EditProfile();
        removeProfile.Click += (_, _) => RemoveProfile();
        _gameProfiles.SelectedIndexChanged += (_, _) =>
        {
            var selected = _gameProfiles.SelectedIndex >= 0;
            editProfile.Enabled = selected;
            removeProfile.Enabled = selected && !IsBuiltInProfile(GetSelectedProfile());
        };
        editProfile.Enabled = false;
        removeProfile.Enabled = false;

        _logDirectory.Text = settings.LogDirectory;
        _logDirectory.Dock = DockStyle.Fill;
        _csvDirectory.Text = settings.CsvDirectory;
        _csvDirectory.Dock = DockStyle.Fill;
        layout.Controls.Add(CreateLabel("Carpeta de registros:"), 0, 6);
        layout.Controls.Add(_logDirectory, 1, 6);
        layout.Controls.Add(CreateBrowseButton(_logDirectory), 2, 6);
        layout.Controls.Add(CreateLabel("Carpeta de archivos CSV:"), 0, 7);
        layout.Controls.Add(_csvDirectory, 1, 7);
        layout.Controls.Add(CreateBrowseButton(_csvDirectory), 2, 7);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var save = new Button { Text = Localization.T("Guardar"), AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = Localization.T("Cancelar"), AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        layout.Controls.Add(buttons, 0, 8);
        layout.SetColumnSpan(buttons, 3);
        Controls.Add(layout);

        AcceptButton = save;
        CancelButton = cancel;
        save.Click += SaveSettings;
    }

    private static Button CreateBrowseButton(TextBox path)
    {
        var button = new Button
        {
            Text = Localization.T("Examinar…"),
            Dock = DockStyle.Fill,
            Margin = new Padding(4, 2, 0, 2)
        };
        button.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = Localization.T("Seleccioná la carpeta donde se guardarán los archivos."),
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(path.Text) ? path.Text : string.Empty
            };
            if (dialog.ShowDialog(path.FindForm()) == DialogResult.OK)
            {
                path.Text = dialog.SelectedPath;
            }
        };
        return button;
    }

    private void SaveSettings(object? sender, EventArgs e)
    {
        var selectedInterface = (InterfaceChoice?)_networkInterface.SelectedItem;
        var profiles = Enumerable.Range(0, _gameProfiles.Items.Count)
            .Select(index => ((GameMonitoringProfile)_gameProfiles.Items[index]!) with
            {
                Enabled = _gameProfiles.GetItemChecked(index)
            })
            .ToArray();
        var updated = new DiagnosticSettings(
            _cloudflareAddress.Text.Trim(),
            _googleAddress.Text.Trim(),
            (int)_sampleInterval.Value,
            selectedInterface?.Id)
        {
            GameProfiles = profiles,
            LogDirectory = _logDirectory.Text.Trim(),
            CsvDirectory = _csvDirectory.Text.Trim(),
            Language = ((LanguageChoice)_language.SelectedItem!).Id
        };
        try
        {
            if (updated.SampleIntervalMilliseconds <= 500)
            {
                MessageBox.Show(this,
                    Localization.T("Un intervalo de 500 ms o menos aumenta el tamaño de los logs y el uso de recursos."),
                    Localization.T("Advertencia de intervalo"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            updated.Save();
            Settings = updated;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            DialogResult = DialogResult.None;
            MessageBox.Show(this, Localization.T(exception.Message), Localization.T("No se pudo guardar la configuración"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void AddProfile()
    {
        using var dialog = new GameProfileForm(new GameMonitoringProfile(
            Guid.NewGuid().ToString("N"), "", Array.Empty<string>(), false));
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        AddProfileToList(dialog.Profile, false);
    }

    private void EditProfile()
    {
        var index = _gameProfiles.SelectedIndex;
        if (index < 0 || _gameProfiles.Items[index] is not GameMonitoringProfile profile)
        {
            return;
        }

        using var dialog = new GameProfileForm(profile);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var enabled = _gameProfiles.GetItemChecked(index);
        _gameProfiles.Items.RemoveAt(index);
        AddProfileToList(dialog.Profile with { Enabled = enabled }, enabled, Math.Min(index, _gameProfiles.Items.Count));
    }

    private void RemoveProfile()
    {
        if (_gameProfiles.SelectedIndex >= 0 && !IsBuiltInProfile(GetSelectedProfile()))
        {
            _gameProfiles.Items.RemoveAt(_gameProfiles.SelectedIndex);
        }
    }

    private void AddProfileToList(GameMonitoringProfile profile, bool enabled, int? index = null)
    {
        var insertionIndex = index ?? _gameProfiles.Items.Count;
        _gameProfiles.Items.Insert(insertionIndex, profile with { Enabled = enabled });
        _gameProfiles.SetItemChecked(insertionIndex, enabled);
        _gameProfiles.SelectedIndex = insertionIndex;
    }

    private GameMonitoringProfile? GetSelectedProfile() =>
        _gameProfiles.SelectedItem as GameMonitoringProfile;

    private static bool IsBuiltInProfile(GameMonitoringProfile? profile) =>
        profile?.Id == GameMonitoringProfile.LeagueOfLegends.Id;

    private static Label CreateLabel(string text) => new()
    {
        Text = Localization.T(text),
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        TextAlign = ContentAlignment.MiddleLeft
    };
}

internal sealed class GameProfileForm : Form
{
    private readonly TextBox _name = new();
    private readonly TextBox _processNames = new();

    public GameMonitoringProfile Profile { get; private set; }

    public GameProfileForm(GameMonitoringProfile profile)
    {
        Profile = profile;
        Text = Localization.T(profile.Name.Length == 0 ? "Agregar perfil de juego" : "Editar perfil de juego");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(500, 255);
        Font = new Font("Segoe UI", 9F);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 4
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.Controls.Add(new Label { Text = Localization.T("Nombre del juego"), AutoSize = true }, 0, 0);
        _name.Text = profile.Name;
        _name.Dock = DockStyle.Fill;
        layout.Controls.Add(_name, 0, 1);
        var processPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        processPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        processPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        processPanel.Controls.Add(new Label
        {
            Text = Localization.T("Procesos (sin .exe), separados por coma o punto y coma; se observan conexiones TCP"),
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        _processNames.Text = string.Join("; ", profile.ProcessNames);
        _processNames.Dock = DockStyle.Fill;
        processPanel.Controls.Add(_processNames, 0, 1);
        layout.Controls.Add(processPanel, 0, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var save = new Button { Text = Localization.T("Guardar"), AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = Localization.T("Cancelar"), AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
        save.Click += SaveProfile;
    }

    private void SaveProfile(object? sender, EventArgs e)
    {
        var processNames = _processNames.Text
            .Split([',', ';', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var name = _name.Text.Trim();
        if (name.Length is 0 or > 80 || processNames.Length is 0 or > 8 ||
            processNames.Any(item => item.Length > 128))
        {
            DialogResult = DialogResult.None;
            MessageBox.Show(this,
                Localization.T("Ingresá un nombre de hasta 80 caracteres y de 1 a 8 nombres de proceso válidos."),
                Localization.T("Perfil incompleto"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Profile = Profile with { Name = name, ProcessNames = processNames };
    }
}
