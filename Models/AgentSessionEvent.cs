using System.Text.Json;

namespace ClaudeCodeCliHarness.Models;

public sealed record AgentSessionEvent(
    long Sequence,
    string Type,
    JsonElement Payload,
    DateTimeOffset OccurredAt);