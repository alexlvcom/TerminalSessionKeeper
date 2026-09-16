using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TerminalSessionKeeper.Native;

/// <summary>One process, as far as tab discovery cares.</summary>
public sealed record ProcessEntry(int ProcessId, int ParentProcessId, string Name)
{
    /// <summary>Creation time, resolved lazily — it needs a handle per process, and only the
    /// handful of processes under a terminal window are ever ordered.</summary>
    public DateTime? StartTimeUtc { get; init; }
}

/// <summary>
/// A parent/child view of the running processes, built from a Toolhelp snapshot. Toolhelp is
/// used rather than WMI because it needs no extra package, costs milliseconds, and tab
/// discovery only ever wants the pid, the parent pid and the image name.
/// </summary>
public sealed class ProcessTree
{
    private const uint Th32CsSnapProcess = 0x00000002;
    private const int MaxPath = 260;
    private const int ProcessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPath)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr handle, out long creation, out long exit,
        out long kernel, out long user);

    private readonly Dictionary<int, ProcessEntry> _byId;
    private readonly Dictionary<int, List<ProcessEntry>> _byParent;

    private ProcessTree(Dictionary<int, ProcessEntry> byId, Dictionary<int, List<ProcessEntry>> byParent)
    {
        _byId = byId;
        _byParent = byParent;
    }

    public IReadOnlyCollection<ProcessEntry> All => _byId.Values;

    public static ProcessTree Capture()
    {
        var byId = new Dictionary<int, ProcessEntry>();
        var byParent = new Dictionary<int, List<ProcessEntry>>();

        var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate processes.");
        }

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>(), ExeFile = string.Empty };
            if (!Process32FirstW(snapshot, ref entry)) return new ProcessTree(byId, byParent);

            do
            {
                var record = new ProcessEntry((int)entry.ProcessId, (int)entry.ParentProcessId,
                    entry.ExeFile ?? string.Empty);

                // A pid can be reused between the snapshot's own passes; last one wins, which
                // matches what any later OpenProcess on that pid would find.
                byId[record.ProcessId] = record;

                if (!byParent.TryGetValue(record.ParentProcessId, out var siblings))
                {
                    siblings = new List<ProcessEntry>();
                    byParent[record.ParentProcessId] = siblings;
                }

                siblings.Add(record);
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return new ProcessTree(byId, byParent);
    }

    public ProcessEntry? Find(int processId) =>
        _byId.TryGetValue(processId, out var entry) ? entry : null;

    public IReadOnlyList<ProcessEntry> ChildrenOf(int processId) =>
        _byParent.TryGetValue(processId, out var children) ? children : Array.Empty<ProcessEntry>();

    /// <summary>Direct children, oldest first — the order the tabs were opened in.</summary>
    public IReadOnlyList<ProcessEntry> ChildrenOfByAge(int processId) =>
        ChildrenOf(processId)
            .Select(child => child with { StartTimeUtc = StartTimeUtc(child.ProcessId) })
            .OrderBy(child => child.StartTimeUtc ?? DateTime.MaxValue)
            .ToList();

    /// <summary>Breadth-first walk of everything below <paramref name="processId"/>.</summary>
    public IEnumerable<ProcessEntry> Descendants(int processId)
    {
        var queue = new Queue<int>();
        var seen = new HashSet<int> { processId };
        queue.Enqueue(processId);

        while (queue.Count > 0)
        {
            foreach (var child in ChildrenOf(queue.Dequeue()))
            {
                // A recycled pid could otherwise make the tree cyclic and never terminate.
                if (!seen.Add(child.ProcessId)) continue;
                yield return child;
                queue.Enqueue(child.ProcessId);
            }
        }
    }

    public static DateTime? StartTimeUtc(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;

        try
        {
            return GetProcessTimes(handle, out var creation, out _, out _, out _)
                ? DateTime.FromFileTimeUtc(creation)
                : null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
