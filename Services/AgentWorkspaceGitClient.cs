using System.Diagnostics;
using ClaudeCodeCliHarness.Models;
using Microsoft.Extensions.Options;

namespace ClaudeCodeCliHarness.Services;

public sealed class AgentWorkspaceGitClient(
    IOptions<ClaudeCodeOptions> options,
    ILogger<AgentWorkspaceGitClient> logger)
{
    private readonly ClaudeCodeOptions _options = options.Value;

    public AgentGitRepository ValidateRepositoryUrl(string? repositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl) ||
            !Uri.TryCreate(repositoryUrl.Trim(), UriKind.Absolute, out var repositoryUri) ||
            !string.Equals(repositoryUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(repositoryUri.UserInfo) ||
            !string.IsNullOrEmpty(repositoryUri.Query) ||
            !string.IsNullOrEmpty(repositoryUri.Fragment))
        {
            throw new ArgumentException("RepositoryUrl must be an HTTPS repository URL without credentials, a query, or a fragment.", nameof(repositoryUrl));
        }

        var allowedHosts = _options.AllowedGitRepositoryHosts
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .Select(host => host.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (allowedHosts.Count == 0)
        {
            throw new InvalidOperationException("Git workspace cloning is not configured. Add ClaudeCode:AllowedGitRepositoryHosts before cloning a repository.");
        }

        if (!allowedHosts.Contains(repositoryUri.Host))
        {
            throw new ArgumentException("Repository host is not in the configured allowed Git repository hosts.", nameof(repositoryUrl));
        }

        var suggestedName = Path.GetFileName(Uri.UnescapeDataString(repositoryUri.AbsolutePath.TrimEnd('/')));
        if (suggestedName.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            suggestedName = suggestedName[..^4];
        }

        return new AgentGitRepository(
            repositoryUri.AbsoluteUri,
            AgentWorkspacePathPolicy.ValidateDirectoryName(suggestedName, nameof(repositoryUrl)));
    }

    public async Task<AgentGitStatus> InspectAsync(string workspacePath, CancellationToken cancellationToken)
    {
        var repositoryCheck = await RunGitAsync(
            workspacePath,
            ["rev-parse", "--show-toplevel"],
            cancellationToken,
            allowFailure: true);
        if (repositoryCheck.ExitCode != 0 || string.IsNullOrWhiteSpace(repositoryCheck.StandardOutput))
        {
            return new AgentGitStatus(false, null);
        }

        var repositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryCheck.StandardOutput.Trim()));
        var normalizedWorkspacePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(repositoryRoot, normalizedWorkspacePath, pathComparison))
        {
            return new AgentGitStatus(false, null);
        }

        var status = await RunGitAsync(
            workspacePath,
            ["status", "--porcelain=v1", "--untracked-files=no"],
            cancellationToken,
            allowFailure: true);
        if (status.ExitCode != 0)
        {
            throw new InvalidOperationException("Git repository status could not be read.");
        }

        return new AgentGitStatus(true, string.IsNullOrWhiteSpace(status.StandardOutput) ? "clean" : "dirty");
    }

    public async Task CloneAsync(string repositoryUrl, string destinationPath, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            Path.GetDirectoryName(destinationPath)!,
            ["clone", "--", repositoryUrl, destinationPath],
            cancellationToken,
            allowFailure: true);
        if (result.ExitCode != 0)
        {
            throw new ArgumentException("Git clone failed. Verify repository access and the configured allowed host.", nameof(repositoryUrl));
        }

        logger.LogInformation("Created managed Git workspace at {WorkspacePath}", destinationPath);
    }

    public async Task CloneIntoAsync(string repositoryUrl, string workspacePath, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            workspacePath,
            ["clone", "--", repositoryUrl, "."],
            cancellationToken,
            allowFailure: true);
        if (result.ExitCode != 0)
        {
            throw new ArgumentException("Git clone failed. Verify repository access and that the selected workspace is empty.", nameof(repositoryUrl));
        }

        logger.LogInformation("Downloaded Git repository into workspace {WorkspacePath}", workspacePath);
    }

    private static async Task<GitCommandResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool allowFailure)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Git did not start while preparing the workspace.");
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        var result = new GitCommandResult(
            process.ExitCode,
            await standardOutputTask,
            await standardErrorTask);
        if (!allowFailure && result.ExitCode != 0)
        {
            throw new InvalidOperationException("Git workspace operation failed.");
        }

        return result;
    }

    private sealed record GitCommandResult(int ExitCode, string StandardOutput, string StandardError);
}

public sealed record AgentGitRepository(string Url, string SuggestedName);

public sealed record AgentGitStatus(bool IsRepository, string? Status);