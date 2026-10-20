// AlexLV Windows UI kit. Canonical copy: C:\projects\AlexLV.WindowsUi (see README.md).
using System;
using System.IO;
using System.Reflection;

namespace AlexLV.WindowsUi;

/// <summary>
/// Points the person's taskbar pins at the installed app. Setup replaces Start menu, desktop and
/// Startup shortcuts, but Windows keeps a pin made from an old portable copy, so the pin goes on
/// launching that stale EXE after the app moved to %LOCALAPPDATA%\Programs.
/// </summary>
public static class ShortcutRepair
{
    /// <summary>
    /// Retargets every pinned taskbar shortcut whose target has this app's EXE name but another path.
    /// Runs only from the installed location, so a development build never takes over the pins.
    /// Returns how many pins were changed; failures are ignored (a pin is a convenience).
    /// </summary>
    public static int RepairTaskbarPins(string? exePath = null, string? pinDirectory = null)
    {
        exePath ??= Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !IsInstalled(exePath)) return 0;
        pinDirectory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");
        if (!Directory.Exists(pinDirectory)) return 0;

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return 0;
        var shell = Activator.CreateInstance(shellType);
        var changed = 0;
        foreach (var pin in Directory.EnumerateFiles(pinDirectory, "*.lnk"))
        {
            try
            {
                var link = Invoke(shell, "CreateShortcut", pin);
                var target = (string?)Get(link, "TargetPath") ?? "";
                if (!NeedsRepair(target, exePath)) continue;
                Set(link, "TargetPath", exePath);
                Set(link, "WorkingDirectory", Path.GetDirectoryName(exePath) ?? "");
                Set(link, "IconLocation", exePath + ",0");
                Invoke(link, "Save");
                changed++;
            }
            catch
            {
                // A locked or broken pin stays as it was.
            }
        }
        return changed;
    }

    /// <summary>A pin needs repair when it launches this app's EXE name from any other path.</summary>
    public static bool NeedsRepair(string target, string exePath) =>
        target.Length > 0
        && string.Equals(Path.GetFileName(target), Path.GetFileName(exePath), StringComparison.OrdinalIgnoreCase)
        && !string.Equals(Path.GetFullPath(target), Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the EXE runs from the per-user install root, %LOCALAPPDATA%\Programs.</summary>
    public static bool IsInstalled(string exePath)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
                   + Path.DirectorySeparatorChar;
        return Path.GetFullPath(exePath).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static object? Invoke(object? target, string name, params object[] args) =>
        target?.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args);

    private static object? Get(object? target, string name) =>
        target?.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);

    private static void Set(object? target, string name, object value) =>
        target?.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, new[] { value });
}
