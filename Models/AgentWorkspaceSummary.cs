namespace ClaudeCodeCliHarness.Models;

public sealed record AgentWorkspaceSummary(
    string Id,
    string Name,
    string Path,
    bool IsGitRepository);