using System.Diagnostics;

namespace ClaudeCodeCliHarness.Services;

public sealed class AgentWorktreeService(ILogger<AgentWorktreeService> logger)
{
    public async Task<AgentWorktreeAllocation> AllocateAsync(
        string workspacePath,
        string sessionId,
        bool enableIsolation,
        CancellationToken cancellationToken)
    {
        if (!enableIsolation)
        {
            return new AgentWorktreeAllocation(workspacePath, false, null);
        }

        var repositoryRoot = await TryGetRepositoryRootAsync(workspacePath, cancellationToken);
        if (repositoryRoot is null)
        {
            return new AgentWorktreeAllocation(workspacePath, false, null);
        }

        var shortSessionId = sessionId[..Math.Min(sessionId.Length, 12)];
        var worktreeName = $"h5-{shortSessionId}";
        var worktreePath = Path.Combine(repositoryRoot, ".claude", "worktrees", worktreeName);
        if (Directory.Exists(worktreePath))
        {
            throw new InvalidOperationException($"The Agent worktree already exists: {worktreePath}");
        }

        await RunGitAsync(
            repositoryRoot,
            ["worktree", "add", "-b", $"h5/{worktreeName}", worktreePath, "HEAD"],
            cancellationToken);
        logger.LogInformation("Created isolated Agent worktree {WorktreePath}", worktreePath);
        return new AgentWorktreeAllocation(worktreePath, true, worktreePath);
    }

    private static async Task<string?> TryGetRepositoryRootAsync(string workspacePath, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(workspacePath, ["rev-parse", "--show-toplevel"], cancellationToken, allowFailure: true);
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput)
            ? Path.GetFullPath(result.StandardOutput.Trim())
            : null;
    }

    private static async Task<GitCommandResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool allowFailure = false)
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
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Git did not start while preparing the Agent worktree.");
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
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
            throw new InvalidOperationException($"Git worktree setup failed: {result.StandardError.Trim()}");
        }

        return result;
    }

    private sealed record GitCommandResult(int ExitCode, string StandardOutput, string StandardError);
}

public sealed record AgentWorktreeAllocation(
    string WorkingDirectory,
    bool IsIsolated,
    string? WorktreePath);