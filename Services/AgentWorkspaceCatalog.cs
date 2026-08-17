using ClaudeCodeCliHarness.Models;

namespace ClaudeCodeCliHarness.Services;

public sealed class AgentWorkspaceCatalog(
    IEnumerable<IAgentWorkspaceProvider> providers,
    ManagedAgentWorkspaceProvider managedProvider,
    AgentWorkspacePathPolicy paths)
{
    private readonly IReadOnlyList<IAgentWorkspaceProvider> _providers = providers.ToArray();

    public IReadOnlyList<AgentWorkspaceSummary> List()
    {
        return List(scope: null);
    }

    public IReadOnlyList<AgentWorkspaceSummary> List(string? scope)
    {
        return BuildCatalog(NormalizeScope(scope))
            .Select(item => item.Summary)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public AgentWorkspaceEnvironmentSummary GetEnvironment()
    {
        var trustedRoots = paths.GetTrustedRoots()
            .Concat(managedProvider.ListAutomaticallyTrustedLocalRoots())
            .DistinctBy(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), StringComparer.Ordinal)
            .Select(path => new AgentWorkspaceRootSummary(
                Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } name
                    ? name
                    : path,
                path))
            .ToArray();
        return new AgentWorkspaceEnvironmentSummary(
            paths.Mode,
            paths.ExecutionLocation,
            paths.IsLocalMode,
            true,
            paths.CanCloneFromGit,
            trustedRoots,
            paths.AllowedGitRepositoryHosts,
            paths.GetServerWorkspaceRoot());
    }

    public AgentWorkspaceDescriptor GetRequired(string workspaceId)
    {
        var descriptor = BuildCatalog().SingleOrDefault(item => item.Summary.Id == workspaceId);
        return descriptor ?? throw new KeyNotFoundException("The requested workspace is not in a trusted root.");
    }

    public async Task<AgentWorkspaceSummary> AddDirectoryAsync(
        AgentWorkspaceDirectoryRequest request,
        CancellationToken cancellationToken)
    {
        var descriptor = await managedProvider.AddDirectoryAsync(request, cancellationToken);
        return GetPreferredDescriptor(descriptor).Summary;
    }

    public async Task<AgentWorkspaceSummary> CreateOrTrustLocalWorkspaceAsync(
        AgentWorkspaceLocalRequest request,
        CancellationToken cancellationToken)
    {
        var descriptor = await managedProvider.CreateOrTrustLocalWorkspaceAsync(request, cancellationToken);
        return GetPreferredDescriptor(descriptor).Summary;
    }

    public async Task<AgentWorkspaceSummary> CreateDirectoryAsync(
        AgentWorkspaceCreateRequest request,
        CancellationToken cancellationToken)
    {
        var descriptor = await managedProvider.CreateDirectoryAsync(request, cancellationToken);
        return GetPreferredDescriptor(descriptor).Summary;
    }

    public async Task<AgentWorkspaceSummary> CloneRepositoryAsync(
        AgentWorkspaceCloneRequest request,
        CancellationToken cancellationToken)
    {
        var descriptor = await managedProvider.CloneRepositoryAsync(request, cancellationToken);
        return GetPreferredDescriptor(descriptor).Summary;
    }

    public async Task<AgentWorkspaceSummary> CloneRepositoryIntoServerWorkspaceAsync(
        AgentWorkspaceGitImportRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.WorkspaceId))
        {
            throw new ArgumentException("WorkspaceId is required.", nameof(request));
        }

        var workspace = GetRequired(request.WorkspaceId);
        var descriptor = await managedProvider.CloneRepositoryIntoServerWorkspaceAsync(workspace, request, cancellationToken);
        return GetPreferredDescriptor(descriptor).Summary;
    }

    public async Task<AgentWorkspaceSummary> CreateServerWorkspaceAsync(
        AgentWorkspaceServerCreateRequest request,
        CancellationToken cancellationToken)
    {
        var descriptor = await managedProvider.CreateServerWorkspaceAsync(request, cancellationToken);
        return GetPreferredDescriptor(descriptor).Summary;
    }

    private IReadOnlyList<AgentWorkspaceDescriptor> BuildCatalog(string? scope = null)
    {
        var descriptors = new Dictionary<string, AgentWorkspaceDescriptor>(StringComparer.Ordinal);
        var workspaceDescriptors = managedProvider.List().Concat(
            _providers
                .Where(provider => !ReferenceEquals(provider, managedProvider))
                .SelectMany(provider => provider.List()));
        foreach (var descriptor in workspaceDescriptors.Where(descriptor =>
                 scope is null || string.Equals(descriptor.Summary.WorkspaceScope, scope, StringComparison.Ordinal)))
        {
            if (!descriptors.ContainsKey(descriptor.Summary.Id))
            {
                descriptors.Add(descriptor.Summary.Id, descriptor);
            }
        }

        return descriptors.Values.ToArray();
    }

    private AgentWorkspaceDescriptor GetPreferredDescriptor(AgentWorkspaceDescriptor descriptor)
    {
        return BuildCatalog().FirstOrDefault(item =>
            string.Equals(item.Summary.WorkspaceScope, descriptor.Summary.WorkspaceScope, StringComparison.Ordinal) &&
            paths.PathsEqual(item.Path, descriptor.Path)) ?? descriptor;
    }

    private static string? NormalizeScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return null;
        }

        return scope.Trim().ToLowerInvariant() switch
        {
            "local" => "local",
            "server" => "server",
            _ => throw new ArgumentException("Workspace scope must be local or server.", nameof(scope))
        };
    }
}

public sealed record AgentWorkspaceDescriptor(
    AgentWorkspaceSummary Summary,
    string Path);