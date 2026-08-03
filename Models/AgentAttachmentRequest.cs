namespace ClaudeCodeCliHarness.Models;

public sealed record AgentAttachmentRequest(
    string MediaType,
    string Data,
    string? FileName = null);