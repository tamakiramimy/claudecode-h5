using ClaudeCodeCliHarness.Models;
using Microsoft.Extensions.Options;

namespace ClaudeCodeCliHarness.Services;

public sealed class AgentWorkspacePathPolicy(
    IOptions<ClaudeCodeOptions> options,
    IHostEnvironment environment)
{
    private readonly ClaudeCodeOptions _options = options.Value;

    public string Mode => NormalizeMode(_options.WorkspaceMode);

    public bool IsLocalMode => Mode == "local";

    public string ExecutionLocation => IsLocalMode ? "local" : "remote";

    public string GetServerWorkspaceRoot()
    {
        var configuredRoot = string.IsNullOrWhiteSpace(_options.ServerWorkspaceRoot)
            ? Path.Combine(environment.ContentRootPath, ".claudecode")
            : GetAbsolutePath(_options.ServerWorkspaceRoot);
        Directory.CreateDirectory(configuredRoot);
        return ResolveRealDirectory(configuredRoot);
    }

    public IReadOnlyList<string> AllowedGitRepositoryHosts => _options.AllowedGitRepositoryHosts
        .Where(host => !string.IsNullOrWhiteSpace(host))
        .Select(host => host.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public bool CanCloneFromGit => AllowedGitRepositoryHosts.Count > 0;

    public IReadOnlyList<string> GetTrustedRoots()
    {
        var configuredRoots = _options.TrustedWorkspaceRoots.Length > 0
            ? _options.TrustedWorkspaceRoots
            : [_options.WorkspacePath];
        var roots = new List<string>();

        foreach (var configuredRoot in configuredRoots.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            try
            {
                var absolutePath = GetAbsolutePath(configuredRoot);
                if (!Directory.Exists(absolutePath))
                {
                    continue;
                }

                var resolvedPath = ResolveRealDirectory(absolutePath);
                if (!roots.Any(root => PathsEqual(root, resolvedPath)))
                {
                    roots.Add(resolvedPath);
                }
            }
            catch (ArgumentException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return roots;
    }

    public string ResolveExistingLocalDirectory(string requestedPath)
    {
        EnsureLocalPathAccessIsAllowed();
        return ResolveExistingDirectory(requestedPath);
    }

    public string CreateOrTrustLocalDirectory(string requestedPath)
    {
        EnsureLocalPathAccessIsAllowed();
        if (string.IsNullOrWhiteSpace(requestedPath) || !Path.IsPathRooted(requestedPath))
        {
            throw new ArgumentException("Path must be an absolute directory path.", nameof(requestedPath));
        }

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedPath));
        if (Directory.Exists(fullPath))
        {
            return ResolveExistingDirectory(fullPath);
        }

        if (File.Exists(fullPath))
        {
            throw new ArgumentException("Path points to a file, not a directory.", nameof(requestedPath));
        }

        var parentPath = Path.GetDirectoryName(fullPath);
        var directoryName = Path.GetFileName(fullPath);
        if (string.IsNullOrWhiteSpace(parentPath) || string.IsNullOrWhiteSpace(directoryName))
        {
            throw new ArgumentException("Path must name a workspace directory below an existing parent directory.", nameof(requestedPath));
        }

        var resolvedParentPath = ResolveExistingDirectory(parentPath);
        return CreateDirectoryInRoot(resolvedParentPath, directoryName);
    }

    public string ResolveExistingServerDirectory(string requestedPath)
    {
        var resolvedPath = ResolveExistingDirectory(requestedPath);
        if (!IsWithinServerWorkspaceRoot(resolvedPath))
        {
            throw new ArgumentException("Path is outside the configured server workspace root.", nameof(requestedPath));
        }

        return resolvedPath;
    }

    public string CreateLocalDirectory(
        string rootPath,
        string name,
        IReadOnlyCollection<string>? automaticallyTrustedRoots = null)
    {
        EnsureLocalPathAccessIsAllowed();
        return CreateTrustedDirectory(rootPath, name, automaticallyTrustedRoots);
    }

    public string CreateServerDirectory(string name)
    {
        return CreateDirectoryInRoot(GetServerWorkspaceRoot(), name);
    }

    public bool IsExistingDirectoryAccessible(string path)
    {
        try
        {
            _ = ResolveExistingDirectory(path);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string ResolveExistingDirectory(string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath) || !Path.IsPathRooted(requestedPath))
        {
            throw new ArgumentException("Path must be an absolute directory path.", nameof(requestedPath));
        }

        var resolvedPath = ResolveRealDirectory(requestedPath);
        EnsureDirectoryAccess(resolvedPath);
        return resolvedPath;
    }

    private string CreateTrustedDirectory(
        string rootPath,
        string name,
        IReadOnlyCollection<string>? automaticallyTrustedRoots)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("RootPath is required.", nameof(rootPath));
        }

        var resolvedRoot = ResolveRealDirectory(rootPath);
        var isConfiguredRoot = GetTrustedRoots().Any(root => PathsEqual(root, resolvedRoot));
        var isAutomaticallyTrustedRoot = automaticallyTrustedRoots?.Any(root => PathsEqual(root, resolvedRoot)) == true;
        if (!isConfiguredRoot && !isAutomaticallyTrustedRoot)
        {
            throw new ArgumentException("RootPath is not a trusted workspace root.", nameof(rootPath));
        }

        return CreateDirectoryInRoot(resolvedRoot, name);
    }

    public bool IsWithinServerWorkspaceRoot(string path)
    {
        return IsPathWithinRoot(path, GetServerWorkspaceRoot());
    }

    private string CreateDirectoryInRoot(string rootPath, string name)
    {
        var directoryName = ValidateDirectoryName(name, nameof(name));
        var candidatePath = Path.GetFullPath(Path.Combine(rootPath, directoryName));
        if (!IsPathWithinRoot(candidatePath, rootPath))
        {
            throw new ArgumentException("The new workspace must stay inside the configured workspace root.", nameof(name));
        }

        if (Directory.Exists(candidatePath) || File.Exists(candidatePath))
        {
            throw new InvalidOperationException("A file system entry with that workspace name already exists.");
        }

        Directory.CreateDirectory(candidatePath);
        var resolvedPath = ResolveRealDirectory(candidatePath);
        if (!IsPathWithinRoot(resolvedPath, rootPath))
        {
            throw new InvalidOperationException("The new workspace resolves outside the configured workspace root.");
        }

        EnsureDirectoryAccess(resolvedPath);
        return resolvedPath;
    }

    public string ResolveManagedWorkspaceDirectory(string path)
    {
        var resolvedPath = ResolveRealDirectory(path);
        var managedRoot = GetManagedWorkspaceRoot();
        if (!IsPathWithinRoot(resolvedPath, managedRoot))
        {
            throw new InvalidOperationException("The managed workspace resolves outside the service workspace root.");
        }

        EnsureDirectoryAccess(resolvedPath);
        return resolvedPath;
    }

    public string GetManagedWorkspaceRoot()
    {
        var configuredRoot = string.IsNullOrWhiteSpace(_options.ManagedWorkspaceRoot)
            ? Path.Combine(Path.GetDirectoryName(GetWorkspaceDataPath())!, "managed-workspaces")
            : GetAbsolutePath(_options.ManagedWorkspaceRoot);
        Directory.CreateDirectory(configuredRoot);
        return ResolveRealDirectory(configuredRoot);
    }

    public string GetWorkspaceDataPath()
    {
        var dataPath = string.IsNullOrWhiteSpace(_options.WorkspaceDataPath)
            ? Path.Combine(GetApplicationDataDirectory(), "workspaces.json")
            : GetAbsolutePath(_options.WorkspaceDataPath);
        Directory.CreateDirectory(Path.GetDirectoryName(dataPath)!);
        return dataPath;
    }

    public bool IsWithinTrustedRoots(string path)
    {
        return GetTrustedRoots().Any(root => IsPathWithinRoot(path, root));
    }

    public bool IsWithinManagedWorkspaceRoot(string path)
    {
        return IsPathWithinRoot(path, GetManagedWorkspaceRoot());
    }

    public bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            GetPathComparison());
    }

    public static string ValidateDirectoryName(string? value, string parameterName)
    {
        var name = value?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 ||
            name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains(Path.DirectorySeparatorChar) ||
            name.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("Name must be a single valid directory name with at most 100 characters.", parameterName);
        }

        return name;
    }

    private void EnsureLocalPathAccessIsAllowed()
    {
        if (!IsLocalMode)
        {
            throw new InvalidOperationException("This server cannot access a browser-local path in remote mode. Use an authorized Git repository, upload a snapshot, or connect a local connector.");
        }
    }

    private void EnsureWithinTrustedRoots(string path)
    {
        if (!IsWithinTrustedRoots(path))
        {
            throw new ArgumentException("Path is outside the configured trusted workspace roots.");
        }
    }

    private static void EnsureDirectoryAccess(string path)
    {
        try
        {
            using var enumerator = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            _ = enumerator.MoveNext();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ArgumentException("The workspace directory cannot be read.", nameof(path), exception);
        }

        var probePath = Path.Combine(path, $".claude-h5-access-{Guid.NewGuid():N}");
        try
        {
            using var probe = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ArgumentException("The workspace directory cannot be written.", nameof(path), exception);
        }
        finally
        {
            try
            {
                File.Delete(probePath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private string GetApplicationDataDirectory()
    {
        var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(applicationData)
            ? Path.Combine(environment.ContentRootPath, ".claude-h5-data")
            : Path.Combine(applicationData, "ClaudeCodeH5");
    }

    private string GetAbsolutePath(string path)
    {
        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(path, environment.ContentRootPath);
    }

    private static string ResolveRealDirectory(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("Path does not have a file system root.", nameof(path));
        }

        var currentPath = root;
        foreach (var segment in fullPath[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            var directory = new DirectoryInfo(currentPath);
            if (!directory.Exists)
            {
                throw new ArgumentException("Directory does not exist.", nameof(path));
            }

            if (directory.LinkTarget is not null)
            {
                var target = directory.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not DirectoryInfo targetDirectory)
                {
                    throw new ArgumentException("Directory symbolic link could not be resolved.", nameof(path));
                }

                currentPath = targetDirectory.FullName;
            }
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentPath));
    }

    private static bool IsPathWithinRoot(string path, string root)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (string.Equals(normalizedPath, normalizedRoot, GetPathComparison()))
        {
            return true;
        }

        var rootWithSeparator = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(rootWithSeparator, GetPathComparison());
    }

    private static StringComparison GetPathComparison()
    {
        return OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    private static string NormalizeMode(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "local" => "local",
            "remote" => "remote",
            _ => throw new InvalidOperationException("WorkspaceMode must be local or remote.")
        };
    }
}