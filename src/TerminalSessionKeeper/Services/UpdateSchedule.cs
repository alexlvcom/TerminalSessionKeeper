namespace TerminalSessionKeeper.Services;

public enum UpdateCheckFrequency
{
    Daily = 0,
    Hourly = 1,
    Weekly = 2,
    Monthly = 3,
    Never = 4,
}

/// <summary>What a completed update check found. Stored in settings, so values are fixed.</summary>
public enum UpdateCheckOutcome
{
    Current = 0,
    Available = 1,
    Failed = 2,
}

public static class UpdateSchedule
{
    public static UpdateCheckFrequency Normalize(UpdateCheckFrequency frequency) =>
        Enum.IsDefined(frequency) ? frequency : UpdateCheckFrequency.Daily;

    public static DateTime NextCheck(DateTime nowUtc, UpdateCheckFrequency frequency) => Normalize(frequency) switch
    {
        UpdateCheckFrequency.Hourly => nowUtc.AddHours(1),
        UpdateCheckFrequency.Weekly => nowUtc.AddDays(7),
        UpdateCheckFrequency.Monthly => nowUtc.AddMonths(1),
        UpdateCheckFrequency.Never => DateTime.MaxValue,
        _ => nowUtc.AddDays(1),
    };
}
