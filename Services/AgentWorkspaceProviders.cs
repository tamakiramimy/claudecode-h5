using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeCodeCliHarness.Models;

namespace ClaudeCodeCliHarness.Services;

public interface IAgentWorkspaceProvider
{
    IReadOnlyList<AgentWorkspaceDescriptor> List();
}

public sealed class ConfiguredAgentWorkspaceProvider(AgentWorkspacePathPolicy paths) : IAgentWorkspaceProvider
{
    public IReadOnlyList<AgentWorkspaceDescriptor> List()
    {
        var descriptors = new Dictionary<string, AgentWorkspaceDescriptor>(StringComparer.Ordinal);
        foreach (var root in paths.GetTrustedRoots())
        {
            AddWorkspace(descriptors, root);
            foreach (var childDirectory in EnumerateImmediateDirectories(root))
            {
                AddWorkspace(descriptors, childDirectory);
            }
        }

        return descriptors.Values.ToArray();
    }

    private void AddWorkspace(
        IDictionary<string, AgentWorkspaceDescriptor> descriptors,
        string workspacePath)
    {
        var id = AgentWorkspaceIdentity.CreateId(workspacePath);
        if (descriptors.ContainsKey(id))
        {
            return;
        }

        var name = Path.GetFileName(workspacePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var summary = new AgentWorkspaceSummary(
            id,
            string.IsNullOrWhiteSpace(name) ? workspacePath : name,
            workspacePath,
            IsGitRepository(workspacePath),
            "configured",
            "local",
            null,
            "local");
        descriptors.Add(id, new AgentWorkspaceDescriptor(summary, workspacePath));
    }

    private static IEnumerable<string> EnumerateImmediateDirectories(string root)
    {
        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(root).Take(100).ToArray();
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (var directory in directories)
        {
            var directoryInfo = new DirectoryInfo(directory);
            if (directoryInfo.LinkTarget is null)
            {
                yield return directoryInfo.FullName;
            }
        }
    }

    private static bool IsGitRepository(string workspacePath)
    {
        return File.Exists(Path.Combine(workspacePath, ".git")) ||
               Directory.Exists(Path.Combine(workspacePath, ".git"));
    }
}

public sealed class ServerAgentWorkspaceProvider(AgentWorkspacePathPolicy paths) : IAgentWorkspaceProvider
{
    public IReadOnlyList<AgentWorkspaceDescriptor> List()
    {
        var root = paths.GetServerWorkspaceRoot();
        var descriptors = new Dictionary<string, AgentWorkspaceDescriptor>(StringComparer.Ordinal);
        foreach (var directory in EnumerateImmediateDirectories(root))
        {
            var id = AgentWorkspaceIdentity.CreateId("server", directory);
            var name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var summary = new AgentWorkspaceSummary(
                id,
                string.IsNullOrWhiteSpace(name) ? directory : name,
                directory,
                IsGitRepository(directory),
                "server-directory",
                "remote",
                null,
                "server");
            descriptors.TryAdd(id, new AgentWorkspaceDescriptor(summary, directory));
        }

        return descriptors.Values.ToArray();
    }

    private static IEnumerable<string> EnumerateImmediateDirectories(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root)
                .Take(100)
                .Where(directory => new DirectoryInfo(directory).LinkTarget is null)
                .ToArray();
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static bool IsGitRepository(string workspacePath)
    {
        return File.Exists(Path.Combine(workspacePath, ".git")) ||
               Directory.Exists(Path.Combine(workspacePath, ".git"));
    }
}

public sealed class ManagedAgentWorkspaceProvider(
    AgentWorkspacePathPolicy paths,
    AgentWorkspaceGitClient git,
    ILogger<ManagedAgentWorkspaceProvider> logger) : IAgentWorkspaceProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly object _syncRoot = new();
    private List<ManagedAgentWorkspaceRecord>? _records;

    public IReadOnlyList<AgentWorkspaceDescriptor> List()
    {
        lock (_syncRoot)
        {
            return GetRecordsLocked()
                .Where(IsAvailableWorkspace)
                .Select(ToDescriptor)
                .ToArray();
        }
    }

    public async Task<AgentWorkspaceDescriptor> AddDirectoryAsync(
        AgentWorkspaceDirectoryRequest request,
        CancellationToken cancellationToken)
    {
        var workspacePath = paths.ResolveExistingLocalDirectory(request.Path);
        var gitStatus = await git.InspectAsync(workspacePath, cancellationToken);
        return SaveWorkspace(
            workspacePath,
            Path.GetFileName(workspacePath),
            "directory",
            null,
            gitStatus,
            "local");
    }

    public async Task<AgentWorkspaceDescriptor> CreateOrTrustLocalWorkspaceAsync(
        AgentWorkspaceLocalRequest request,
        CancellationToken cancellationToken)
    {
        var workspacePath = paths.CreateOrTrustLocalDirectory(request.Path);
        var gitStatus = await git.InspectAsync(workspacePath, cancellationToken);
        return SaveWorkspace(
            workspacePath,
            Path.GetFileName(workspacePath),
            "directory",
            null,
            gitStatus,
            "local");
    }

    public async Task<AgentWorkspaceDescriptor> CreateDirectoryAsync(
        AgentWorkspaceCreateRequest request,
        CancellationToken cancellationToken)
    {
        var workspacePath = paths.CreateLocalDirectory(
            request.RootPath,
            request.Name,
            ListAutomaticallyTrustedLocalRoots());
        var gitStatus = await git.InspectAsync(workspacePath, cancellationToken);
        return SaveWorkspace(
            workspacePath,
            request.Name.Trim(),
            "created",
            null,
            gitStatus,
            "local");
    }

    public async Task<AgentWorkspaceDescriptor> CreateServerWorkspaceAsync(
        AgentWorkspaceServerCreateRequest request,
        CancellationToken cancellationToken)
    {
        var workspacePath = paths.CreateServerDirectory(request.Name);
        var gitStatus = await git.InspectAsync(workspacePath, cancellationToken);
        return SaveWorkspace(
            workspacePath,
            request.Name.Trim(),
            "server-created",
            null,
            gitStatus,
            "server");
    }

    public IReadOnlyList<string> ListAutomaticallyTrustedLocalRoots()
    {
        lock (_syncRoot)
        {
            return GetRecordsLocked()
                .Where(record =>
                    GetScope(record) == "local" &&
                    string.Equals(record.Source, "directory", StringComparison.Ordinal) &&
                    paths.IsExistingDirectoryAccessible(record.Path))
                .Select(record => record.Path)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
    }

    public async Task<AgentWorkspaceDescriptor> CloneRepositoryAsync(
        AgentWorkspaceCloneRequest request,
        CancellationToken cancellationToken)
    {
        var scope = NormalizeScope(request.WorkspaceScope);
        var repository = git.ValidateRepositoryUrl(request.RepositoryUrl);
        var workspaceName = string.IsNullOrWhiteSpace(request.Name)
            ? repository.SuggestedName
            : AgentWorkspacePathPolicy.ValidateDirectoryName(request.Name, nameof(request.Name));
        var workspaceRoot = scope == "server" ? paths.GetServerWorkspaceRoot() : paths.GetManagedWorkspaceRoot();
        var destinationPath = GetAvailableWorkspacePath(workspaceRoot, workspaceName);

        try
        {
            await git.CloneAsync(repository.Url, destinationPath, cancellationToken);
            var workspacePath = scope == "server"
                ? paths.ResolveExistingServerDirectory(destinationPath)
                : paths.ResolveManagedWorkspaceDirectory(destinationPath);
            var gitStatus = await git.InspectAsync(workspacePath, cancellationToken);
            if (!gitStatus.IsRepository)
            {
                throw new InvalidOperationException("The cloned directory is not a Git repository.");
            }

            return SaveWorkspace(workspacePath, workspaceName, "git", repository.Url, gitStatus, scope);
        }
        catch
        {
            DeleteFailedClone(destinationPath);
            throw;
        }
    }

    public async Task<AgentWorkspaceDescriptor> CloneRepositoryIntoServerWorkspaceAsync(
        AgentWorkspaceDescriptor workspace,
        AgentWorkspaceGitImportRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(workspace.Summary.WorkspaceScope, "server", StringComparison.Ordinal))
        {
            throw new ArgumentException("Git repository download requires a server workspace.", nameof(request));
        }

        var workspacePath = paths.ResolveExistingServerDirectory(workspace.Path);
        if (Directory.EnumerateFileSystemEntries(workspacePath).Any())
        {
            throw new InvalidOperationException("The selected workspace must be empty before downloading repository code.");
        }

        var repository = git.ValidateRepositoryUrl(request.RepositoryUrl);
        await git.CloneIntoAsync(repository.Url, workspacePath, cancellationToken);
        var gitStatus = await git.InspectAsync(workspacePath, cancellationToken);
        if (!gitStatus.IsRepository)
        {
            throw new InvalidOperationException("The downloaded workspace is not a Git repository.");
        }

        return SaveWorkspace(
            workspacePath,
            workspace.Summary.Name,
            "git",
            repository.Url,
            gitStatus,
            "server");
    }

    private AgentWorkspaceDescriptor SaveWorkspace(
        string workspacePath,
        string name,
        string source,
        string? repositoryUrl,
        AgentGitStatus gitStatus,
        string scope)
    {
        lock (_syncRoot)
        {
            var records = GetRecordsLocked();
            var existing = records.SingleOrDefault(record =>
                GetScope(record) == scope && paths.PathsEqual(record.Path, workspacePath));
            if (existing is not null)
            {
                var updated = existing with
                {
                    Name = name,
                    Source = source,
                    RepositoryUrl = repositoryUrl,
                    IsGitRepository = gitStatus.IsRepository,
                    GitStatus = gitStatus.Status,
                    WorkspaceScope = scope
                };
                records[records.IndexOf(existing)] = updated;
                SaveRecordsLocked(records);
                return ToDescriptor(updated);
            }

            var record = new ManagedAgentWorkspaceRecord(
                AgentWorkspaceIdentity.CreateId(scope, workspacePath),
                name,
                workspacePath,
                source,
                repositoryUrl,
                gitStatus.IsRepository,
                gitStatus.Status,
                DateTimeOffset.UtcNow,
                scope);
            records.Add(record);
            SaveRecordsLocked(records);
            return ToDescriptor(record);
        }
    }

    private List<ManagedAgentWorkspaceRecord> GetRecordsLocked()
    {
        if (_records is not null)
        {
            return _records;
        }

        var dataPath = paths.GetWorkspaceDataPath();
        if (!File.Exists(dataPath))
        {
            _records = [];
            return _records;
        }

        try
        {
            _records = JsonSerializer.Deserialize<List<ManagedAgentWorkspaceRecord>>(
                File.ReadAllText(dataPath),
                JsonOptions) ?? [];
            return _records;
        }
        catch (JsonException exception)
        {
            logger.LogError(exception, "Managed workspace registry is invalid at {RegistryPath}", dataPath);
            throw new InvalidOperationException("Managed workspace registry is invalid. Fix or remove the registry file before adding another workspace.", exception);
        }
    }

    private void SaveRecordsLocked(IReadOnlyList<ManagedAgentWorkspaceRecord> records)
    {
        var dataPath = paths.GetWorkspaceDataPath();
        var temporaryPath = $"{dataPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(records, JsonOptions));
            File.Move(temporaryPath, dataPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
        }
    }

    private bool IsAvailableWorkspace(ManagedAgentWorkspaceRecord record)
    {
        if (!Directory.Exists(record.Path))
        {
            return false;
        }

        try
        {
            return GetScope(record) == "server"
                ? paths.IsWithinServerWorkspaceRoot(record.Path)
                : string.Equals(record.Source, "directory", StringComparison.Ordinal)
                    ? paths.IsExistingDirectoryAccessible(record.Path)
                : record.Source == "git"
                    ? paths.IsWithinManagedWorkspaceRoot(record.Path)
                    : paths.IsWithinTrustedRoots(record.Path);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Ignoring unavailable managed workspace {WorkspaceId}", record.Id);
            return false;
        }
    }

    private AgentWorkspaceDescriptor ToDescriptor(ManagedAgentWorkspaceRecord record)
    {
        var summary = new AgentWorkspaceSummary(
            record.Id,
            record.Name,
            record.Path,
            record.IsGitRepository,
            record.Source,
            GetScope(record) == "server" ? "remote" : "local",
            record.GitStatus,
            GetScope(record));
        return new AgentWorkspaceDescriptor(summary, record.Path);
    }

    private static string GetAvailableWorkspacePath(string root, string name)
    {
        for (var suffix = 1; suffix <= 100; suffix++)
        {
            var candidateName = suffix == 1 ? name : $"{name}-{suffix}";
            var candidatePath = Path.Combine(root, candidateName);
            if (!Directory.Exists(candidatePath) && !File.Exists(candidatePath))
            {
                return candidatePath;
            }
        }

        throw new InvalidOperationException("No available managed workspace directory name could be allocated.");
    }

    private static void DeleteFailedClone(string destinationPath)
    {
        try
        {
            var directory = new DirectoryInfo(destinationPath);
            if (directory.Exists && directory.LinkTarget is null)
            {
                directory.Delete(recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string NormalizeScope(string? scope)
    {
        return string.Equals(scope, "server", StringComparison.OrdinalIgnoreCase) ? "server" : "local";
    }

    private static string GetScope(ManagedAgentWorkspaceRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.WorkspaceScope))
        {
            return NormalizeScope(record.WorkspaceScope);
        }

        return record.Source.StartsWith("server-", StringComparison.Ordinal) ? "server" : "local";
    }
}

public sealed record ManagedAgentWorkspaceRecord(
    string Id,
    string Name,
    string Path,
    string Source,
    string? RepositoryUrl,
    bool IsGitRepository,
    string? GitStatus,
    DateTimeOffset CreatedAt,
    string WorkspaceScope = "local");

internal static class AgentWorkspaceIdentity
{
    public static string CreateId(string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    public static string CreateId(string scope, string path)
    {
        return string.Equals(scope, "local", StringComparison.OrdinalIgnoreCase)
            ? CreateId(path)
            : CreateId($"{scope}\0{path}");
    }
}