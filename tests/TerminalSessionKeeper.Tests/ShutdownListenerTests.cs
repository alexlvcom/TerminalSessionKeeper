using System.Runtime.InteropServices;
using TerminalSessionKeeper.Ui;

namespace TerminalSessionKeeper.Tests;

public sealed class ShutdownListenerTests
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);

    [Fact]
    public void SetupFindsTheListenerByTitleAndItsCloseRequestShutsTheAppDown()
    {
        // Setup and uninstall look the window up by title and post WM_CLOSE (installer/TerminalSessionKeeper.iss).
        var shutdownRequested = false;
        using var listener = new ShutdownListenerWindow(() => shutdownRequested = true);

        var handle = FindWindow(null, "TerminalSessionKeeperListener");
        Assert.NotEqual(IntPtr.Zero, handle);

        SendMessage(handle, 0x0010, IntPtr.Zero, IntPtr.Zero);
        Assert.True(shutdownRequested);
    }
}
