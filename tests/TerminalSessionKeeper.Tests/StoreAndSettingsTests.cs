using System.Text.Json;
using TerminalSessionKeeper.Logging;
using TerminalSessionKeeper.Model;
using TerminalSessionKeeper.Settings;
using TerminalSessionKeeper.Snapshots;

namespace TerminalSessionKeeper.Tests;

/// <summary>A scratch directory that cleans itself up.</summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "tsk-tests-" + Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Test cleanup only.
        }
    }
}

public class AppSettingsTests
{
    [Theory]
    [InlineData(0, AppSettings.MinSnapshotIntervalMinutes)]
    [InlineData(-5, AppSettings.MinSnapshotIntervalMinutes)]
    [InlineData(10, 10)]
    [InlineData(99999, AppSettings.MaxSnapshotIntervalMinutes)]
    public void A_hand_edited_interval_is_clamped_before_it_reaches_a_timer(int stored, int expected)
    {
        var settings = new AppSettings { AutoSnapshotIntervalMinutes = stored };

        Assert.Equal(expected, (int)settings.SnapshotInterval.TotalMinutes);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(20, 20)]
    [InlineData(10_000, 500)]
    public void The_number_of_snapshots_kept_is_clamped_too(int stored, int expected) =>
        Assert.Equal(expected, new AppSettings { KeepSnapshots = stored }.EffectiveKeepSnapshots);

    [Fact]
    public void Resume_commands_wait_at_the_prompt_unless_asked_otherwise()
    {
        // A dozen agents and their MCP servers booting at once, right after a reboot, is the
        // worst possible moment for it.
        Assert.False(new AppSettings().AutoResume);
    }

    [Fact]
    public void Settings_survive_a_round_trip()
    {
        using var directory = new TempDirectory();
        var store = new SettingsStore(NullLog.Instance, directory.File("settings.json"));

        store.Save(new AppSettings
        {
            AutoResume = true,
            AutoSnapshotIntervalMinutes = 3,
            PinTitles = true,
            RestoreWslEnvironment = "RESTORE_MESSAGE=hello world",
            TypeCdIntoWslTabs = false,
        });
        var loaded = store.Load();

        Assert.True(loaded.AutoResume);
        Assert.True(loaded.PinTitles);
        Assert.Equal(3, loaded.AutoSnapshotIntervalMinutes);
        Assert.Equal("RESTORE_MESSAGE=hello world", loaded.RestoreWslEnvironment);
        Assert.False(loaded.TypeCdIntoWslTabs);
    }

    [Fact]
    public void Unreadable_settings_fall_back_to_the_defaults()
    {
        using var directory = new TempDirectory();
        var path = directory.File("settings.json");
        File.WriteAllText(path, "{ this is not json");

        var loaded = new SettingsStore(NullLog.Instance, path).Load();

        Assert.True(loaded.AutoSnapshotEnabled);
        Assert.Equal(AppSettings.DefaultSnapshotIntervalMinutes, loaded.AutoSnapshotIntervalMinutes);
    }
}

public class SnapshotStoreTests
{
    private static SnapshotRecord Sample(string title = "checkout-api")
    {
        var tab = TabBuilder.Wsl("/home/me/source/checkout-api", agent: "claude",
            resume: "claude --resume 043c02a0-a42b-4441-84d2-754721971e63");
        tab.Title = title;

        return new SnapshotRecord
        {
            CapturedAt = DateTimeOffset.Now.ToString("o"),
            Machine = "TEST",
            User = "test",
            BootTimeUtc = DateTimeOffset.UtcNow,
            Windows = { new WindowRecord { ProcessId = 1234, Tabs = { tab } } },
        };
    }

    [Fact]
    public void A_saved_snapshot_reads_back_as_it_was()
    {
        using var directory = new TempDirectory();
        var store = new SnapshotStore(NullLog.Instance, directory.Path);

        store.Save(Sample(), keep: 20);
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(1, loaded.TabCount);
        Assert.Equal("checkout-api", loaded.Tabs.Single().Title);
        Assert.Equal("claude --resume 043c02a0-a42b-4441-84d2-754721971e63", loaded.Tabs.Single().ResumeCommand);
    }

    [Fact]
    public void The_file_is_camel_cased_json_a_person_can_edit()
    {
        using var directory = new TempDirectory();
        var store = new SnapshotStore(NullLog.Instance, directory.Path);

        var path = store.Save(Sample(), keep: 20);
        var json = File.ReadAllText(path);

        Assert.Contains("\"resumeCommand\"", json);
        Assert.Contains("\"wtSession\"", json);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("windows").ValueKind);
    }

    [Fact]
    public void Only_the_newest_snapshots_are_kept()
    {
        using var directory = new TempDirectory();
        var store = new SnapshotStore(NullLog.Instance, directory.Path);

        // The file name is per-second, so the pruning is exercised directly on hand-made files.
        for (var index = 0; index < 5; index++)
        {
            File.WriteAllText(Path.Combine(directory.Path, $"snapshot-2026-09-16_10-00-0{index}.json"), "{}");
        }

        store.Save(Sample(), keep: 3);

        Assert.Equal(3, store.List().Count);
        Assert.True(File.Exists(store.LatestPath));
    }

    [Fact]
    public void A_missing_snapshot_is_not_an_error()
    {
        using var directory = new TempDirectory();

        Assert.Null(new SnapshotStore(NullLog.Instance, directory.Path).Load());
        Assert.Empty(new SnapshotStore(NullLog.Instance, directory.Path).List());
    }
}

public class OverrideStoreTests
{
    [Fact]
    public void An_entry_is_found_by_its_exact_directory()
    {
        var overrides = new[]
        {
            new TabOverride { Cwd = "/home/me/source/checkout-api", Title = "ABC-1022", TabColor = "#7B3FF2" },
        };

        var found = OverrideStore.For(overrides, "/home/me/source/checkout-api", agent: "claude");

        Assert.NotNull(found);
        Assert.Equal("ABC-1022", found.Title);
        Assert.Equal("#7B3FF2", found.EffectiveColor);
    }

    [Fact]
    public void An_entry_that_names_an_agent_only_matches_that_agent()
    {
        var overrides = new[]
        {
            new TabOverride { Cwd = "/home/me/source/cart", Agent = "codex", Title = "codex cart" },
        };

        Assert.NotNull(OverrideStore.For(overrides, "/home/me/source/cart", "codex"));
        Assert.Null(OverrideStore.For(overrides, "/home/me/source/cart", "claude"));
    }

    [Fact]
    public void Either_spelling_of_the_colour_is_accepted()
    {
        Assert.Equal("#101010", new TabOverride { Color = "#101010" }.EffectiveColor);
        Assert.Equal("#202020", new TabOverride { TabColor = "#202020" }.EffectiveColor);
    }

    [Fact]
    public void A_typo_in_the_file_costs_the_overrides_but_never_the_snapshot()
    {
        using var directory = new TempDirectory();
        var path = directory.File("overrides.json");
        File.WriteAllText(path, "[ { \"cwd\": oops }");

        Assert.Empty(new OverrideStore(NullLog.Instance, path).Load());
    }

    [Fact]
    public void An_empty_file_is_created_so_there_is_something_to_edit()
    {
        using var directory = new TempDirectory();
        var path = directory.File("overrides.json");

        new OverrideStore(NullLog.Instance, path).EnsureExists();

        Assert.True(File.Exists(path));
        Assert.Empty(new OverrideStore(NullLog.Instance, path).Load());
    }
}
