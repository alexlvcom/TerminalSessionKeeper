using System.Drawing;
using TerminalSessionKeeper.Agents;
using TerminalSessionKeeper.Logging;
using TerminalSessionKeeper.Terminal;

namespace TerminalSessionKeeper.Tests;

public class AgentCommandsTests
{
    [Theory]
    [InlineData("claude", "abc", "claude --resume abc")]
    [InlineData("codex", "abc", "codex resume abc")]
    [InlineData("junie", "session-260511-145144-uw5k", "junie --resume --session-id=session-260511-145144-uw5k")]
    [InlineData("agy", "01a06c4d", "agy --conversation 01a06c4d")]
    public void Each_agent_reopens_a_named_session(string agent, string id, string expected) =>
        Assert.Equal(expected, AgentCommands.Resume(agent, id));

    [Fact]
    public void An_agent_is_recognised_whatever_its_casing() =>
        Assert.Equal("claude --resume abc", AgentCommands.Resume("Claude", "abc"));

    [Theory]
    [InlineData("claude", null)]
    [InlineData("claude", "")]
    [InlineData(null, "abc")]
    [InlineData("something-else", "abc")]
    public void Nothing_to_resume_means_no_command(string? agent, string? id) =>
        Assert.Null(AgentCommands.Resume(agent, id));

    [Fact]
    public void Every_named_agent_can_build_a_command()
    {
        // A new agent added to the list without a command would restore silently broken.
        foreach (var agent in AgentCommands.All) Assert.NotNull(AgentCommands.Resume(agent, "id"));
    }
}

public class AgySessionsTests
{
    [Fact]
    public void A_workspace_list_is_json()
    {
        var workspaces = AgySessions.ParseWorkspaces("[\"file:///home/me/source/cart\"]");

        Assert.Equal(new[] { "/home/me/source/cart" }, workspaces);
    }

    [Fact]
    public void Several_workspaces_all_count()
    {
        var workspaces = AgySessions.ParseWorkspaces(
            "[\"file:///home/me/a\",\"file:///home/me/b\"]");

        Assert.Equal(new[] { "/home/me/a", "/home/me/b" }, workspaces);
    }

    [Fact]
    public void An_older_build_wrote_a_bare_path()
    {
        Assert.Equal(new[] { @"c:/users/me/thing" },
            AgySessions.ParseWorkspaces(@"C:\Users\Me\Thing"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("[not json")]
    public void Nothing_usable_yields_no_workspace(string? raw) =>
        Assert.Empty(AgySessions.ParseWorkspaces(raw));

    [Fact]
    public void A_long_preview_is_cut_to_fit_a_tab()
    {
        var shortened = AgySessions.Shorten(new string('x', 200));

        Assert.NotNull(shortened);
        Assert.Equal(60, shortened.Length);
        Assert.EndsWith("...", shortened);
    }

    [Fact]
    public void A_machine_without_antigravity_costs_agy_and_nothing_else()
    {
        var sessions = new AgySessions(NullLog.Instance,
            new[] { Path.Combine(Path.GetTempPath(), "tsk-no-agy-" + Guid.NewGuid().ToString("N")) });

        Assert.Null(sessions.ConversationFor("/home/me/source/cart"));
        Assert.Null(sessions.ConversationFor(null));
    }
}

public class JunieSessionsTests
{
    private const string Line =
        "{\"sessionId\":\"session-260511-145144-uw5k\",\"createdAt\":1778500304458," +
        "\"updatedAt\":1778503703427,\"projectDir\":\"/home/me/source/metrics-ui\"," +
        "\"taskName\":\"Identify AI Model Type\"}";

    [Fact]
    public void The_index_names_the_directory_projectDir()
    {
        // The reference implementation looked for "projectPath", which is not a field Junie
        // writes — so no Junie tab ever matched its directory.
        var sessions = JunieSessions.Parse(new[] { Line });

        var session = Assert.Single(sessions);
        Assert.Equal("session-260511-145144-uw5k", session.Id);
        Assert.Equal("/home/me/source/metrics-ui", session.ProjectDir);
        Assert.Equal("Identify AI Model Type", session.TaskName);
        Assert.Equal(1778503703427, session.UpdatedAt);
    }

    [Fact]
    public void A_half_written_line_is_skipped_not_fatal()
    {
        var sessions = JunieSessions.Parse(new[] { Line, "{\"sessionId\":", string.Empty });

        Assert.Single(sessions);
    }

    [Fact]
    public void A_record_without_a_session_id_is_not_a_session() =>
        Assert.Empty(JunieSessions.Parse(new[] { "{\"projectDir\":\"/home/me\"}" }));

    [Fact]
    public void A_machine_without_junie_costs_junie_and_nothing_else()
    {
        var sessions = new JunieSessions(NullLog.Instance,
            Path.Combine(Path.GetTempPath(), "tsk-no-junie-" + Guid.NewGuid().ToString("N")));

        Assert.Null(sessions.SessionFor("/home/me/source/metrics-ui"));
    }
}

public class TabColorSamplerTests
{
    [Fact]
    public void Undoing_the_blend_recovers_the_colour_that_was_set()
    {
        // Measured on this machine: an unfocused tab shows 30% of its colour over the strip
        // background. These are the real readings from tabs opened with known colours.
        var background = Color.FromArgb(0x2E, 0x2E, 0x2E);

        var recovered = TabColorSampler.Unblend(Color.FromArgb(0x45, 0x33, 0x69), background, 0.30);

        // #7B3FF2 was the colour actually set.
        Assert.InRange(recovered.R, 0x7B - 4, 0x7B + 4);
        Assert.InRange(recovered.G, 0x3F - 4, 0x3F + 4);
        Assert.InRange(recovered.B, 0xF2 - 4, 0xF2 + 4);
    }

    [Fact]
    public void A_tab_the_colour_of_the_background_has_no_colour_to_recover()
    {
        var background = Color.FromArgb(0x2E, 0x2E, 0x2E);

        Assert.True(TabColorSampler.IsSameColor(background, Color.FromArgb(0x2E, 0x2E, 0x2E)));
        Assert.True(TabColorSampler.IsSameColor(background, Color.FromArgb(0x30, 0x2C, 0x31)));
        Assert.False(TabColorSampler.IsSameColor(background, Color.FromArgb(0x62, 0x26, 0x32)));
    }

    [Fact]
    public void Recovering_a_saturated_colour_clamps_rather_than_wrapping()
    {
        // Blue is already near the ceiling here; pushing it back out must not overflow.
        var recovered = TabColorSampler.Unblend(
            Color.FromArgb(0x29, 0x4B, 0x6D), Color.FromArgb(0x2E, 0x2E, 0x2E), 0.30);

        Assert.InRange(recovered.R, 0, 255);
        Assert.InRange(recovered.G, 0, 255);
        Assert.Equal(255, recovered.B);
    }

    [Theory]
    [InlineData(0, 0, 0, "#000000")]
    [InlineData(123, 63, 242, "#7B3FF2")]
    [InlineData(255, 255, 255, "#FFFFFF")]
    public void Colours_are_written_the_way_wt_expects_them(int r, int g, int b, string expected) =>
        Assert.Equal(expected, TabColorSampler.ToHex(Color.FromArgb(r, g, b)));

    private static readonly Color Strip = Color.FromArgb(0x2E, 0x2E, 0x2E);
    private static readonly Color TerminalBody = Color.FromArgb(0x0C, 0x0C, 0x0C);

    [Fact]
    public void An_unfocused_tab_the_colour_of_the_strip_has_no_colour() =>
        Assert.Null(TabColorSampler.Interpret(Strip, isSelected: false, Strip, TerminalBody));

    [Fact]
    public void An_unfocused_coloured_tab_is_recovered()
    {
        // Crimson #DC143C renders as #622632 on an unfocused tab; this is the real reading.
        var recovered = TabColorSampler.Interpret(
            Color.FromArgb(0x62, 0x26, 0x32), isSelected: false, Strip, TerminalBody);

        Assert.Equal("#DB133B", recovered);
    }

    [Fact]
    public void The_focused_tab_is_taken_at_face_value()
    {
        // No dimming is applied to the active tab, so what is drawn is what was set.
        var recovered = TabColorSampler.Interpret(
            Color.FromArgb(0x1F, 0x9E, 0x8F), isSelected: true, Strip, TerminalBody);

        Assert.Equal("#1F9E8F", recovered);
    }

    [Fact]
    public void An_uncoloured_focused_tab_shows_the_terminal_and_is_left_alone() =>
        Assert.Null(TabColorSampler.Interpret(TerminalBody, isSelected: true, Strip, TerminalBody));

    [Fact]
    public void A_focused_tab_is_left_alone_when_the_terminal_background_is_unknown() =>
        Assert.Null(TabColorSampler.Interpret(
            Color.FromArgb(0x1F, 0x9E, 0x8F), isSelected: true, Strip, terminalBackground: null));

    [Fact]
    public void Nothing_read_means_no_colour() =>
        Assert.Null(TabColorSampler.Interpret(null, isSelected: false, Strip, TerminalBody));

    [Fact]
    public void One_unfocused_tab_is_not_enough_to_call_it_the_background()
    {
        // Mistaking the only coloured tab for the background would strip the colour from every
        // tab that has one.
        var tabs = new[] { new TabElement("a", Rectangle.Empty, false) };

        Assert.Null(TabColorSampler.StripBackground(tabs, new Color?[] { Strip }));
    }

    [Fact]
    public void Two_tabs_that_agree_are_the_background()
    {
        var tabs = new[]
        {
            new TabElement("a", Rectangle.Empty, false),
            new TabElement("b", Rectangle.Empty, false),
            new TabElement("c", Rectangle.Empty, false),
        };

        var readings = new Color?[] { Strip, Color.FromArgb(0x62, 0x26, 0x32), Strip };

        Assert.Equal(Strip, TabColorSampler.StripBackground(tabs, readings));
    }

    [Fact]
    public void The_focused_tab_never_votes_for_the_background()
    {
        var tabs = new[]
        {
            new TabElement("a", Rectangle.Empty, true),
            new TabElement("b", Rectangle.Empty, true),
        };

        Assert.Null(TabColorSampler.StripBackground(tabs, new Color?[] { Strip, Strip }));
    }
}
