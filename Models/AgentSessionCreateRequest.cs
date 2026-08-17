namespace ClaudeCodeCliHarness.Models;

public sealed record AgentSessionCreateRequest(
    string WorkspaceId,
    string? Name = null,
    string? PermissionMode = null);