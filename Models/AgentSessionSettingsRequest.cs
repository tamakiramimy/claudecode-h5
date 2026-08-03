namespace ClaudeCodeCliHarness.Models;

public sealed record AgentSessionSettingsRequest(
    string? Model = null,
    int? MaxThinkingTokens = null);