using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Setup;

internal sealed class SetupForm : Form
{
    private static readonly Color WindowBackground = Color.FromArgb(11, 18, 32);
    private static readonly Color Surface = Color.FromArgb(17, 27, 44);
    private static readonly Color SurfaceMuted = Color.FromArgb(23, 35, 55);
    private static readonly Color TextPrimary = Color.FromArgb(247, 250, 252);
    private static readonly Color TextSecondary = Color.FromArgb(174, 180, 191);
    private static readonly Color Border = Color.FromArgb(42, 57, 80);
    private static readonly Color Accent = Color.FromArgb(11, 112, 201);
    private static readonly Color Success = Color.FromArgb(87, 209, 124);
    private static readonly Color Error = Color.FromArgb(255, 112, 112);

    private readonly Func<Task<string>> install;
    private readonly Button primaryButton;
    private readonly Button cancelButton;
    private readonly Label statusLabel;
    private readonly ProgressBar progressBar;
    private readonly CheckBox launchCheckBox;
    private bool isBusy;
    private bool installationCompleted;

    public SetupForm(
        LauncherSetupAction action,
        string version,
        string? installedVersion,
        string publisher,
        PerUserInstallLayout layout,
        Func<Task<string>> install)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(publisher);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(install);

        this.install = install;
        Text = $"{ModBridgeProductIdentity.ProductName} setup";
        AccessibleName = $"{ModBridgeProductIdentity.ProductName} setup";
        Icon = Environment.ProcessPath is { } processPath
            ? Icon.ExtractAssociatedIcon(processPath)
            : null;
        BackColor = WindowBackground;
        ForeColor = TextPrimary;
        Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(700, 560);
        MinimumSize = new Size(620, 500);
        MaximizeBox = false;

        var root = new TableLayoutPanel
        {
            AutoScroll = true,
            BackColor = WindowBackground,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(34, 28, 34, 26),
            RowCount = 8,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(CreateLabel(ModBridgeProductIdentity.ProductName, 24F, FontStyle.Bold, TextPrimary));
        var descriptor = CreateLabel(ModBridgeProductIdentity.Descriptor, 11F, FontStyle.Bold, Accent);
        descriptor.Margin = new Padding(0, 3, 0, 18);
        root.Controls.Add(descriptor);

        var introduction = CreateLabel(
            action switch
            {
                LauncherSetupAction.Install => "Install this per-user Windows application?",
                LauncherSetupAction.Update => "Update the installed application with this version?",
                _ => "Repair or reinstall the current application files?",
            },
            15F,
            FontStyle.Bold,
            TextPrimary);
        introduction.Margin = new Padding(0, 0, 0, 14);
        introduction.MaximumSize = new Size(500, 0);
        root.Controls.Add(introduction);

        var details = CreateDetails(version, installedVersion, publisher, layout);
        details.Margin = new Padding(0, 0, 0, 14);
        root.Controls.Add(details);

        var boundary = CreateLabel(
            "Setup installs only for your Windows account and does not require administrator privileges. "
            + "It does not install, remove, or change the Community Mod in your game directory.",
            9.5F,
            FontStyle.Regular,
            TextSecondary);
        boundary.BackColor = SurfaceMuted;
        boundary.MaximumSize = new Size(500, 0);
        boundary.Padding = new Padding(14, 12, 14, 12);
        boundary.Margin = new Padding(0, 0, 0, 14);
        root.Controls.Add(boundary);

        progressBar = new ProgressBar
        {
            AccessibleName = "Setup progress",
            Dock = DockStyle.Top,
            Height = 5,
            MarqueeAnimationSpeed = 22,
            Margin = new Padding(0, 0, 0, 9),
            Style = ProgressBarStyle.Marquee,
            Visible = false,
        };
        root.Controls.Add(progressBar);

        var statusPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            BackColor = WindowBackground,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            Margin = Padding.Empty,
            WrapContents = false,
        };
        statusLabel = CreateLabel("Nothing has been changed yet.", 9.5F, FontStyle.Regular, TextSecondary);
        statusLabel.AccessibleName = "Setup status";
        statusLabel.MaximumSize = new Size(500, 0);
        statusPanel.Controls.Add(statusLabel);
        launchCheckBox = new CheckBox
        {
            AccessibleName = "Launch STFC Mod Bridge after setup",
            AutoSize = true,
            Checked = true,
            ForeColor = TextPrimary,
            Margin = new Padding(0, 10, 0, 0),
            Text = "Launch STFC Mod Bridge",
            UseVisualStyleBackColor = false,
            Visible = false,
        };
        statusPanel.Controls.Add(launchCheckBox);
        root.Controls.Add(statusPanel);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            BackColor = WindowBackground,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = new Padding(0, 18, 0, 0),
            WrapContents = false,
        };
        primaryButton = CreateButton(action.ToString(), Accent, Color.White);
        primaryButton.AccessibleName = $"{action} STFC Mod Bridge";
        primaryButton.Click += PrimaryButton_Click;
        cancelButton = CreateButton("Cancel", Surface, TextPrimary);
        cancelButton.AccessibleName = "Cancel setup";
        cancelButton.DialogResult = DialogResult.Cancel;
        actions.Controls.Add(primaryButton);
        actions.Controls.Add(cancelButton);
        root.Controls.Add(actions);

        Controls.Add(root);
        AcceptButton = primaryButton;
        CancelButton = cancelButton;
        FormClosing += SetupForm_FormClosing;
    }

    public bool LaunchRequested => installationCompleted && launchCheckBox.Checked;

    private async void PrimaryButton_Click(object? sender, EventArgs e)
    {
        if (installationCompleted)
        {
            DialogResult = DialogResult.OK;
            Close();
            return;
        }

        SetBusy(true);
        statusLabel.ForeColor = TextSecondary;
        statusLabel.Text = "Verifying and installing the signed application files…";
        try
        {
            _ = await install();
            installationCompleted = true;
            SetBusy(false);
            statusLabel.ForeColor = Success;
            statusLabel.Text = "STFC Mod Bridge is installed and registered for this Windows account.";
            launchCheckBox.Visible = true;
            primaryButton.Text = "Finish";
            primaryButton.AccessibleName = "Finish setup";
            cancelButton.Visible = false;
            primaryButton.Enabled = true;
            primaryButton.Focus();
        }
        catch (Exception exception)
        {
            progressBar.Visible = false;
            statusLabel.ForeColor = Error;
            statusLabel.Text = $"Setup did not complete: {exception.Message}";
            primaryButton.Text = "Try again";
            primaryButton.AccessibleName = "Try setup again";
            cancelButton.Text = "Close";
            cancelButton.AccessibleName = "Close setup";
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        isBusy = busy;
        progressBar.Visible = busy;
        primaryButton.Enabled = !busy;
        cancelButton.Enabled = !busy;
        ControlBox = !busy;
    }

    private void SetupForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (isBusy)
        {
            e.Cancel = true;
        }
    }

    private static TableLayoutPanel CreateDetails(
        string version,
        string? installedVersion,
        string publisher,
        PerUserInstallLayout layout)
    {
        var hasInstalledVersion = !string.IsNullOrWhiteSpace(installedVersion);
        var details = new TableLayoutPanel
        {
            AutoSize = true,
            BackColor = Surface,
            CellBorderStyle = TableLayoutPanelCellBorderStyle.Single,
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Padding = new Padding(1),
            RowCount = hasInstalledVersion ? 5 : 4,
        };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddDetail(details, 0, "Setup version", version);
        var row = 1;
        if (hasInstalledVersion)
        {
            AddDetail(details, row++, "Installed", LauncherInstalledProduct.NormalizeVersion(installedVersion) ?? "Unknown");
        }
        AddDetail(details, row++, "Verified publisher", publisher);
        AddDetail(details, row++, "Application", layout.ProgramDirectory);
        AddDetail(details, row, "Local data", layout.StateDirectory);
        return details;
    }

    private static void AddDetail(TableLayoutPanel details, int row, string name, string value)
    {
        var nameLabel = CreateLabel(name, 9F, FontStyle.Bold, TextSecondary);
        var valueLabel = CreateLabel(value, 9F, FontStyle.Regular, TextPrimary);
        valueLabel.MaximumSize = new Size(350, 0);
        nameLabel.Margin = new Padding(11, 9, 8, 9);
        valueLabel.Margin = new Padding(8, 9, 11, 9);
        details.Controls.Add(nameLabel, 0, row);
        details.Controls.Add(valueLabel, 1, row);
    }

    private static Label CreateLabel(string text, float size, FontStyle style, Color color) =>
        new()
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", size, style, GraphicsUnit.Point),
            ForeColor = color,
            Text = text,
        };

    private static Button CreateButton(string text, Color background, Color foreground)
    {
        var button = new Button
        {
            AutoSize = true,
            BackColor = background,
            FlatStyle = FlatStyle.Flat,
            ForeColor = foreground,
            Margin = new Padding(10, 0, 0, 0),
            MinimumSize = new Size(118, 44),
            Padding = new Padding(16, 7, 16, 7),
            Text = text,
            UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(10, 104, 184);
        return button;
    }
}
