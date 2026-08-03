using System.Diagnostics;
using System.Text.Json;

namespace ClaudeCodeCliHarness.Services;

public sealed class ClaudeProcessRunner(ClaudeCommandFactory commandFactory)
{
    public async Task<ClaudeRunResult> RunAsync(
        ProcessStartInfo startInfo,
        Func<ClaudeCliEvent, CancellationToken, Task> onEvent,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Claude Code process did not start.");
        }

        using var timeout = new CancellationTokenSource(commandFactory.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(linked.Token);

        try
        {
            while (await process.StandardOutput.ReadLineAsync(linked.Token) is { } line)
            {
                await onEvent(Parse(line), linked.Token);
            }

            await process.WaitForExitAsync(linked.Token);
            var standardError = await errorTask;
            if (!string.IsNullOrWhiteSpace(standardError))
            {
                await onEvent(new ClaudeCliEvent("stderr", standardError, null, null, null, null, true), linked.Token);
            }

            return new ClaudeRunResult(process.ExitCode);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            KillProcessTree(process);
            throw new TimeoutException($"Claude Code exceeded the {commandFactory.Timeout.TotalMinutes:N0}-minute timeout.");
        }
        catch
        {
            KillProcessTree(process);
            throw;
        }
    }

    private static ClaudeCliEvent Parse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var sessionId = GetString(root, "session_id");
            var kind = GetString(root, "type") ?? "event";
            string? text = null;
            string? toolName = null;
            string? toolDetail = null;

            if (kind == "stream_event" && root.TryGetProperty("event", out var streamEvent))
            {
                var streamType = GetString(streamEvent, "type");
                if (streamType == "content_block_delta" && streamEvent.TryGetProperty("delta", out var delta))
                {
                    text = GetString(delta, "text");
                    if (text is not null)
                    {
                        kind = "delta";
                    }
                }
                else if (streamType == "content_block_start" && streamEvent.TryGetProperty("content_block", out var contentBlock))
                {
                    toolName = GetString(contentBlock, "name");
                    if (!string.IsNullOrWhiteSpace(toolName))
                    {
                        kind = "tool-start";
                    }
                }
            }
            else if (kind == "assistant" && root.TryGetProperty("message", out var message))
            {
                (toolName, toolDetail) = GetToolUse(message);
                if (!string.IsNullOrWhiteSpace(toolName))
                {
                    kind = "tool";
                }
            }
            else if (kind == "user" && root.TryGetProperty("tool_use_result", out var toolResult))
            {
                toolDetail = toolResult.ValueKind == JsonValueKind.Object
                    ? GetToolResultTarget(toolResult)
                    : null;
                kind = "tool-result";
            }

            return new ClaudeCliEvent(kind, line, sessionId, text, toolName, toolDetail, false);
        }
        catch (JsonException)
        {
            return new ClaudeCliEvent("stdout", line, null, null, null, null, false);
        }
    }

    private static (string? Name, string? Target) GetToolUse(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return (null, null);
        }

        foreach (var block in content.EnumerateArray())
        {
            if (GetString(block, "type") != "tool_use")
            {
                continue;
            }

            var name = GetString(block, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            return (name, block.TryGetProperty("input", out var input) ? GetToolTarget(input) : null);
        }

        return (null, null);
    }

    private static string? GetToolTarget(JsonElement input)
    {
        foreach (var propertyName in new[] { "file_path", "path", "command", "query", "pattern" })
        {
            var value = GetString(input, propertyName);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            return propertyName is "file_path" or "path"
                ? Path.GetFileName(value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : Shorten(value);
        }

        return null;
    }

    private static string? GetToolResultTarget(JsonElement toolResult)
    {
        if (toolResult.TryGetProperty("file", out var file))
        {
            var filePath = GetString(file, "filePath");
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                return Path.GetFileName(filePath);
            }
        }

        return null;
    }

    private static string Shorten(string value) => value.Length <= 90 ? value : $"{value[..87]}...";

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static void KillProcessTree(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
    }
}

public sealed record ClaudeCliEvent(
    string Kind,
    string RawJson,
    string? SessionId,
    string? Text,
    string? ToolName,
    string? ToolDetail,
    bool IsError);

public sealed record ClaudeRunResult(int ExitCode);