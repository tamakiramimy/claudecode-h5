using System.Diagnostics;
using ClaudeCodeCliHarness.Models;
using Microsoft.Extensions.Options;

namespace ClaudeCodeCliHarness.Services;

public sealed class ClaudeCommandFactory(IOptions<ClaudeCodeOptions> options, IHostEnvironment environment)
{
    private readonly ClaudeCodeOptions _options = options.Value;

    public ProcessStartInfo Create(string prompt, string? resumeSessionId)
    {
        var workingDirectory = ResolvePath(_options.WorkspacePath);
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"Configured Claude Code workspace does not exist: {workingDirectory}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveClaudeExecutablePath(),
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        ConfigureMode(startInfo);
        startInfo.ArgumentList.Add("--print");
        startInfo.ArgumentList.Add("--output-format");
        startInfo.ArgumentList.Add("stream-json");
        startInfo.ArgumentList.Add("--verbose");
        startInfo.ArgumentList.Add("--include-partial-messages");

        if (!string.IsNullOrWhiteSpace(resumeSessionId))
        {
            startInfo.ArgumentList.Add("--resume");
            startInfo.ArgumentList.Add(resumeSessionId);
        }

        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(prompt);
        return startInfo;
    }

    public string WorkspaceName => Path.GetFileName(ResolvePath(_options.WorkspacePath).TrimEnd(Path.DirectorySeparatorChar));

    public string Mode => _options.Mode;

    public TimeSpan Timeout => TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 30, 3_600));

    public string ResolveClaudeExecutablePath() => ResolveExecutablePath(_options.ExecutablePath);

    public string ResolveAgentBridgePath()
    {
        var path = ResolvePath(_options.AgentBridgePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Configured Agent Bridge script does not exist.", path);
        }

        return path;
    }

    public ClaudeBridgeRuntimeOptions GetBridgeRuntimeOptions()
    {
        return _options.Mode.ToLowerInvariant() switch
        {
            "env" => new ClaudeBridgeRuntimeOptions(null, null, false),
            "settings" => new ClaudeBridgeRuntimeOptions(ResolveSettingsPath(), ["user", "project", "local"], true),
            "isolated-settings" => new ClaudeBridgeRuntimeOptions(ResolveSettingsPath(), [], true),
            _ => throw new InvalidOperationException("ClaudeCode:Mode must be env, settings, or isolated-settings.")
        };
    }

    public void ConfigureAgentBridgeEnvironment(ProcessStartInfo startInfo)
    {
        if (!GetBridgeRuntimeOptions().RemoveInheritedGatewayEnvironment)
        {
            return;
        }

        foreach (var variable in GatewayEnvironmentVariables)
        {
            startInfo.Environment.Remove(variable);
        }
    }

    private void ConfigureMode(ProcessStartInfo startInfo)
    {
        switch (_options.Mode.ToLowerInvariant())
        {
            case "env":
                return;
            case "settings":
            case "isolated-settings":
                var settingsPath = ResolveSettingsPath();
                foreach (var variable in GatewayEnvironmentVariables)
                {
                    startInfo.Environment.Remove(variable);
                }

                startInfo.ArgumentList.Add("--settings");
                startInfo.ArgumentList.Add(settingsPath);
                if (_options.Mode.Equals("isolated-settings", StringComparison.OrdinalIgnoreCase))
                {
                    startInfo.ArgumentList.Add("--setting-sources");
                    startInfo.ArgumentList.Add(string.Empty);
                }

                return;
            default:
                throw new InvalidOperationException("ClaudeCode:Mode must be env, settings, or isolated-settings.");
        }
    }

    private string ResolveSettingsPath()
    {
        if (string.IsNullOrWhiteSpace(_options.SettingsPath))
        {
            throw new InvalidOperationException("ClaudeCode:SettingsPath is required for settings modes.");
        }

        var path = ResolvePath(_options.SettingsPath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Configured Claude Code settings file does not exist.", path);
        }

        return path;
    }

    private string ResolvePath(string value) => Path.IsPathRooted(value)
        ? value
        : Path.GetFullPath(value, environment.ContentRootPath);

    private string ResolveExecutablePath(string value)
    {
        if (Path.IsPathRooted(value))
        {
            if (!File.Exists(value))
            {
                throw new FileNotFoundException("Configured Claude Code executable does not exist.", value);
            }

            return value;
        }

        var contentRootCandidate = ResolvePath(value);
        if (File.Exists(contentRootCandidate))
        {
            return contentRootCandidate;
        }

        var pathEntries = Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [];
        foreach (var pathEntry in pathEntries)
        {
            var candidate = Path.Combine(pathEntry, value);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Configured Claude Code executable was not found on PATH.", value);
    }

    private static readonly string[] GatewayEnvironmentVariables =
    [
        "ANTHROPIC_BASE_URL",
        "ANTHROPIC_AUTH_TOKEN",
        "ANTHROPIC_API_KEY",
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "ALL_PROXY",
        "NO_PROXY",
    ];
}

public sealed record ClaudeBridgeRuntimeOptions(
    string? SettingsPath,
    IReadOnlyList<string>? SettingSources,
    bool RemoveInheritedGatewayEnvironment);