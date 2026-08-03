using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeCliHarness.Models;
using Microsoft.Extensions.Options;

namespace ClaudeCodeCliHarness.Services;

public sealed class AgentBridgeClient(
    ClaudeCommandFactory commandFactory,
    IOptions<ClaudeCodeOptions> options,
    IHostEnvironment environment,
    ILogger<AgentBridgeClient> logger) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions BridgeJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ClaudeCodeOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pendingCommands = new();
    private readonly ConcurrentDictionary<string, Func<AgentBridgeMessage, Task>> _sessionHandlers = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private Process? _process;
    private StreamWriter? _standardInput;

    public async Task StartSessionAsync(
        AgentBridgeStartRequest request,
        Func<AgentBridgeMessage, Task> onMessage,
        CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken);
        if (!_sessionHandlers.TryAdd(request.SessionId, onMessage))
        {
            throw new InvalidOperationException($"An Agent Bridge handler already exists for session '{request.SessionId}'.");
        }

        try
        {
            var runtimeOptions = commandFactory.GetBridgeRuntimeOptions();
            var command = new JsonObject
            {
                ["type"] = "start",
                ["sessionId"] = request.SessionId,
                ["cwd"] = request.WorkspacePath,
                ["executablePath"] = commandFactory.ResolveClaudeExecutablePath(),
                ["permissionMode"] = request.PermissionMode
            };

            if (!string.IsNullOrWhiteSpace(request.Model))
            {
                command["model"] = request.Model;
            }

            if (!string.IsNullOrWhiteSpace(request.ResumeSessionId))
            {
                command["resume"] = request.ResumeSessionId;
            }

            if (request.MaxTurns is > 0)
            {
                command["maxTurns"] = request.MaxTurns;
            }

            if (request.MaxThinkingTokens is > 0)
            {
                command["maxThinkingTokens"] = request.MaxThinkingTokens;
            }

            if (!string.IsNullOrWhiteSpace(runtimeOptions.SettingsPath))
            {
                command["settingsPath"] = runtimeOptions.SettingsPath;
            }

            if (runtimeOptions.SettingSources is not null)
            {
                command["settingSources"] = JsonSerializer.SerializeToNode(runtimeOptions.SettingSources);
            }

            await SendCommandAsync(command, cancellationToken);
        }
        catch
        {
            _sessionHandlers.TryRemove(request.SessionId, out _);
            throw;
        }
    }

    public Task QueuePromptAsync(
        string sessionId,
        AgentPromptRequest request,
        CancellationToken cancellationToken)
    {
        var command = new JsonObject
        {
            ["type"] = "prompt",
            ["sessionId"] = sessionId,
            ["message"] = request.Message
        };
        if (request.Attachments is { Count: > 0 })
        {
            command["attachments"] = JsonSerializer.SerializeToNode(request.Attachments, BridgeJsonOptions);
        }

        return SendCommandAsync(command, cancellationToken);
    }

    public Task InterruptSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        SendCommandAsync(new JsonObject
        {
            ["type"] = "interrupt",
            ["sessionId"] = sessionId
        }, cancellationToken);

    public Task ConfigureSessionAsync(
        string sessionId,
        AgentSessionSettingsRequest settings,
        CancellationToken cancellationToken) =>
        SendCommandAsync(new JsonObject
        {
            ["type"] = "configure",
            ["sessionId"] = sessionId,
            ["model"] = settings.Model,
            ["maxThinkingTokens"] = settings.MaxThinkingTokens
        }, cancellationToken);

    public Task RespondToRequestAsync(
        string sessionId,
        AgentPermissionResponse response,
        CancellationToken cancellationToken)
    {
        var command = new JsonObject
        {
            ["type"] = "respond",
            ["sessionId"] = sessionId,
            ["requestId"] = response.RequestId,
            ["decision"] = response.Decision,
            ["remember"] = response.Remember,
            ["interrupt"] = response.Interrupt,
            ["response"] = response.Response,
            ["message"] = response.Message
        };
        if (response.UpdatedInput is { } updatedInput)
        {
            command["updatedInput"] = JsonNode.Parse(updatedInput.GetRawText());
        }

        if (response.Answers is { } answers)
        {
            command["answers"] = JsonNode.Parse(answers.GetRawText());
        }

        return SendCommandAsync(command, cancellationToken);
    }

    public async Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            await SendCommandAsync(new JsonObject
            {
                ["type"] = "close",
                ["sessionId"] = sessionId
            }, cancellationToken);
        }
        finally
        {
            _sessionHandlers.TryRemove(sessionId, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            try
            {
                await SendCommandAsync(new JsonObject { ["type"] = "shutdown" }, CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Agent Bridge did not accept graceful shutdown.");
            }
        }

        if (_process is { HasExited: false } process)
        {
            process.Kill(entireProcessTree: true);
        }

        _standardInput?.Dispose();
        _standardInput = null;
        _process?.Dispose();
        _process = null;
        _startGate.Dispose();
        _writeGate.Dispose();
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false })
        {
            return;
        }

        await _startGate.WaitAsync(cancellationToken);
        try
        {
            if (_process is { HasExited: false })
            {
                return;
            }

            _standardInput?.Dispose();
            _process?.Dispose();
            var bridgePath = commandFactory.ResolveAgentBridgePath();
            var startInfo = new ProcessStartInfo
            {
                FileName = _options.NodeExecutablePath,
                WorkingDirectory = environment.ContentRootPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(bridgePath);
            commandFactory.ConfigureAgentBridgeEnvironment(startInfo);

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("The Agent Bridge process did not start.");
            }

            _process = process;
            _standardInput = process.StandardInput;
            _ = Task.Run(() => ReadStandardOutputAsync(process));
            _ = Task.Run(() => ReadStandardErrorAsync(process));
            process.Exited += (_, _) => FailPendingCommands("The Agent Bridge process exited.");
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task<JsonElement> SendCommandAsync(JsonObject command, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken);
        var commandId = Guid.NewGuid().ToString("N");
        command["commandId"] = commandId;
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingCommands.TryAdd(commandId, completion))
        {
            throw new InvalidOperationException("Unable to register Agent Bridge command.");
        }

        try
        {
            await _writeGate.WaitAsync(cancellationToken);
            try
            {
                if (_standardInput is null)
                {
                    throw new InvalidOperationException("The Agent Bridge process is not accepting input.");
                }

                await _standardInput.WriteLineAsync(command.ToJsonString());
                await _standardInput.FlushAsync(cancellationToken);
            }
            finally
            {
                _writeGate.Release();
            }

            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            _pendingCommands.TryRemove(commandId, out _);
        }
    }

    private async Task ReadStandardOutputAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                HandleBridgeOutput(line);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Agent Bridge standard output reader failed.");
        }
        finally
        {
            FailPendingCommands("The Agent Bridge output stream closed.");
        }
    }

    private async Task ReadStandardErrorAsync(Process process)
    {
        while (await process.StandardError.ReadLineAsync() is { } line)
        {
            logger.LogWarning("Agent Bridge: {BridgeError}", line);
        }
    }

    private void HandleBridgeOutput(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var payload = document.RootElement.Clone();
            var type = GetString(payload, "type") ?? "event";
            var commandId = GetString(payload, "commandId");
            if (!string.IsNullOrWhiteSpace(commandId) && _pendingCommands.TryGetValue(commandId, out var completion))
            {
                if (type == "command-error")
                {
                    completion.TrySetException(new InvalidOperationException(GetString(payload, "error") ?? "The Agent Bridge rejected the command."));
                }
                else
                {
                    completion.TrySetResult(payload);
                }
            }

            var sessionId = GetString(payload, "sessionId");
            if (!string.IsNullOrWhiteSpace(sessionId) && _sessionHandlers.TryGetValue(sessionId, out var handler))
            {
                _ = handler(new AgentBridgeMessage(type, sessionId, commandId, payload));
            }
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Ignoring malformed Agent Bridge output: {BridgeOutput}", line);
        }
    }

    private void FailPendingCommands(string message)
    {
        foreach (var completion in _pendingCommands.Values)
        {
            completion.TrySetException(new InvalidOperationException(message));
        }
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}

public sealed record AgentBridgeStartRequest(
    string SessionId,
    string WorkspacePath,
    string PermissionMode,
    string? Model = null,
    int? MaxTurns = null,
    string? ResumeSessionId = null,
    int? MaxThinkingTokens = null);

public sealed record AgentBridgeMessage(
    string Type,
    string SessionId,
    string? CommandId,
    JsonElement Payload);