namespace ClaudeCodeCliHarness.Models;

public sealed record AgentPromptRequest(
    string? Message,
    IReadOnlyList<AgentAttachmentRequest>? Attachments = null);