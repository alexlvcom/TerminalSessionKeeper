using TerminalSessionKeeper.Logging;

namespace TerminalSessionKeeper.Agents;

/// <summary>What one snapshot learned about an agent tab: which session, and what to call it.</summary>
public sealed record AgentSession(string? SessionId, string? Title, string? ResumeCommand);

/// <summary>
/// The Windows-side session lookup for every supported agent, held together for one snapshot.
///
/// Each backing store is read at most once per snapshot and answered from memory afterwards,
/// and the claimed-id tracking lives here too, so ten tabs cost one pass over each store and
/// two tabs in one directory still come back with different conversations.
/// </summary>
public sealed class WindowsAgentState
{
    private readonly ClaudeSessions _claude = new();
    private readonly CodexSessions _codex;
    private readonly JunieSessions _junie;
    private readonly AgySessions _agy;

    public WindowsAgentState(ILog log)
    {
        _codex = new CodexSessions(log);
        _junie = new JunieSessions(log);
        _agy = new AgySessions(log);
    }

    /// <summary>
    /// Resolves the live session for an agent running in <paramref name="cwd"/>. Never throws:
    /// a tab with no resolvable session still restores, just without a command at its prompt.
    /// </summary>
    public AgentSession Resolve(string agent, string? cwd, string? commandLine = null)
    {
        switch (agent.ToLowerInvariant())
        {
            case AgentCommands.Claude:
            {
                var id = _claude.SessionIdFor(cwd);
                return new AgentSession(id, ClaudeSessions.SessionTitle(id), AgentCommands.Resume(agent, id));
            }

            case AgentCommands.Codex:
            {
                var thread = _codex.ThreadFor(cwd);
                var id = CodexSessions.SessionIdFromCommandLine(commandLine) ?? thread?.Id;
                return new AgentSession(id, id == thread?.Id ? CodexSessions.DisplayTitle(thread) : null,
                    AgentCommands.Resume(agent, id));
            }

            case AgentCommands.Junie:
            {
                var session = _junie.SessionFor(cwd);
                return new AgentSession(session?.Id, session?.TaskName,
                    AgentCommands.Resume(agent, session?.Id));
            }

            case AgentCommands.Agy:
            {
                var conversation = _agy.ConversationFor(cwd);
                return new AgentSession(conversation?.Id, conversation?.Title,
                    AgentCommands.Resume(agent, conversation?.Id));
            }

            default:
                return new AgentSession(null, null, null);
        }
    }
}
