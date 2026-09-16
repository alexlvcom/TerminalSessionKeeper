namespace TerminalSessionKeeper.Agents;

/// <summary>
/// The agents the app knows how to put back, and the command that resumes each one.
///
/// Kept in one place because both sides need it: the Windows side builds these directly, and
/// the WSL probe builds the same strings inside the distro. Adding an agent means touching
/// this, the probe, and nothing else.
/// </summary>
public static class AgentCommands
{
    public const string Claude = "claude";
    public const string Codex = "codex";
    public const string Junie = "junie";

    /// <summary>Antigravity's CLI. The executable is <c>agy</c>, not <c>antigravity</c>.</summary>
    public const string Agy = "agy";

    /// <summary>Every agent name that can appear in a snapshot.</summary>
    public static readonly string[] All = { Claude, Codex, Junie, Agy };

    /// <summary>
    /// The command that reopens <paramref name="sessionId"/>, or null when the agent is unknown
    /// or there is no session to reopen.
    /// </summary>
    public static string? Resume(string? agent, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(agent) || string.IsNullOrWhiteSpace(sessionId)) return null;

        return agent.ToLowerInvariant() switch
        {
            Claude => $"claude --resume {sessionId}",
            Codex => $"codex resume {sessionId}",
            Junie => $"junie --resume --session-id={sessionId}",

            // agy has --continue for "the most recent", but a tab has to name its own
            // conversation or two tabs in one project would both reopen the same one.
            Agy => $"agy --conversation {sessionId}",
            _ => null,
        };
    }
}
