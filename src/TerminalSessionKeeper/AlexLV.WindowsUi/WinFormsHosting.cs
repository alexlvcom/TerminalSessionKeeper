// AlexLV Windows UI kit. Canonical copy: C:\projects\AlexLV.WindowsUi (see README.md).
using System;
using System.Drawing;
using System.Windows;
using System.Windows.Forms.Integration;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AlexLV.WindowsUi;

/// <summary>
/// Glue for apps whose message loop is WinForms (tray apps with a NotifyIcon) and whose windows
/// are WPF.
/// </summary>
public static class WinFormsHosting
{
    /// <summary>
    /// Shows a modeless WPF window from a WinForms loop. Without the interop call the window gets
    /// no keyboard input for Tab, arrows, Enter, Esc or text boxes.
    /// </summary>
    public static void ShowModeless(Window window)
    {
        ElementHost.EnableModelessKeyboardInterop(window);
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    /// <summary>Converts the app's WinForms icon for use as a WPF window icon.</summary>
    public static ImageSource ToImageSource(Icon icon)
    {
        var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }
}
