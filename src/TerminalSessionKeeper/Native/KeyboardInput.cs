using System.Runtime.InteropServices;

namespace TerminalSessionKeeper.Native;

/// <summary>
/// Presses a key chord at the window that has the focus.
///
/// Separate from <see cref="ConsoleInjector"/>, and for a different job: that one writes into a
/// tab's console input queue, where the shell reads it, and never involves the terminal's own
/// keyboard handling. A Windows Terminal action — the only way to set a tab colour the user can
/// clear again — is handled by the terminal itself before any of that, so it has to arrive as a
/// real keystroke.
/// </summary>
public static class KeyboardInput
{
    private const int InputKeyboard = 1;
    private const uint KeyUp = 0x0002;

    private const ushort Control = 0x11;
    private const ushort Shift = 0x10;
    private const ushort Menu = 0x12;

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardEvent
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public KeyboardEvent Keyboard;

        // The union is as wide as its largest member, MOUSEINPUT; without the padding the
        // struct is too small and SendInput rejects every record.
        public int Padding1;
        public int Padding2;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    public static IntPtr Foreground() => GetForegroundWindow();

    public static bool Focus(IntPtr window) => SetForegroundWindow(window);

    public static int ProcessIdOf(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var processId);
        return (int)processId;
    }

    /// <summary>Ctrl+Alt+Shift+<paramref name="virtualKey"/>, pressed and released in order.</summary>
    public static bool SendCtrlAltShift(ushort virtualKey)
    {
        var sequence = new[]
        {
            Down(Control), Down(Menu), Down(Shift),
            Down(virtualKey), Up(virtualKey),
            Up(Shift), Up(Menu), Up(Control),
        };

        var sent = SendInput((uint)sequence.Length, sequence, Marshal.SizeOf<Input>());
        return sent == sequence.Length;
    }

    private static Input Down(ushort virtualKey) => new()
    {
        Type = InputKeyboard,
        Keyboard = new KeyboardEvent { VirtualKey = virtualKey },
    };

    private static Input Up(ushort virtualKey) => new()
    {
        Type = InputKeyboard,
        Keyboard = new KeyboardEvent { VirtualKey = virtualKey, Flags = KeyUp },
    };
}
