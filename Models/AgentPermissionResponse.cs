using System.Text.Json;

namespace ClaudeCodeCliHarness.Models;

public sealed record AgentPermissionResponse(
    string RequestId,
    string Decision,
    JsonElement? UpdatedInput = null,
    JsonElement? Answers = null,
    string? Response = null,
    bool Remember = false,
    bool Interrupt = false,
    string? Message = null);