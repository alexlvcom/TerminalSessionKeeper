using System.IO;

namespace TerminalSessionKeeper.Ui;

/// <summary>Per-PC release acknowledgement, separate from settings drafts and Drive sync.</summary>
internal sealed class WhatsNewState(string? path = null)
{
    private readonly string _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TerminalSessionKeeper", "last-seen-release.txt");

    public bool ShouldShow(string currentVersion)
    {
        if (!Version.TryParse(currentVersion, out var current)) return false;
        try
        {
            return !File.Exists(_path) || !Version.TryParse(File.ReadAllText(_path).Trim(), out var previous) || current > previous;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    public void MarkShown(string version)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, version);
        File.Move(temp, _path, overwrite: true);
    }
}
