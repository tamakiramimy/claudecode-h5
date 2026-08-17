namespace ClaudeCodeCliHarness.Models;

public sealed record AgentWorkspaceDirectoryRequest(string Path);

public sealed record AgentWorkspaceLocalRequest(string Path);

public sealed record AgentWorkspaceCreateRequest(string Name, string RootPath);

public sealed record AgentWorkspaceServerCreateRequest(string Name);

public sealed record AgentWorkspaceCloneRequest(string RepositoryUrl, string? Name, string WorkspaceScope = "local");

public sealed record AgentWorkspaceGitImportRequest(string WorkspaceId, string RepositoryUrl);

public sealed record AgentWorkspaceRootSummary(string Name, string Path);

public sealed record AgentWorkspaceEnvironmentSummary(
    string Mode,
    string ExecutionLocation,
    bool CanUseLocalPaths,
    bool CanUseServerPaths,
    bool CanCloneFromGit,
    IReadOnlyList<AgentWorkspaceRootSummary> TrustedRoots,
    IReadOnlyList<string> AllowedGitRepositoryHosts,
    string ServerWorkspaceRoot);