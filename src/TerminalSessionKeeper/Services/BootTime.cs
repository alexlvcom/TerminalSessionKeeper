using System.Runtime.InteropServices;

namespace TerminalSessionKeeper.Services;

/// <summary>
/// When this Windows session started.
///
/// Auto-restore turns on this one fact: it is the difference between "the session these tabs
/// belonged to is gone" and "the user is still sitting in it". Restoring into a live session
/// leaves two windows open, and the next snapshot captures both — the one after that would
/// restore twenty tabs instead of ten.
/// </summary>
public static class BootTime
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTimeOfDayInformation
    {
        public long BootTime;
        public long CurrentTime;
        public long TimeZoneBias;
        public uint TimeZoneId;
        public uint Reserved;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, ref SystemTimeOfDayInformation info,
        int length, out int returnLength);

    private const int SystemTimeOfDay = 3;

    /// <summary>
    /// Two boots cannot land within this of each other, and no clock correction inside one
    /// session moves the answer this far, so it separates the two cases cleanly.
    /// </summary>
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    public static DateTimeOffset Current()
    {
        var info = new SystemTimeOfDayInformation();

        if (NtQuerySystemInformation(SystemTimeOfDay, ref info, Marshal.SizeOf<SystemTimeOfDayInformation>(), out _) == 0
            && info.BootTime > 0)
        {
            try
            {
                return new DateTimeOffset(DateTime.FromFileTimeUtc(info.BootTime));
            }
            catch (ArgumentOutOfRangeException)
            {
                // Fall through to the tick-count estimate.
            }
        }

        // Good to a few seconds, which is well inside the tolerance below.
        return new DateTimeOffset(DateTime.UtcNow.AddMilliseconds(-Environment.TickCount64));
    }

    /// <summary>True when <paramref name="recorded"/> is from an earlier boot than the one
    /// running now. An unknown recorded time counts as the same session: never guess in the
    /// direction that rebuilds tabs the user can already see.</summary>
    public static bool IsFromEarlierSession(DateTimeOffset? recorded) =>
        recorded.HasValue && (Current() - recorded.Value).Duration() > Tolerance;

    public static bool IsSameSession(DateTimeOffset? recorded) =>
        recorded.HasValue && (Current() - recorded.Value).Duration() <= Tolerance;
}
