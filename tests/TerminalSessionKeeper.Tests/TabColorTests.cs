using System.Drawing;
using System.Text.Json;
using TerminalSessionKeeper.Logging;
using TerminalSessionKeeper.Restore;
using TerminalSessionKeeper.Snapshots;
using TerminalSessionKeeper.Terminal;

namespace TerminalSessionKeeper.Tests;

public class TabColorMemoryTests
{
    private const string Session = "457a75f6-7842-43bc-8cc0-59b4a05580a9";

    private static TabColorMemory Fresh(TempDirectory directory) =>
        new(NullLog.Instance, directory.File("tab-colors.json"));

    [Fact]
    public void A_colour_seen_once_is_not_recorded_yet()
    {
        using var directory = new TempDirectory();
        var memory = Fresh(directory);

        Assert.Null(memory.Observe(Session, "#DC143C"));
    }

    [Fact]
    public void A_colour_seen_twice_in_a_row_is_recorded()
    {
        using var directory = new TempDirectory();
        var memory = Fresh(directory);

        memory.Observe(Session, "#DC143C");

        Assert.Equal("#DC143C", memory.Observe(Session, "#DC143C"));
    }

    [Fact]
    public void A_one_off_reading_between_two_others_never_counts()
    {
        // The bad frame that turned a plain tab into #AAAAAA showed up exactly once.
        using var directory = new TempDirectory();
        var memory = Fresh(directory);

        memory.Observe(Session, "#DC143C");
        memory.Observe(Session, "#DC143C");

        Assert.Equal("#DC143C", memory.Observe(Session, "#AAAAAA"));
        Assert.Equal("#DC143C", memory.Observe(Session, "#DC143C"));
    }

    [Fact]
    public void Re_reading_a_restored_colour_does_not_let_it_drift()
    {
        // The loop that walked #DD153D to #FF0051: each restore repaints what the last sample
        // read, and un-blending multiplies every rounding error by more than three.
        using var directory = new TempDirectory();
        var memory = Fresh(directory);

        memory.Observe(Session, "#DD153D");
        Assert.Equal("#DD153D", memory.Observe(Session, "#DD153D"));

        foreach (var drifted in new[] { "#DB133B", "#DF173F", "#DD153D", "#DB143C" })
        {
            Assert.Equal("#DD153D", memory.Observe(Session, drifted));
        }
    }

    [Fact]
    public void A_colour_the_user_cleared_is_forgotten_after_two_snapshots()
    {
        using var directory = new TempDirectory();
        var memory = Fresh(directory);

        memory.Observe(Session, "#DC143C");
        memory.Observe(Session, "#DC143C");

        Assert.Equal("#DC143C", memory.Observe(Session, null));
        Assert.Null(memory.Observe(Session, null));
        Assert.Null(memory.Confirmed(Session));
    }

    [Fact]
    public void A_pinned_colour_counts_as_already_proven()
    {
        using var directory = new TempDirectory();
        var memory = Fresh(directory);

        memory.Confirm(Session, "#7B3FF2");

        Assert.Equal("#7B3FF2", memory.Confirmed(Session));
        Assert.Equal("#7B3FF2", memory.Observe(Session, "#7B3FF2"));
    }

    [Fact]
    public void What_was_confirmed_survives_a_restart()
    {
        using var directory = new TempDirectory();

        var first = Fresh(directory);
        first.Observe(Session, "#DC143C");
        first.Observe(Session, "#DC143C");
        first.Save();

        Assert.Equal("#DC143C", Fresh(directory).Confirmed(Session));
    }

    [Fact]
    public void A_tab_with_no_session_id_cannot_be_confirmed_and_is_passed_through() =>
        Assert.Equal("#DC143C", new TabColorMemory(NullLog.Instance,
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tsk-missing-" + Guid.NewGuid().ToString("N")))
            .Observe(string.Empty, "#DC143C"));

    [Theory]
    [InlineData("#DC143C", "#DC143C", true)]
    [InlineData("#DC143C", "#DB133B", true)]
    [InlineData("#DC143C", "#FF0051", false)]
    [InlineData(null, null, true)]
    [InlineData("#DC143C", null, false)]
    public void Two_readings_mean_the_same_thing_when_no_channel_is_far_apart(
        string? left, string? right, bool same) =>
        Assert.Equal(same, TabColorMemory.IsSame(left, right));
}

public class TabColorBackgroundTests
{
    private static readonly Color Strip = Color.FromArgb(0x33, 0x33, 0x33);

    [Fact]
    public void The_bare_strip_is_used_when_the_tabs_agree_with_it()
    {
        var background = TabColorSampler.Background(Strip, Color.FromArgb(0x2E, 0x2E, 0x2E), out var why);

        Assert.Equal(Strip, background);
        Assert.Empty(why);
    }

    [Fact]
    public void Neither_is_believed_when_the_two_disagree()
    {
        // The frame that came back black: the bare strip read #000000 while every tab read
        // #333333, and trusting the black one invented a colour for every uncoloured tab.
        var background = TabColorSampler.Background(Color.Black, Strip, out var why);

        Assert.Null(background);
        Assert.Contains("not settled", why);
    }

    [Fact]
    public void One_source_on_its_own_is_enough()
    {
        Assert.Equal(Strip, TabColorSampler.Background(Strip, null, out _));
        Assert.Equal(Strip, TabColorSampler.Background(null, Strip, out _));
    }

    [Fact]
    public void Nothing_at_all_is_not_a_background()
    {
        Assert.Null(TabColorSampler.Background(null, null, out var why));
        Assert.Contains("could not be identified", why);
    }
}

public class WtSettingsPatchTests
{
    private const string Settings = """
        {
            "$schema": "https://aka.ms/terminal-profiles-schema",
            "actions":
            [
                {
                    "command": "find",
                    "id": "User.find"
                }
            ],
            "copyOnSelect": false,
            "keybindings":
            [
                { "id": "User.find", "keys": "ctrl+f" }
            ],
            "profiles": { "defaults": {}, "list": [] }
        }
        """;

    [Fact]
    public void An_action_and_a_key_are_added_for_each_colour()
    {
        var patched = WtSettingsPatch.Insert(Settings, new[] { "#DC143C", "#1F90FF" });

        Assert.NotNull(patched);
        Assert.Contains("\"action\": \"setTabColor\", \"color\": \"#DC143C\"", patched);
        Assert.Contains("\"action\": \"setTabColor\", \"color\": \"#1F90FF\"", patched);
        Assert.Contains("ctrl+alt+shift+f13", patched);
        Assert.Contains("ctrl+alt+shift+f14", patched);

        // Still valid JSON, and the user's own entries are untouched.
        using var document = JsonDocument.Parse(patched!);
        Assert.Contains("\"id\": \"User.find\"", patched);
    }

    [Fact]
    public void Removing_them_again_gives_back_the_original_file()
    {
        var patched = WtSettingsPatch.Insert(Settings, new[] { "#DC143C", "#1F90FF" });
        var cleaned = WtSettingsPatch.RemoveEntries(patched!);

        Assert.Equal(Settings, cleaned);
    }

    [Fact]
    public void Removing_works_even_after_the_terminal_rewrites_the_file()
    {
        // Windows Terminal reformats settings.json whenever it saves one itself, so the entries
        // are found by id rather than by the text that was inserted.
        var reformatted = """
            {
                "actions": [
                    {
                        "command": {
                            "action": "setTabColor",
                            "color": "#DC143C"
                        },
                        "id": "TerminalSessionKeeper.RestoreTabColor.0"
                    },
                    { "command": "find", "id": "User.find" }
                ],
                "keybindings": [
                    {
                        "id": "TerminalSessionKeeper.RestoreTabColor.0",
                        "keys": "ctrl+alt+shift+f13"
                    }
                ]
            }
            """;

        var cleaned = WtSettingsPatch.RemoveEntries(reformatted);

        Assert.NotNull(cleaned);
        Assert.DoesNotContain(WtSettingsPatch.IdPrefix, cleaned);
        Assert.Contains("User.find", cleaned);

        using var document = JsonDocument.Parse(cleaned!);
        Assert.Equal(1, document.RootElement.GetProperty("actions").GetArrayLength());
        Assert.Equal(0, document.RootElement.GetProperty("keybindings").GetArrayLength());
    }

    [Fact]
    public void A_settings_file_with_no_actions_at_all_gets_the_arrays_it_needs()
    {
        var patched = WtSettingsPatch.Insert("""{ "copyOnSelect": false }""", new[] { "#DC143C" });

        Assert.NotNull(patched);
        using var document = JsonDocument.Parse(patched!);
        Assert.Equal(1, document.RootElement.GetProperty("actions").GetArrayLength());
        Assert.Equal(1, document.RootElement.GetProperty("keybindings").GetArrayLength());
        Assert.False(document.RootElement.GetProperty("copyOnSelect").GetBoolean());
    }

    [Fact]
    public void Nothing_to_remove_leaves_the_file_exactly_as_it_was() =>
        Assert.Equal(Settings, WtSettingsPatch.RemoveEntries(Settings));

    [Fact]
    public void A_file_with_windows_line_endings_comes_back_with_windows_line_endings()
    {
        var crlf = Settings.ReplaceLineEndings("\r\n");

        var patched = WtSettingsPatch.Insert(crlf, new[] { "#DC143C" });

        Assert.NotNull(patched);
        Assert.Contains("\r\n        { \"command\": { \"action\": \"setTabColor\"", patched);
        Assert.Equal(crlf, WtSettingsPatch.RemoveEntries(patched!));
    }
}

public class TabColorApplierTests
{
    [Fact]
    public void Tabs_are_grouped_by_colour_so_each_colour_costs_one_action()
    {
        var passes = TabColorApplier.Passes(new[]
        {
            new ColoredTab(0, "#DC143C"),
            new ColoredTab(1, "#1F90FF"),
            new ColoredTab(2, "#dc143c"),
        });

        var groups = Assert.Single(passes);
        Assert.Equal(2, groups.Count);
        Assert.Equal(new[] { 0, 2 }, groups[0].Select(tab => tab.Index));
    }

    [Fact]
    public void More_colours_than_there_are_keys_take_another_pass()
    {
        var tabs = Enumerable.Range(0, WtSettingsPatch.MaxColorsPerPass + 2)
            .Select(index => new ColoredTab(index, $"#0000{index:X2}"))
            .ToList();

        var passes = TabColorApplier.Passes(tabs);

        Assert.Equal(2, passes.Count);
        Assert.Equal(WtSettingsPatch.MaxColorsPerPass, passes[0].Count);
        Assert.Equal(2, passes[1].Count);
    }
}

public class RestoreWindowNameTests
{
    [Fact]
    public void Each_restore_gets_a_window_of_its_own()
    {
        // wt -w <name> joins the window that already has that name, so a fixed one made the
        // second restore append its tabs to the window the first restore built.
        var first = RestoreService.WindowName("tskrestore", new DateTimeOffset(2026, 9, 17, 14, 4, 29, TimeSpan.Zero));
        var second = RestoreService.WindowName("tskrestore", new DateTimeOffset(2026, 9, 17, 15, 21, 3, TimeSpan.Zero));

        Assert.NotEqual(first, second);
        Assert.StartsWith("tskrestore-", first);
        Assert.Equal("tskrestore-140429", first);
        Assert.Equal("tskrestore-152103", second);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_blank_configured_name_still_produces_one(string? configured) =>
        Assert.StartsWith("tskrestore-", RestoreService.WindowName(configured!, DateTimeOffset.Now));

    [Fact]
    public void The_configured_name_is_the_prefix() =>
        Assert.StartsWith("my-tabs-", RestoreService.WindowName("  my-tabs  ", DateTimeOffset.Now));
}
