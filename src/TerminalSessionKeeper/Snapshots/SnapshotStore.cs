using System.Text;
using System.Text.Json;
using TerminalSessionKeeper.Logging;
using TerminalSessionKeeper.Model;
using TerminalSessionKeeper.Services;

namespace TerminalSessionKeeper.Snapshots;

public sealed record SavedSnapshot(string Path, DateTime SavedAtUtc, string Name);

/// <summary>
/// Snapshots on disk. They are a few KB each, plain JSON, and meant to be opened and edited —
/// fixing a mis-matched title is a one-line change, which is the whole reason the format is not
/// something more compact.
/// </summary>
public sealed class SnapshotStore
{
    private const string FilePrefix = "snapshot-";
    private const string FileSuffix = ".json";
    private const string LatestFileName = "latest.json";

    private readonly string _directory;
    private readonly ILog _log;

    public SnapshotStore(ILog log, string? directory = null)
    {
        _log = log;
        _directory = string.IsNullOrWhiteSpace(directory) ? AppPaths.SnapshotDirectory : directory;
    }

    public string Directory => _directory;

    public string LatestPath => Path.Combine(_directory, LatestFileName);

    public string Save(SnapshotRecord snapshot, int keep)
    {
        System.IO.Directory.CreateDirectory(_directory);

        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var path = Path.Combine(_directory, $"{FilePrefix}{stamp}{FileSuffix}");

        var json = JsonSerializer.Serialize(snapshot, SnapshotRecord.JsonOptions);
        var encoding = new UTF8Encoding(false);

        File.WriteAllText(path, json, encoding);

        // latest.json is a copy rather than a link so that an external tool — or the user —
        // can read it without caring how the timestamped files are named.
        File.WriteAllText(LatestPath, json, encoding);

        Prune(keep);
        return path;
    }

    public SnapshotRecord? Load(string? path = null)
    {
        var target = string.IsNullOrWhiteSpace(path) ? LatestPath : path;

        try
        {
            if (!File.Exists(target)) return null;

            var json = File.ReadAllText(target, new UTF8Encoding(false));
            return JsonSerializer.Deserialize<SnapshotRecord>(json, SnapshotRecord.JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log.Warn($"Could not read the snapshot at {target}. {ex.Message}");
            return null;
        }
    }

    /// <summary>Saved snapshots, newest first. latest.json is excluded: it is a duplicate.</summary>
    public IReadOnlyList<SavedSnapshot> List()
    {
        try
        {
            if (!System.IO.Directory.Exists(_directory)) return Array.Empty<SavedSnapshot>();

            return System.IO.Directory
                .EnumerateFiles(_directory, FilePrefix + "*" + FileSuffix)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => new SavedSnapshot(
                    file.FullName,
                    file.LastWriteTimeUtc,
                    Path.GetFileNameWithoutExtension(file.Name)[FilePrefix.Length..].Replace('_', ' ')))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Warn($"Could not list snapshots. {ex.Message}");
            return Array.Empty<SavedSnapshot>();
        }
    }

    private void Prune(int keep)
    {
        foreach (var stale in List().Skip(Math.Max(1, keep)))
        {
            try
            {
                File.Delete(stale.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Debug($"Could not delete the old snapshot {stale.Name}. {ex.Message}");
            }
        }
    }
}
