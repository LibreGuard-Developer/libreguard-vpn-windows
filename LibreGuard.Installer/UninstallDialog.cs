using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace LibreGuard.Installer;

internal sealed class UninstallDialog : Form
{
    private readonly Func<bool, Task<int>> _uninstallAsync;
    private readonly CheckBox _removeDataCheckBox;
    private readonly Button _uninstallButton;
    private readonly Button _cancelButton;
    private readonly LinkLabel _supportLinkLabel;

    public UninstallDialog(Func<bool, Task<int>> uninstallAsync)
    {
        _uninstallAsync = uninstallAsync;

        Text = "LibreGuard VPN Uninstaller";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(560, 320);
        Font = SystemFonts.MessageBoxFont;

        var titleLabel = new Label
        {
            AutoSize = false,
            Location = new Point(24, 20),
            Size = new Size(512, 34),
            Text = "Remove LibreGuard VPN from this PC",
            Font = new Font(Font, FontStyle.Bold)
        };

        var descriptionLabel = new Label
        {
            AutoSize = false,
            Location = new Point(24, 60),
            Size = new Size(512, 84),
            Text = "If you uninstall, LibreGuard will remove the app, service, and shortcuts. You can also choose whether to delete saved preferences and account data.",
        };

        _removeDataCheckBox = new CheckBox
        {
            AutoSize = true,
            Location = new Point(24, 154),
            Text = "Remove saved preferences and account data",
            Checked = true
        };

        var feedbackLabel = new Label
        {
            AutoSize = false,
            Location = new Point(24, 186),
            Size = new Size(512, 42),
            Text = "If you have a moment, we would really appreciate hearing why you are uninstalling or any feedback you have.",
        };

        _supportLinkLabel = new LinkLabel
        {
            AutoSize = true,
            Location = new Point(24, 236),
            Text = "Email support@libreguard.net"
        };
        _supportLinkLabel.Links.Clear();
        _supportLinkLabel.Links.Add(0, _supportLinkLabel.Text.Length, "mailto:support@libreguard.net?subject=LibreGuard%20VPN%20Uninstall%20Feedback");
        _supportLinkLabel.LinkClicked += SupportLinkLabelOnLinkClicked;

        _uninstallButton = new Button
        {
            Text = "Uninstall",
            DialogResult = DialogResult.None,
            Location = new Point(360, 268),
            Size = new Size(80, 30)
        };
        _uninstallButton.Click += UninstallButtonOnClick;

        _cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(446, 268),
            Size = new Size(80, 30)
        };

        AcceptButton = _uninstallButton;
        CancelButton = _cancelButton;

        Controls.Add(titleLabel);
        Controls.Add(descriptionLabel);
        Controls.Add(_removeDataCheckBox);
        Controls.Add(feedbackLabel);
        Controls.Add(_supportLinkLabel);
        Controls.Add(_uninstallButton);
        Controls.Add(_cancelButton);
    }

    public int? ExitCode { get; private set; }

    private async void UninstallButtonOnClick(object? sender, EventArgs e)
    {
        _uninstallButton.Enabled = false;
        _cancelButton.Enabled = false;
        UseWaitCursor = true;

        try
        {
            ExitCode = await _uninstallAsync(_removeDataCheckBox.Checked);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "LibreGuard VPN Uninstaller", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _uninstallButton.Enabled = true;
            _cancelButton.Enabled = true;
            UseWaitCursor = false;
        }
    }

    private static void SupportLinkLabelOnLinkClicked(object? sender, LinkLabelLinkClickedEventArgs e)
    {
        var mailto = e.Link?.LinkData as string;
        if (string.IsNullOrWhiteSpace(mailto))
            return;

        Process.Start(new ProcessStartInfo(mailto) { UseShellExecute = true });
    }
}