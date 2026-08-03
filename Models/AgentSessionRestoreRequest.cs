namespace ClaudeCodeCliHarness.Models;

public sealed record AgentSessionRestoreRequest(
    string WorkspaceId,
    string ClaudeSessionId,
    string? Name = null,
    string? PermissionMode = null,
    string? Model = null,
    int? MaxThinkingTokens = null);