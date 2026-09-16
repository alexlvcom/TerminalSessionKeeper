using System.Drawing;

namespace TerminalSessionKeeper.Ui;

/// <summary>
/// About window: version, build stamp, what the app does, what it touches, and the change
/// history.
/// </summary>
public sealed class AboutForm : Form
{
    public AboutForm(Icon icon)
    {
        Text = $"About {AppInfo.DisplayName} — v{Changelog.MarketingVersion}";
        Icon = icon;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(560, 400);
        Size = new Size(760, 580);
        ShowInTaskbar = true;

        var header = BuildHeader(icon);

        var body = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, 9f),
            BackColor = SystemColors.Window,
            TabStop = false,
            // The header already carries the version and build stamp.
            Text = Changelog.FormatBody(),
        };

        body.SelectionStart = 0;
        body.SelectionLength = 0;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(8),
        };

        var closeButton = new Button { Text = "Close", AutoSize = true };
        closeButton.Click += (_, _) => Close();

        // Handy when reporting a problem: version header included, unlike the body above.
        var copyButton = new Button { Text = "Copy details", AutoSize = true };
        copyButton.Click += (_, _) => CopyDetails(Changelog.FormatFull());

        buttons.Controls.AddRange(new Control[] { closeButton, copyButton });

        // Added last-to-first: Fill must be added after the docked edges to lay out correctly.
        Controls.Add(buttons);
        Controls.Add(body);
        Controls.Add(header);

        AcceptButton = closeButton;
        CancelButton = closeButton;
    }

    private static Panel BuildHeader(Icon icon)
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 84, Padding = new Padding(12, 10, 12, 6) };

        var logo = new PictureBox
        {
            Image = icon.ToBitmap(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(40, 40),
            Location = new Point(12, 12),
        };

        var title = new Label
        {
            Text = $"{AppInfo.DisplayName}  v{Changelog.MarketingVersion}",
            Font = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif,
                12f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(64, 10),
        };

        var subtitle = new Label
        {
            Text = Changelog.BuildDate == DateTime.MinValue
                ? $"Build {Changelog.BuildVersion}"
                : $"Build {Changelog.BuildVersion}   ·   built {Changelog.BuildDate:yyyy-MM-dd HH:mm}",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Location = new Point(66, 34),
        };

        var author = new Label
        {
            Text = Changelog.AuthorLine,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Location = new Point(66, 54),
        };

        header.Controls.AddRange(new Control[] { logo, title, subtitle, author });
        return header;
    }

    private void CopyDetails(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or ThreadStateException)
        {
            // Another process can hold the clipboard open; not worth interrupting the user for.
            MessageBox.Show(this, "Could not copy to the clipboard. Try again in a moment.",
                AppInfo.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
