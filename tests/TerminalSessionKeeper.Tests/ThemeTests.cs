using AlexLV.WindowsUi;
using TerminalSessionKeeper.Logging;
using TerminalSessionKeeper.Settings;

namespace TerminalSessionKeeper.Tests;

public sealed class ThemeTests
{
    [Fact]
    public void The_theme_follows_Windows_by_default() => Assert.Equal(AppTheme.System, new AppSettings().Theme);

    [Theory]
    [InlineData(AppTheme.System, true, false)]
    [InlineData(AppTheme.System, false, true)]
    [InlineData(AppTheme.Light, false, false)]
    [InlineData(AppTheme.Dark, true, true)]
    public void System_resolves_from_the_Windows_app_mode(AppTheme choice, bool windowsUsesLight, bool expectDark) =>
        Assert.Equal(expectDark, ThemeManager.Resolve(choice, windowsUsesLight));

    [Fact]
    public void The_chosen_theme_survives_a_save_and_load()
    {
        using var temp = new TempDirectory();
        var store = new SettingsStore(NullLog.Instance, temp.File("settings.json"));
        store.Save(new AppSettings { Theme = AppTheme.Dark });

        Assert.Equal(AppTheme.Dark, store.Load().Theme);
    }

    [Fact]
    public void A_hand_edited_unknown_theme_falls_back_to_System()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.File("settings.json"), "{ \"Theme\": 42 }");

        Assert.Equal(AppTheme.System, new SettingsStore(NullLog.Instance, temp.File("settings.json")).Load().Theme);
    }

    [Fact]
    public void A_draft_leaves_the_running_settings_unchanged()
    {
        var running = new AppSettings { Theme = AppTheme.Light, AutoSnapshotIntervalMinutes = 10 };
        var draft = running.Clone();

        draft.Theme = AppTheme.Dark;
        draft.AutoSnapshotIntervalMinutes = 30;

        Assert.Equal(AppTheme.Light, running.Theme);
        Assert.Equal(10, running.AutoSnapshotIntervalMinutes);
    }

    [Fact]
    public void The_app_keeps_culture_data_for_WPF_text_layout()
    {
        // WPF creates real cultures when it measures text; invariant globalization crashed the first TextBox.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "TerminalSessionKeeper", "TerminalSessionKeeper.csproj")))
            directory = directory.Parent;
        Assert.NotNull(directory);

        var project = File.ReadAllText(Path.Combine(directory!.FullName, "src", "TerminalSessionKeeper", "TerminalSessionKeeper.csproj"));
        Assert.DoesNotContain("<InvariantGlobalization>true</InvariantGlobalization>", project);
    }
}
