namespace ClaudeCodeCliHarness.Models;

public sealed record AgentSessionSummary(
    string Id,
    string Name,
    string WorkspaceId,
    string WorkspaceName,
    string Status,
    string PermissionMode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool IsIsolated,
    string? WorktreePath,
    string? ClaudeSessionId);