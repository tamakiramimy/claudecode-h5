namespace ClaudeCodeCliHarness.Models;

public sealed record AgentWorkspaceSummary(
    string Id,
    string Name,
    string Path,
    bool IsGitRepository,
    string Source = "configured",
    string ExecutionLocation = "local",
    string? GitStatus = null,
    string WorkspaceScope = "local");