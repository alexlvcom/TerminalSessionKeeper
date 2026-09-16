using System.Drawing;

namespace TerminalSessionKeeper.Ui;

/// <summary>
/// Loads the application icon from the executable itself, so the tray icon and the window icon
/// always match the icon compiled into the binary. Falls back to a drawn placeholder if the
/// embedded icon cannot be extracted.
/// </summary>
public static class AppIcon
{
    public static Icon Load()
    {
        try
        {
            // Environment.ProcessPath is the only reliable source here: Assembly.Location is
            // empty in a single-file publish.
            var path = Environment.ProcessPath;

            if (!string.IsNullOrEmpty(path))
            {
                var extracted = Icon.ExtractAssociatedIcon(path);
                if (extracted is not null) return extracted;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Fall through to the drawn placeholder.
        }

        return CreatePlaceholder();
    }

    private static Icon CreatePlaceholder()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var background = new SolidBrush(Color.FromArgb(24, 30, 44));
            graphics.FillRectangle(background, 1, 3, 30, 26);

            using var pen = new Pen(Color.FromArgb(120, 224, 160), 3f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
            };

            // A ">" prompt.
            graphics.DrawLines(pen, new[] { new Point(8, 11), new Point(14, 16), new Point(8, 21) });
            graphics.DrawLine(pen, 17, 21, 24, 21);
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }
}
