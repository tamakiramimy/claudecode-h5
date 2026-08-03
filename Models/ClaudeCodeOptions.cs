namespace ClaudeCodeCliHarness.Models;

public sealed class ClaudeCodeOptions
{
    public const string SectionName = "ClaudeCode";

    public string ExecutablePath { get; init; } = "claude";

    public string NodeExecutablePath { get; init; } = "node";

    public string AgentBridgePath { get; init; } = "bridge/src/agent-bridge.mjs";

    public string Mode { get; init; } = "env";

    public string? SettingsPath { get; init; }

    public string WorkspacePath { get; init; } = "agent-workspace";

    public string[] TrustedWorkspaceRoots { get; init; } = [];

    public int MaxConcurrentSessions { get; init; } = 4;

    public int MaxAttachmentBytes { get; init; } = 10 * 1024 * 1024;

    public int MaxAttachmentsPerMessage { get; init; } = 5;

    public bool EnableWorktreeIsolation { get; init; } = true;

    public int TimeoutSeconds { get; init; } = 600;
}