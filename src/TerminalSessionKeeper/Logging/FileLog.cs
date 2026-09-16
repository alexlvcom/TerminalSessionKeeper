using System.Text;

namespace TerminalSessionKeeper.Logging;

public interface ILog
{
    void Debug(string message);
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();
    public void Debug(string message) { }
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? exception = null) { }
}

/// <summary>
/// Minimal rotating file log. Nothing beyond the app's own state, the tab data it captured
/// and exception text is ever written: no environment dumps, no transcript contents.
/// </summary>
public sealed class FileLog : ILog, IDisposable
{
    private const long MaxBytes = 512 * 1024;
    private const int MaxFiles = 5;

    private readonly string _directory;
    private readonly string _currentFile;
    private readonly object _gate = new();
    private bool _disabled;

    public FileLog(string? directory = null)
    {
        _directory = string.IsNullOrWhiteSpace(directory) ? Services.AppPaths.LogDirectory : directory;
        _currentFile = Path.Combine(_directory, "terminalsessionkeeper.log");

        try
        {
            System.IO.Directory.CreateDirectory(_directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Logging is best effort; the app must still run.
            _disabled = true;
        }
    }

    public void Debug(string message) => Write("DEBUG", message);
    public void Info(string message) => Write("INFO ", message);
    public void Warn(string message) => Write("WARN ", message);

    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    private void Write(string level, string message)
    {
        if (_disabled) return;

        lock (_gate)
        {
            try
            {
                Rotate();
                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(_currentFile, line, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _disabled = true;
            }
        }
    }

    private void Rotate()
    {
        var info = new FileInfo(_currentFile);
        if (!info.Exists || info.Length < MaxBytes) return;

        var oldest = Path.Combine(_directory, $"terminalsessionkeeper.{MaxFiles - 1}.log");
        if (File.Exists(oldest)) File.Delete(oldest);

        for (var i = MaxFiles - 2; i >= 1; i--)
        {
            var from = Path.Combine(_directory, $"terminalsessionkeeper.{i}.log");
            var to = Path.Combine(_directory, $"terminalsessionkeeper.{i + 1}.log");
            if (File.Exists(from)) File.Move(from, to, overwrite: true);
        }

        File.Move(_currentFile, Path.Combine(_directory, "terminalsessionkeeper.1.log"), overwrite: true);
    }

    public void Dispose() { }
}
