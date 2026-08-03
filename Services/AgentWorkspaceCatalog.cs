using System.Security.Cryptography;
using System.Text;
using ClaudeCodeCliHarness.Models;
using Microsoft.Extensions.Options;

namespace ClaudeCodeCliHarness.Services;

public sealed class AgentWorkspaceCatalog(
    IOptions<ClaudeCodeOptions> options,
    IHostEnvironment environment)
{
    private readonly ClaudeCodeOptions _options = options.Value;

    public IReadOnlyList<AgentWorkspaceSummary> List()
    {
        return BuildCatalog()
            .Select(item => item.Summary)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public AgentWorkspaceDescriptor GetRequired(string workspaceId)
    {
        var descriptor = BuildCatalog().SingleOrDefault(item => item.Summary.Id == workspaceId);
        return descriptor ?? throw new KeyNotFoundException("The requested workspace is not in a trusted root.");
    }

    private IReadOnlyList<AgentWorkspaceDescriptor> BuildCatalog()
    {
        var descriptors = new Dictionary<string, AgentWorkspaceDescriptor>(StringComparer.Ordinal);
        foreach (var root in GetTrustedRoots())
        {
            AddWorkspace(descriptors, root);

            foreach (var childDirectory in EnumerateImmediateDirectories(root))
            {
                AddWorkspace(descriptors, childDirectory);
            }
        }

        return descriptors.Values.ToArray();
    }

    private IEnumerable<string> GetTrustedRoots()
    {
        var configuredRoots = _options.TrustedWorkspaceRoots.Length > 0
            ? _options.TrustedWorkspaceRoots
            : [_options.WorkspacePath];

        foreach (var configuredRoot in configuredRoots.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var absolutePath = Path.IsPathRooted(configuredRoot)
                ? configuredRoot
                : Path.GetFullPath(configuredRoot, environment.ContentRootPath);

            if (Directory.Exists(absolutePath))
            {
                yield return Path.GetFullPath(absolutePath);
            }
        }
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

    private static void AddWorkspace(
        IDictionary<string, AgentWorkspaceDescriptor> descriptors,
        string workspacePath)
    {
        var id = CreateId(workspacePath);
        if (descriptors.ContainsKey(id))
        {
            return;
        }

        var name = Path.GetFileName(workspacePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var summary = new AgentWorkspaceSummary(
            id,
            string.IsNullOrWhiteSpace(name) ? workspacePath : name,
            workspacePath,
            File.Exists(Path.Combine(workspacePath, ".git")) || Directory.Exists(Path.Combine(workspacePath, ".git")));
        descriptors.Add(id, new AgentWorkspaceDescriptor(summary, workspacePath));
    }

    private static string CreateId(string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}

public sealed record AgentWorkspaceDescriptor(
    AgentWorkspaceSummary Summary,
    string Path);