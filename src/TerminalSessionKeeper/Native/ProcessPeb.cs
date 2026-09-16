using System.Runtime.InteropServices;
using System.Text;

namespace TerminalSessionKeeper.Native;

/// <summary>
/// Reads a running process's working directory, command line and environment straight out of
/// its PEB. There is no supported API for any of the three, and the environment in particular
/// is the whole point: <c>WT_SESSION</c> lives there and nowhere else.
///
/// All offsets are x64 and verified on this machine.
/// </summary>
public static class ProcessPeb
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2;
        public IntPtr Reserved3;
        public IntPtr UniqueProcessId;
        public IntPtr Reserved4;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr handle, int infoClass,
        ref ProcessBasicInformation info, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr handle, IntPtr address,
        byte[] buffer, int size, out IntPtr bytesRead);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>PROCESS_QUERY_INFORMATION | PROCESS_VM_READ.</summary>
    private const int Access = 0x0410;

    private const int PebProcessParameters = 0x20;
    private const int ParamsCurrentDirectory = 0x38;   // UNICODE_STRING
    private const int ParamsCommandLine = 0x70;        // UNICODE_STRING
    private const int ParamsEnvironment = 0x80;        // PVOID
    private const int ParamsEnvironmentSize = 0x3F0;   // ULONG_PTR

    /// <summary>A UNICODE_STRING that big is corruption, not a path; refuse to allocate for it.</summary>
    private const int MaxUnicodeStringBytes = 32768;

    private const long MaxEnvironmentBytes = 1024 * 1024;
    private const long FallbackEnvironmentBytes = 64 * 1024;

    /// <summary>
    /// Working directory of a process, with the trailing separator kept on a drive root:
    /// "C:" without it is a relative path, not the root of C.
    /// </summary>
    public static string? CurrentDirectory(int processId)
    {
        var handle = OpenProcess(Access, false, processId);
        if (handle == IntPtr.Zero) return null;

        try
        {
            if (!TryReadProcessParameters(handle, out var parameters)) return null;

            var path = ReadUnicodeString(handle, parameters + ParamsCurrentDirectory);
            if (string.IsNullOrEmpty(path)) return null;

            return path.Length > 3 ? path.TrimEnd('\\') : path;
        }
        catch (Exception ex) when (ex is OutOfMemoryException or OverflowException)
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>Full command line of a process, including the executable.</summary>
    public static string? CommandLine(int processId)
    {
        var handle = OpenProcess(Access, false, processId);
        if (handle == IntPtr.Zero) return null;

        try
        {
            if (!TryReadProcessParameters(handle, out var parameters)) return null;
            var line = ReadUnicodeString(handle, parameters + ParamsCommandLine);
            return string.IsNullOrWhiteSpace(line) ? null : line;
        }
        catch (Exception ex) when (ex is OutOfMemoryException or OverflowException)
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// The process's environment block. Empty when the process cannot be opened, which is the
    /// normal outcome for anything running as another user.
    /// </summary>
    public static Dictionary<string, string> Environment(int processId)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var handle = OpenProcess(Access, false, processId);
        if (handle == IntPtr.Zero) return result;

        try
        {
            if (!TryReadProcessParameters(handle, out var parameters)) return result;

            var pointer = new byte[8];
            if (!Read(handle, parameters + ParamsEnvironment, pointer, out _)) return result;
            var environment = BitConverter.ToInt64(pointer, 0);
            if (environment == 0) return result;

            long size = 0;
            var sizeBytes = new byte[8];
            if (Read(handle, parameters + ParamsEnvironmentSize, sizeBytes, out _))
            {
                size = BitConverter.ToInt64(sizeBytes, 0);
            }

            if (size <= 0 || size > MaxEnvironmentBytes) size = FallbackEnvironmentBytes;

            var block = new byte[size];
            if (!Read(handle, environment, block, out var read) || read <= 0) return result;

            // NUL-separated "KEY=VALUE" entries terminated by an empty entry. Stopping at
            // that empty entry is not optional: past it is stale memory, and reading on
            // yields duplicate — and wrong — values.
            foreach (var entry in Encoding.Unicode.GetString(block, 0, read).Split('\0'))
            {
                if (entry.Length == 0) break;

                // From index 1: a leading '=' belongs to the "=C:" per-drive cwd entries.
                var split = entry.IndexOf('=', 1);
                if (split > 0) result[entry[..split]] = entry[(split + 1)..];
            }

            return result;
        }
        catch (Exception ex) when (ex is OutOfMemoryException or OverflowException)
        {
            return result;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool Read(IntPtr handle, long address, byte[] buffer, out int read)
    {
        var ok = ReadProcessMemory(handle, (IntPtr)address, buffer, buffer.Length, out var got);
        read = (int)got;
        return ok;
    }

    private static bool TryReadProcessParameters(IntPtr handle, out long parameters)
    {
        parameters = 0;

        var info = new ProcessBasicInformation();
        if (NtQueryInformationProcess(handle, 0, ref info,
                Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0)
        {
            return false;
        }

        var pointer = new byte[8];
        if (!Read(handle, info.PebBaseAddress.ToInt64() + PebProcessParameters, pointer, out _)) return false;

        parameters = BitConverter.ToInt64(pointer, 0);
        return parameters != 0;
    }

    /// <summary>UNICODE_STRING: Length at +0 (bytes), Buffer at +8.</summary>
    private static string? ReadUnicodeString(IntPtr handle, long address)
    {
        var header = new byte[16];
        if (!Read(handle, address, header, out _)) return null;

        int length = BitConverter.ToUInt16(header, 0);
        var buffer = BitConverter.ToInt64(header, 8);
        if (length <= 0 || length > MaxUnicodeStringBytes || buffer == 0) return null;

        var text = new byte[length];
        if (!Read(handle, buffer, text, out var read) || read <= 0) return null;

        return Encoding.Unicode.GetString(text, 0, read);
    }
}
