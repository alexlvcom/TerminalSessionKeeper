// AlexLV Windows UI kit. Canonical copy: C:\projects\AlexLV.WindowsUi (see README.md).
using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AlexLV.WindowsUi;

/// <summary>
/// Base class for every app window. Windows shows a new window with a light frame and a white
/// client area before WPF draws its first frame, so a dark window flashes light. This window
/// themes its title bar as soon as its handle exists and stays cloaked (invisible but shown)
/// until its themed content has rendered.
/// </summary>
public class ThemedWindow : Window
{
    private bool _isCloaked;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        ThemeManager.ApplyTitleBar(handle);
        _isCloaked = ThemeManager.Cloak(handle, true);
        // ContentRendered can be skipped (a window shown minimized); never leave one invisible.
        if (_isCloaked) Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, Uncloak);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        Uncloak();
    }

    private void Uncloak()
    {
        if (!_isCloaked) return;
        _isCloaked = false;
        ThemeManager.Cloak(new WindowInteropHelper(this).Handle, false);
    }
}
