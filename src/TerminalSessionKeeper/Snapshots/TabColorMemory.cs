using System.Text;
using System.Text.Json;
using TerminalSessionKeeper.Logging;
using TerminalSessionKeeper.Services;

namespace TerminalSessionKeeper.Snapshots;

/// <summary>What the sampler has been saying about one tab, across snapshots.</summary>
public sealed class TabColorObservation
{
    /// <summary>The colour the snapshot is allowed to record. Null means "no colour".</summary>
    public string? Confirmed { get; set; }

    /// <summary>The reading waiting for a second snapshot to agree with it.</summary>
    public string? Pending { get; set; }

    /// <summary>How many snapshots in a row have read <see cref="Pending"/>.</summary>
    public int PendingCount { get; set; }

    public DateTimeOffset LastSeenUtc { get; set; }
}

/// <summary>
/// Decides which sampled colours are real, by making each one prove itself twice.
///
/// A colour read off the screen goes into the snapshot, and the snapshot is what a restore paints
/// back onto the tab — so the sampler reads its own output on the next pass. That loop has no
/// damping in it: un-blending divides by 0.30, so a reading one unit off comes back three units
/// off, and a tab walked from #DD153D to #FF0051 over an evening while another sank to black. A
/// single bad frame is worse still, because <see cref="SnapshotService"/> carries a colour forward
/// when a later snapshot cannot see the tab — so one wrong reading never expires.
///
/// Two rules together close it. A reading close to the colour already confirmed leaves that colour
/// exactly as it is, which removes the drift: only a real change moves a tab's colour. And a
/// reading that is a change has to arrive twice in a row before it is believed, which removes the
/// one-off — including the one-off that reads "no colour", so a colour the user actually cleared
/// still disappears, just one snapshot later.
///
/// Tabs are keyed by WT_SESSION, the id Windows Terminal puts in every pane's environment, so a
/// tab keeps its history when it is dragged, renamed or re-titled by its agent.
/// </summary>
public sealed class TabColorMemory
{
    /// <summary>How many snapshots in a row must agree before a change is recorded.</summary>
    public const int RequiredAgreement = 2;

    /// <summary>
    /// Channel distance within which a new reading is treated as the confirmed colour unchanged.
    /// Wider than the sampler's own tolerance: this is where re-reading a restored colour is
    /// absorbed, and an un-blended reading drifts by about three units per unit of rounding.
    /// </summary>
    public const int SameColorTolerance = 10;

    /// <summary>Forgotten after this long unseen; a closed tab's id never comes back.</summary>
    private static readonly TimeSpan Expiry = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly ILog _log;
    private readonly Dictionary<string, TabColorObservation> _entries;

    public TabColorMemory(ILog log, string? path = null)
    {
        _log = log;
        _path = string.IsNullOrWhiteSpace(path) ? AppPaths.TabColorMemoryFile : path;
        _entries = Load();
    }

    /// <summary>
    /// Files one snapshot's reading for a tab and returns the colour that may be recorded for it.
    /// <paramref name="reading"/> is the sampler's verdict: a hex colour, or null for "this tab
    /// has no colour". A window the sampler could not read must not be passed here at all —
    /// silence is not the same as no colour.
    /// </summary>
    public string? Observe(string wtSession, string? reading)
    {
        if (string.IsNullOrEmpty(wtSession)) return reading;

        if (!_entries.TryGetValue(wtSession, out var entry))
        {
            entry = new TabColorObservation();
            _entries[wtSession] = entry;
        }

        entry.LastSeenUtc = DateTimeOffset.UtcNow;

        // Close enough to what is already confirmed is that colour, not a new one. This is the
        // rule that stops a restored colour drifting a little further every time it is re-read.
        if (IsSame(entry.Confirmed, reading))
        {
            entry.Pending = null;
            entry.PendingCount = 0;
            return entry.Confirmed;
        }

        if (IsSame(entry.Pending, reading))
        {
            entry.PendingCount++;
        }
        else
        {
            entry.Pending = reading;
            entry.PendingCount = 1;
        }

        if (entry.PendingCount < RequiredAgreement) return entry.Confirmed;

        _log.Debug($"Tab colour for session {wtSession} settled on " +
                   $"{entry.Confirmed ?? "none"} → {reading ?? "none"} after " +
                   $"{entry.PendingCount} agreeing snapshots.");

        entry.Confirmed = reading;
        entry.Pending = null;
        entry.PendingCount = 0;

        return entry.Confirmed;
    }

    /// <summary>The colour last confirmed for a tab, without filing a new reading.</summary>
    public string? Confirmed(string wtSession) =>
        !string.IsNullOrEmpty(wtSession) && _entries.TryGetValue(wtSession, out var entry)
            ? entry.Confirmed
            : null;

    /// <summary>
    /// Records a colour as already proven — used when the user pins one in overrides.json, so a
    /// sampled reading of the colour the app itself just applied is not treated as a change.
    /// </summary>
    public void Confirm(string wtSession, string? color)
    {
        if (string.IsNullOrEmpty(wtSession)) return;

        _entries[wtSession] = new TabColorObservation
        {
            Confirmed = color,
            LastSeenUtc = DateTimeOffset.UtcNow,
        };
    }

    public void Save()
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow - Expiry;
            foreach (var key in _entries.Where(entry => entry.Value.LastSeenUtc < cutoff)
                         .Select(entry => entry.Key).ToList())
            {
                _entries.Remove(key);
            }

            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllText(_path, JsonSerializer.Serialize(_entries, Options), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            // Losing it costs one snapshot cycle of confirmation, never a snapshot.
            _log.Warn($"Could not save the tab colour history. {ex.Message}");
        }
    }

    private Dictionary<string, TabColorObservation> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new Dictionary<string, TabColorObservation>(StringComparer.Ordinal);

            var json = File.ReadAllText(_path, new UTF8Encoding(false));
            if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, TabColorObservation>(StringComparer.Ordinal);

            return JsonSerializer.Deserialize<Dictionary<string, TabColorObservation>>(json, Options)
                   ?? new Dictionary<string, TabColorObservation>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log.Warn($"Ignoring an unreadable tab colour history. {ex.Message}");
            return new Dictionary<string, TabColorObservation>(StringComparer.Ordinal);
        }
    }

    /// <summary>Two readings that mean the same thing: both absent, or near enough in every channel.</summary>
    public static bool IsSame(string? left, string? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return true;

        var first = Parse(left);
        var second = Parse(right);
        if (first is null || second is null) return false;

        return Math.Abs(first.Value.R - second.Value.R) <= SameColorTolerance
               && Math.Abs(first.Value.G - second.Value.G) <= SameColorTolerance
               && Math.Abs(first.Value.B - second.Value.B) <= SameColorTolerance;
    }

    private static (int R, int G, int B)? Parse(string color)
    {
        var text = color.TrimStart('#');
        if (text.Length != 6) return null;

        return int.TryParse(text[..2], System.Globalization.NumberStyles.HexNumber, null, out var r)
               && int.TryParse(text[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g)
               && int.TryParse(text[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b)
            ? (r, g, b)
            : null;
    }
}
