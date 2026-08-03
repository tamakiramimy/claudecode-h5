using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using ClaudeCodeCliHarness.Models;
using Microsoft.Extensions.Options;

namespace ClaudeCodeCliHarness.Services;

public sealed class AgentSessionManager(
    AgentBridgeClient bridgeClient,
    AgentWorkspaceCatalog workspaces,
    AgentWorktreeService worktrees,
    IOptions<ClaudeCodeOptions> options,
    ILogger<AgentSessionManager> logger)
{
    private const int DefaultThinkingTokens = 8_192;
    private static readonly HashSet<string> AllowedPermissionModes = ["default", "acceptEdits", "auto", "plan"];
    private static readonly HashSet<int> AllowedThinkingTokenBudgets = [4_096, 8_192, 16_384];
    private static readonly HashSet<string> AllowedImageMediaTypes = ["image/gif", "image/jpeg", "image/png", "image/webp"];
    private readonly ClaudeCodeOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, ManagedAgentSession> _sessions = new();

    public IReadOnlyList<AgentWorkspaceSummary> ListWorkspaces() => workspaces.List();

    public IReadOnlyList<AgentSessionSummary> ListSessions(string browserSessionId)
    {
        return _sessions.Values
            .Where(session => session.OwnerBrowserSessionId == browserSessionId)
            .Select(session => session.Summary)
            .OrderByDescending(summary => summary.UpdatedAt)
            .ToArray();
    }

    public async Task<AgentSessionSummary> CreateAsync(
        string browserSessionId,
        AgentSessionCreateRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.WorkspaceId))
        {
            throw new ArgumentException("WorkspaceId is required.", nameof(request));
        }

        if (GetActiveSessionCount(browserSessionId) >= Math.Clamp(_options.MaxConcurrentSessions, 1, 16))
        {
            throw new InvalidOperationException("The configured concurrent Agent session limit has been reached.");
        }

        var workspace = workspaces.GetRequired(request.WorkspaceId);
        var permissionMode = NormalizePermissionMode(request.PermissionMode);
        EnsureNonGitWorkspaceIsAvailable(browserSessionId, workspace.Summary.Id, workspace.Summary.IsGitRepository);

        var sessionId = Guid.NewGuid().ToString("N");
        var allocation = await worktrees.AllocateAsync(
            workspace.Path,
            sessionId,
            _options.EnableWorktreeIsolation,
            cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var summary = new AgentSessionSummary(
            sessionId,
            NormalizeName(request.Name, workspace.Summary.Name),
            workspace.Summary.Id,
            workspace.Summary.Name,
            "starting",
            permissionMode,
            now,
            now,
            allocation.IsIsolated,
            allocation.WorktreePath,
            null);
        var managedSession = new ManagedAgentSession(browserSessionId, summary, allocation.WorkingDirectory);
        if (!_sessions.TryAdd(sessionId, managedSession))
        {
            throw new InvalidOperationException("Unable to create the Agent session.");
        }

        try
        {
            await bridgeClient.StartSessionAsync(
                new AgentBridgeStartRequest(
                    SessionId: sessionId,
                    WorkspacePath: allocation.WorkingDirectory,
                    PermissionMode: permissionMode,
                    MaxThinkingTokens: DefaultThinkingTokens),
                message => HandleBridgeMessageAsync(managedSession, message),
                cancellationToken);
            Publish(managedSession, "session-created", JsonSerializer.SerializeToElement(summary));
            return managedSession.Summary;
        }
        catch (Exception exception)
        {
            _sessions.TryRemove(sessionId, out _);
            logger.LogWarning(exception, "Unable to start Agent session {SessionId} for workspace {WorkspaceId}", sessionId, workspace.Summary.Id);
            throw;
        }
    }

    public async Task QueuePromptAsync(
        string browserSessionId,
        string sessionId,
        AgentPromptRequest request,
        CancellationToken cancellationToken)
    {
        var session = GetOwnedSession(browserSessionId, sessionId);
        ValidatePrompt(request);
        if (session.Summary.Status is "stopped" or "completed" or "failed")
        {
            await ResumeAsync(session, cancellationToken);
        }

        UpdateSummary(session, summary => summary with { Status = "working" });
        Publish(session, "prompt-submitted", JsonSerializer.SerializeToElement(new
        {
            hasText = !string.IsNullOrWhiteSpace(request.Message),
            attachmentCount = request.Attachments?.Count ?? 0
        }));
        await bridgeClient.QueuePromptAsync(sessionId, request, cancellationToken);
    }

    private async Task ResumeAsync(ManagedAgentSession session, CancellationToken cancellationToken)
    {
        var claudeSessionId = session.Summary.ClaudeSessionId;
        if (string.IsNullOrWhiteSpace(claudeSessionId))
        {
            throw new InvalidOperationException("This session cannot be resumed because Claude did not provide a session ID. Create a new session instead.");
        }

        await bridgeClient.CloseSessionAsync(session.Summary.Id, cancellationToken);
        UpdateSummary(session, summary => summary with { Status = "starting" });
        await bridgeClient.StartSessionAsync(
            new AgentBridgeStartRequest(
                SessionId: session.Summary.Id,
                WorkspacePath: session.WorkingDirectory,
                PermissionMode: session.Summary.PermissionMode,
                ResumeSessionId: claudeSessionId,
                Model: session.Model,
                MaxThinkingTokens: session.MaxThinkingTokens),
            message => HandleBridgeMessageAsync(session, message),
            cancellationToken);
        Publish(session, "session-resumed", JsonSerializer.SerializeToElement(new { claudeSessionId }));
    }

    public async Task InterruptAsync(string browserSessionId, string sessionId, CancellationToken cancellationToken)
    {
        var session = GetOwnedSession(browserSessionId, sessionId);
        UpdateSummary(session, summary => summary with { Status = "stopping" });
        await bridgeClient.InterruptSessionAsync(sessionId, cancellationToken);
    }

    public async Task ConfigureAsync(
        string browserSessionId,
        string sessionId,
        AgentSessionSettingsRequest settings,
        CancellationToken cancellationToken)
    {
        var session = GetOwnedSession(browserSessionId, sessionId);
        if (session.Summary.Status != "idle")
        {
            throw new InvalidOperationException("Model and thinking level can be changed only while the session is idle.");
        }

        var model = NormalizeModel(settings.Model);
        var thinkingTokens = NormalizeThinkingTokens(settings.MaxThinkingTokens);
        await bridgeClient.ConfigureSessionAsync(
            sessionId,
            new AgentSessionSettingsRequest(model, thinkingTokens),
            cancellationToken);

        lock (session.SyncRoot)
        {
            session.Model = model ?? session.Model;
            session.MaxThinkingTokens = thinkingTokens ?? session.MaxThinkingTokens;
        }
        Publish(session, "session-settings-updated", JsonSerializer.SerializeToElement(new
        {
            model = session.Model,
            maxThinkingTokens = session.MaxThinkingTokens
        }));
    }

    public async Task RespondAsync(
        string browserSessionId,
        string sessionId,
        AgentPermissionResponse response,
        CancellationToken cancellationToken)
    {
        var session = GetOwnedSession(browserSessionId, sessionId);
        if (string.IsNullOrWhiteSpace(response.RequestId))
        {
            throw new ArgumentException("RequestId is required.", nameof(response));
        }

        if (!string.Equals(response.Decision, "allow", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(response.Decision, "deny", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Decision must be allow or deny.", nameof(response));
        }

        UpdateSummary(session, summary => summary with { Status = "working" });
        await bridgeClient.RespondToRequestAsync(sessionId, response, cancellationToken);
    }

    public async Task CloseAsync(string browserSessionId, string sessionId, CancellationToken cancellationToken)
    {
        var session = GetOwnedSession(browserSessionId, sessionId);
        await bridgeClient.CloseSessionAsync(sessionId, cancellationToken);
        UpdateSummary(session, summary => summary with { Status = "stopped" });
        Publish(session, "session-stopped", JsonSerializer.SerializeToElement(new { }));
    }

    public async IAsyncEnumerable<AgentSessionEvent> StreamAsync(
        string browserSessionId,
        string sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var session = GetOwnedSession(browserSessionId, sessionId);
        var channel = Channel.CreateUnbounded<AgentSessionEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        var subscriptionId = Guid.NewGuid();

        lock (session.SyncRoot)
        {
            foreach (var historicalEvent in session.Events)
            {
                channel.Writer.TryWrite(historicalEvent);
            }

            session.Subscribers[subscriptionId] = channel.Writer;
        }

        try
        {
            await foreach (var agentEvent in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return agentEvent;
            }
        }
        finally
        {
            lock (session.SyncRoot)
            {
                session.Subscribers.Remove(subscriptionId);
            }

            channel.Writer.TryComplete();
        }
    }

    private Task HandleBridgeMessageAsync(ManagedAgentSession session, AgentBridgeMessage message)
    {
        UpdateStateFromBridgeMessage(session, message);
        Publish(session, message.Type, message.Payload);
        return Task.CompletedTask;
    }

    private void UpdateStateFromBridgeMessage(ManagedAgentSession session, AgentBridgeMessage message)
    {
        switch (message.Type)
        {
            case "permission-request":
            case "question-request":
                UpdateSummary(session, summary => summary with { Status = "needs-input" });
                return;
            case "prompt-queued":
            case "request-resolved":
                UpdateSummary(session, summary => summary with { Status = "working" });
                return;
            case "session-ended":
                UpdateSummary(session, summary => summary with
                {
                    Status = GetString(message.Payload, "reason") == "error" ? "failed" : "completed"
                });
                return;
            case "session-closed":
                UpdateSummary(session, summary => summary.Status == "failed"
                    ? summary
                    : summary with { Status = "stopped" });
                return;
            case "event":
                UpdateStateFromAgentEvent(session, message.Payload);
                return;
        }
    }

    private static readonly HashSet<string> ActivityEventKinds =
    [
        "assistant", "stream_event", "user", "tool_progress",
        "task_started", "task_progress", "task_notification", "api_retry"
    ];

    private void UpdateStateFromAgentEvent(ManagedAgentSession session, JsonElement bridgePayload)
    {
        if (!bridgePayload.TryGetProperty("event", out var eventEnvelope) ||
            !eventEnvelope.TryGetProperty("kind", out var kind) ||
            kind.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var kindValue = kind.GetString() ?? string.Empty;

        if (kindValue == "capabilities")
        {
            UpdateSummary(session, summary => summary.Status == "starting" ? summary with { Status = "idle" } : summary);
            return;
        }

        if (kindValue == "init" && eventEnvelope.TryGetProperty("payload", out var initPayload))
        {
            var claudeSessionId = GetString(initPayload, "session_id");
            UpdateSummary(session, summary =>
            {
                var updated = string.IsNullOrWhiteSpace(claudeSessionId) ? summary : summary with { ClaudeSessionId = claudeSessionId };
                return updated.Status == "starting" ? updated with { Status = "idle" } : updated;
            });
            return;
        }

        if (kindValue == "session_state_changed" && eventEnvelope.TryGetProperty("payload", out var statePayload))
        {
            var state = GetString(statePayload, "state");
            var status = state switch
            {
                "idle" => "idle",
                "requires_action" => "needs-input",
                "running" => "working",
                _ => null
            };
            if (status is not null)
            {
                UpdateSummary(session, summary => summary with { Status = status });
            }

            return;
        }

        if (kindValue == "result" && eventEnvelope.TryGetProperty("payload", out var resultPayload))
        {
            var isError = resultPayload.TryGetProperty("is_error", out var errorValue) &&
                          errorValue.ValueKind == JsonValueKind.True;
            UpdateSummary(session, summary => summary with { Status = isError ? "failed" : "idle" });
            return;
        }

        if (ActivityEventKinds.Contains(kindValue))
        {
            UpdateSummary(session, summary => summary.Status is "idle" or "starting" ? summary with { Status = "working" } : summary);
        }
    }

    private void Publish(ManagedAgentSession session, string type, JsonElement payload)
    {
        AgentSessionEvent agentEvent;
        ChannelWriter<AgentSessionEvent>[] subscribers;
        lock (session.SyncRoot)
        {
            agentEvent = new AgentSessionEvent(++session.NextSequence, type, payload.Clone(), DateTimeOffset.UtcNow);
            session.Events.Add(agentEvent);
            if (session.Events.Count > 500)
            {
                session.Events.RemoveRange(0, session.Events.Count - 500);
            }

            subscribers = session.Subscribers.Values.ToArray();
        }

        foreach (var subscriber in subscribers)
        {
            subscriber.TryWrite(agentEvent);
        }
    }

    private ManagedAgentSession GetOwnedSession(string browserSessionId, string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session) || session.OwnerBrowserSessionId != browserSessionId)
        {
            throw new KeyNotFoundException("The requested Agent session was not found.");
        }

        return session;
    }

    private int GetActiveSessionCount(string browserSessionId)
    {
        return _sessions.Values.Count(session =>
            session.OwnerBrowserSessionId == browserSessionId &&
            session.Summary.Status is not ("completed" or "failed" or "stopped"));
    }

    private void EnsureNonGitWorkspaceIsAvailable(string browserSessionId, string workspaceId, bool isGitRepository)
    {
        if (isGitRepository && _options.EnableWorktreeIsolation)
        {
            return;
        }

        var existingWriter = _sessions.Values.Any(session =>
            session.OwnerBrowserSessionId == browserSessionId &&
            session.Summary.WorkspaceId == workspaceId &&
            session.Summary.Status is not ("completed" or "failed" or "stopped"));
        if (existingWriter)
        {
            throw new InvalidOperationException("A non-isolated workspace supports only one active Agent session at a time.");
        }
    }

    private void UpdateSummary(ManagedAgentSession session, Func<AgentSessionSummary, AgentSessionSummary> update)
    {
        lock (session.SyncRoot)
        {
            session.Summary = update(session.Summary) with { UpdatedAt = DateTimeOffset.UtcNow };
        }
    }

    private void ValidatePrompt(AgentPromptRequest request)
    {
        var message = request.Message?.Trim() ?? string.Empty;
        var attachments = request.Attachments ?? [];
        if (message.Length == 0 && attachments.Count == 0)
        {
            throw new ArgumentException("A prompt needs text, an attachment, or both.", nameof(request));
        }

        if (message.Length > 32_000)
        {
            throw new ArgumentException("Prompt text must contain at most 32000 characters.", nameof(request));
        }

        if (attachments.Count > Math.Clamp(_options.MaxAttachmentsPerMessage, 1, 10))
        {
            throw new ArgumentException("The prompt contains too many attachments.", nameof(request));
        }

        foreach (var attachment in attachments)
        {
            if (!AllowedImageMediaTypes.Contains(attachment.MediaType) || string.IsNullOrWhiteSpace(attachment.Data))
            {
                throw new ArgumentException("Only PNG, JPEG, GIF, and WebP image attachments are supported.", nameof(request));
            }

            try
            {
                var bytes = Convert.FromBase64String(attachment.Data);
                if (bytes.Length > Math.Clamp(_options.MaxAttachmentBytes, 1, 50 * 1024 * 1024))
                {
                    throw new ArgumentException("An attachment exceeds the configured size limit.", nameof(request));
                }
            }
            catch (FormatException exception)
            {
                throw new ArgumentException("Attachment data must be valid base64.", nameof(request), exception);
            }
        }
    }

    private static string NormalizePermissionMode(string? permissionMode)
    {
        var normalized = string.IsNullOrWhiteSpace(permissionMode) ? "default" : permissionMode.Trim();
        if (!AllowedPermissionModes.Contains(normalized))
        {
            throw new ArgumentException("Permission mode must be default, acceptEdits, auto, or plan.", nameof(permissionMode));
        }

        return normalized;
    }

    private static string? NormalizeModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var normalized = model.Trim();
        if (normalized.Length > 128 || !normalized.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.'))
        {
            throw new ArgumentException("Model contains unsupported characters.", nameof(model));
        }

        return normalized;
    }

    private static int? NormalizeThinkingTokens(int? maxThinkingTokens)
    {
        if (maxThinkingTokens is null)
        {
            return null;
        }

        if (!AllowedThinkingTokenBudgets.Contains(maxThinkingTokens.Value))
        {
            throw new ArgumentException("Thinking level is not supported.", nameof(maxThinkingTokens));
        }

        return maxThinkingTokens;
    }

    private static string NormalizeName(string? name, string workspaceName)
    {
        var normalized = name?.Trim();
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            return normalized[..Math.Min(normalized.Length, 120)];
        }

        return $"{workspaceName} {DateTimeOffset.Now:HH:mm}";
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private sealed class ManagedAgentSession(
        string ownerBrowserSessionId,
        AgentSessionSummary summary,
        string workingDirectory)
    {
        public string OwnerBrowserSessionId { get; } = ownerBrowserSessionId;

        public string WorkingDirectory { get; } = workingDirectory;

        public object SyncRoot { get; } = new();

        public List<AgentSessionEvent> Events { get; } = [];

        public Dictionary<Guid, ChannelWriter<AgentSessionEvent>> Subscribers { get; } = [];

        public long NextSequence { get; set; }

        public long InactivityGeneration { get; set; }

        public CancellationTokenSource? InactivityCancellation { get; set; }

        public string? Model { get; set; }

        public int MaxThinkingTokens { get; set; } = DefaultThinkingTokens;

        public AgentSessionSummary Summary { get; set; } = summary;
    }
}