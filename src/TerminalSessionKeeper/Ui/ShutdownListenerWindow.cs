using System.Drawing;
using System.Windows.Forms;

namespace TerminalSessionKeeper.Ui;

internal sealed class ShutdownListenerWindow : Form
{
    private readonly Action _onShutdown;

    public ShutdownListenerWindow(Action onShutdown)
    {
        _onShutdown = onShutdown;
        Text = "TerminalSessionKeeperListener";
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        FormBorderStyle = FormBorderStyle.None;
        Size = Size.Empty;
        Opacity = 0;
    }

    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);

    protected override void WndProc(ref Message m)
    {
        const int WM_CLOSE = 0x0010;
        if (m.Msg == WM_CLOSE)
        {
            _onShutdown();
            return;
        }

        base.WndProc(ref m);
    }
}
