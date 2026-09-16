using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using TerminalSessionKeeper.Logging;

namespace TerminalSessionKeeper.Native;

/// <summary>
/// Types text into another process's console, so a rebuilt tab comes back with its resume
/// command sitting at the prompt exactly as if the user had typed it.
///
/// A console's input queue is read by whatever asks for input next, so writing KEY_EVENT
/// records into it reaches the prompt without any cooperation from the shell. This works for
/// PowerShell — PSReadLine cannot be pre-filled through its own API from outside its prompt —
/// and, verified on this machine, for a Windows Terminal tab's <c>wsl.exe</c>/<c>ubuntu.exe</c>
/// as well: the records cross the ConPTY into the Linux pty and land on the zsh command line.
/// That is what lets the app own the whole job with no shell-side hook to install.
///
/// The attach happens in a short-lived copy of this executable rather than in the tray process,
/// for two reasons. A process can hold only one console at a time, so concurrent injections
/// would fight over it. And attaching makes the caller a member of that console's process
/// group: closing the tab would deliver CTRL_CLOSE_EVENT to whoever is attached, which must
/// never be the tray app.
/// </summary>
public static class ConsoleInjector
{
    public const string HelperArgument = "--inject-console";

    private const string TypeMode = "type";
    private const string TitleMode = "title";

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetConsoleTitleW")]
    private static extern bool SetConsoleTitle(string title);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteConsoleInput(IntPtr handle, InputRecord[] buffer, uint length,
        out uint written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyEventRecord
    {
        public int KeyDown;
        public ushort RepeatCount;
        public ushort VirtualKeyCode;
        public ushort VirtualScanCode;
        public char UnicodeChar;
        public uint ControlKeyState;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputRecord
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KeyEventRecord KeyEvent;
    }

    private const ushort KeyEvent = 1;
    private const ushort VkReturn = 0x0D;
    private const uint GenericReadWrite = 0xC0000000;
    private const uint ShareReadWrite = 3;
    private const uint OpenExisting = 3;

    private static readonly IntPtr InvalidHandle = new(-1);

    /// <summary>The helper's exit codes, so a failure says which step failed.</summary>
    public enum InjectResult
    {
        Ok = 0,
        BadArguments = 1,
        AttachFailed = 2,
        ConsoleUnavailable = 3,
        WriteFailed = 4,
        HelperUnavailable = 5,
        Timeout = 6,
    }

    /// <summary>
    /// Types <paramref name="text"/> into the console owned by <paramref name="processId"/>.
    /// A carriage return anywhere in the text submits the line before it, so the caller
    /// decides what runs and what only waits at the prompt.
    /// </summary>
    public static InjectResult Send(int processId, string text, ILog log) =>
        Run(TypeMode, processId, text, log);

    /// <summary>
    /// Renames the tab, through the console rather than through the shell.
    ///
    /// Going through the shell does not survive: a title command has to be submitted, and the
    /// prompt that follows it is exactly when a themed shell renames the tab after its folder
    /// again. Setting the console's own title reaches the terminal without a command and
    /// without a new prompt, so it is still there when the user arrives — and the agent stays
    /// free to rename the tab itself the moment its session resumes.
    /// </summary>
    public static InjectResult SetTitle(int processId, string title, ILog log) =>
        Run(TitleMode, processId, title, log);

    private static InjectResult Run(string mode, int processId, string text, ILog log)
    {
        if (processId <= 0 || string.IsNullOrEmpty(text)) return InjectResult.BadArguments;

        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            log.Warn("Cannot type into a tab: the executable path is unknown.");
            return InjectResult.HelperUnavailable;
        }

        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // ArgumentList quotes each element correctly; a composed command-line string does not,
        // which is how "-p Windows PowerShell" silently became "-p Windows" in the original.
        startInfo.ArgumentList.Add(HelperArgument);
        startInfo.ArgumentList.Add(mode);
        startInfo.ArgumentList.Add(processId.ToString());
        startInfo.ArgumentList.Add(payload);

        try
        {
            using var helper = Process.Start(startInfo);
            if (helper is null) return InjectResult.HelperUnavailable;

            if (!helper.WaitForExit(10_000))
            {
                log.Warn($"Typing into pid {processId} timed out; leaving the helper to exit on its own.");
                return InjectResult.Timeout;
            }

            var result = (InjectResult)helper.ExitCode;
            if (result != InjectResult.Ok) log.Warn($"Console {mode} on pid {processId} failed: {result}.");
            return result;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                      or ObjectDisposedException or SystemException)
        {
            log.Error($"Could not start the console helper for pid {processId}.", ex);
            return InjectResult.HelperUnavailable;
        }
    }

    /// <summary>
    /// The helper side. Runs in its own process, attaches to the target's console, writes the
    /// keystrokes, and exits. Nothing is printed: stdout would land in the user's tab.
    /// </summary>
    public static int RunHelper(string[] arguments)
    {
        if (arguments.Length < 4
            || !int.TryParse(arguments[2], out var processId)
            || processId <= 0)
        {
            return (int)InjectResult.BadArguments;
        }

        var mode = arguments[1];
        if (mode is not (TypeMode or TitleMode)) return (int)InjectResult.BadArguments;

        string text;
        try
        {
            text = Encoding.UTF8.GetString(Convert.FromBase64String(arguments[3]));
        }
        catch (FormatException)
        {
            return (int)InjectResult.BadArguments;
        }

        if (text.Length == 0) return (int)InjectResult.BadArguments;

        // This process is a WinExe and normally has no console, but FreeConsole is harmless
        // when there is none and mandatory when there is: AttachConsole fails outright while
        // another console is held.
        FreeConsole();

        // The tab may still be starting when the first attempt lands, so a few short retries
        // are the difference between a restored tab that resumes and one that does not.
        if (!TryAttach(processId)) return (int)InjectResult.AttachFailed;

        try
        {
            return mode == TitleMode ? ApplyTitle(text) : TypeText(text);
        }
        finally
        {
            FreeConsole();
        }
    }

    /// <summary>
    /// ConPTY turns a console title change into the escape sequence the terminal reads as
    /// "rename this tab", so this reaches Windows Terminal for a WSL pane just as it does for a
    /// PowerShell one — the Linux side never sees it and never has to cooperate.
    /// </summary>
    private static int ApplyTitle(string title) =>
        SetConsoleTitle(title) ? (int)InjectResult.Ok : (int)InjectResult.WriteFailed;

    private static int TypeText(string text)
    {
        var console = CreateFile("CONIN$", GenericReadWrite, ShareReadWrite, IntPtr.Zero,
            OpenExisting, 0, IntPtr.Zero);

        if (console == IntPtr.Zero || console == InvalidHandle) return (int)InjectResult.ConsoleUnavailable;

        try
        {
            var records = BuildRecords(text);
            var ok = WriteConsoleInput(console, records, (uint)records.Length, out var written);
            return ok && written == records.Length
                ? (int)InjectResult.Ok
                : (int)InjectResult.WriteFailed;
        }
        finally
        {
            CloseHandle(console);
        }
    }

    private static bool TryAttach(int processId)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (AttachConsole(processId)) return true;
            Thread.Sleep(150);
        }

        return false;
    }

    /// <summary>
    /// One key-down and one key-up per character, which is what a real keystroke looks like.
    /// A bare '\r' carries VK_RETURN so the line editor reads it as Enter rather than as a
    /// stray control character.
    /// </summary>
    private static InputRecord[] BuildRecords(string text)
    {
        var records = new InputRecord[text.Length * 2];

        for (var i = 0; i < text.Length; i++)
        {
            for (var updown = 0; updown < 2; updown++)
            {
                var slot = i * 2 + updown;
                records[slot].EventType = KeyEvent;
                records[slot].KeyEvent.KeyDown = updown == 0 ? 1 : 0;
                records[slot].KeyEvent.RepeatCount = 1;
                records[slot].KeyEvent.UnicodeChar = text[i];
                if (text[i] == '\r') records[slot].KeyEvent.VirtualKeyCode = VkReturn;
            }
        }

        return records;
    }
}
