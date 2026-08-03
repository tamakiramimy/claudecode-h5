using ClaudeCodeCliHarness.Models;
using ClaudeCodeCliHarness.Services;
using Microsoft.AspNetCore.Antiforgery;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.WebHost.UseUrls("http://127.0.0.1:5080");
builder.Services.AddRazorPages();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services
    .AddOptions<ClaudeCodeOptions>()
    .Bind(builder.Configuration.GetSection(ClaudeCodeOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<ClaudeCommandFactory>();
builder.Services.AddSingleton<ClaudeProcessRunner>();
builder.Services.AddSingleton<ClaudeConversationStore>();
builder.Services.AddSingleton<AgentWorkspaceCatalog>();
builder.Services.AddSingleton<AgentWorktreeService>();
builder.Services.AddSingleton<AgentBridgeClient>();
builder.Services.AddSingleton<AgentSessionManager>();
builder.Services.AddSession(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.IdleTimeout = TimeSpan.FromHours(8);
});

var app = builder.Build();

app.UseExceptionHandler("/Error");
app.UseStaticFiles();
app.UseRouting();
app.UseSession();
app.UseAntiforgery();
app.MapRazorPages();
app.MapPost("/api/chat", StreamChatAsync);
app.MapPost("/api/chat/new", StartNewChatAsync);
app.MapPost("/api/chat/stop", StopChatAsync);
app.MapGet("/api/workspaces", ListWorkspacesAsync);
app.MapGet("/api/agent/sessions", ListAgentSessionsAsync);
app.MapGet("/api/agent/sessions/{sessionId}/events", StreamAgentEventsAsync);
app.MapPost("/api/agent/sessions", CreateAgentSessionAsync);
app.MapPost("/api/agent/sessions/{sessionId}/prompts", QueueAgentPromptAsync);
app.MapPost("/api/agent/sessions/{sessionId}/interrupt", InterruptAgentSessionAsync);
app.MapPost("/api/agent/sessions/{sessionId}/settings", ConfigureAgentSessionAsync);
app.MapPost("/api/agent/sessions/{sessionId}/responses", RespondToAgentRequestAsync);
app.MapPost("/api/agent/sessions/{sessionId}/close", CloseAgentSessionAsync);

app.Run();

static async Task StreamChatAsync(
    HttpContext context,
    ChatRequest request,
    ClaudeConversationStore conversations,
    ClaudeCommandFactory commandFactory,
    ClaudeProcessRunner processRunner,
    CancellationToken requestAborted)
{
    if (string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > 32_000)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = "Message must contain 1 to 32000 characters." }, requestAborted);
        return;
    }

    context.Session.SetString("ClaudeCodeChat", "active");
    var browserSessionId = context.Session.Id;
    if (!conversations.TryBegin(browserSessionId, out var run))
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsJsonAsync(new { error = "A Claude Code task is already running." }, requestAborted);
        return;
    }

    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Append("X-Accel-Buffering", "no");

    try
    {
        var command = commandFactory.Create(
            request.Message.Trim(),
            conversations.GetClaudeSessionId(browserSessionId));
        var result = await processRunner.RunAsync(
            command,
            async (cliEvent, cancellationToken) =>
            {
                if (!string.IsNullOrWhiteSpace(cliEvent.SessionId))
                {
                    conversations.SetClaudeSessionId(browserSessionId, cliEvent.SessionId);
                }

                await WriteSseAsync(context, cliEvent.Kind, cliEvent, cancellationToken);
            },
            run.Token);

        await WriteSseAsync(context, "complete", new { result.ExitCode }, run.Token);
    }
    catch (OperationCanceledException) when (run.Token.IsCancellationRequested)
    {
        await WriteSseAsync(context, "cancelled", new { }, CancellationToken.None);
    }
    catch (Exception exception)
    {
        await WriteSseAsync(context, "error", new { error = exception.Message }, CancellationToken.None);
    }
    finally
    {
        conversations.Complete(browserSessionId, run);
    }
}

static IResult StartNewChatAsync(HttpContext context, ClaudeConversationStore conversations)
{
    conversations.Reset(context.Session.Id);
    return Results.NoContent();
}

static IResult StopChatAsync(HttpContext context, ClaudeConversationStore conversations)
{
    return conversations.Stop(context.Session.Id) ? Results.NoContent() : Results.NotFound();
}

static IResult ListWorkspacesAsync(AgentSessionManager sessions) => Results.Ok(sessions.ListWorkspaces());

static IResult ListAgentSessionsAsync(HttpContext context, AgentSessionManager sessions) =>
    Results.Ok(sessions.ListSessions(context.Session.Id));

static async Task<IResult> CreateAgentSessionAsync(
    HttpContext context,
    AgentSessionCreateRequest request,
    AgentSessionManager sessions,
    IAntiforgery antiforgery,
    CancellationToken cancellationToken)
{
    if (!await IsAntiforgeryValidAsync(context, antiforgery))
    {
        return Results.BadRequest(new { error = "The antiforgery token is missing or invalid." });
    }

    try
    {
        context.Session.SetString("ClaudeCodeChat", "active");
        var session = await sessions.CreateAsync(context.Session.Id, request, cancellationToken);
        return Results.Created($"/api/agent/sessions/{session.Id}", session);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (KeyNotFoundException exception)
    {
        return Results.NotFound(new { error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
}

static async Task<IResult> QueueAgentPromptAsync(
    HttpContext context,
    string sessionId,
    AgentPromptRequest request,
    AgentSessionManager sessions,
    IAntiforgery antiforgery,
    CancellationToken cancellationToken)
{
    if (!await IsAntiforgeryValidAsync(context, antiforgery))
    {
        return Results.BadRequest(new { error = "The antiforgery token is missing or invalid." });
    }

    try
    {
        await sessions.QueuePromptAsync(context.Session.Id, sessionId, request, cancellationToken);
        return Results.Accepted();
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (KeyNotFoundException exception)
    {
        return Results.NotFound(new { error = exception.Message });
    }
}

static async Task<IResult> InterruptAgentSessionAsync(
    HttpContext context,
    string sessionId,
    AgentSessionManager sessions,
    IAntiforgery antiforgery,
    CancellationToken cancellationToken)
{
    if (!await IsAntiforgeryValidAsync(context, antiforgery))
    {
        return Results.BadRequest(new { error = "The antiforgery token is missing or invalid." });
    }

    try
    {
        await sessions.InterruptAsync(context.Session.Id, sessionId, cancellationToken);
        return Results.Accepted();
    }
    catch (KeyNotFoundException exception)
    {
        return Results.NotFound(new { error = exception.Message });
    }
}

static async Task<IResult> ConfigureAgentSessionAsync(
    HttpContext context,
    string sessionId,
    AgentSessionSettingsRequest settings,
    AgentSessionManager sessions,
    IAntiforgery antiforgery,
    CancellationToken cancellationToken)
{
    if (!await IsAntiforgeryValidAsync(context, antiforgery))
    {
        return Results.BadRequest(new { error = "The antiforgery token is missing or invalid." });
    }

    try
    {
        await sessions.ConfigureAsync(context.Session.Id, sessionId, settings, cancellationToken);
        return Results.Accepted();
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
    catch (KeyNotFoundException exception)
    {
        return Results.NotFound(new { error = exception.Message });
    }
}

static async Task<IResult> RespondToAgentRequestAsync(
    HttpContext context,
    string sessionId,
    AgentPermissionResponse response,
    AgentSessionManager sessions,
    IAntiforgery antiforgery,
    CancellationToken cancellationToken)
{
    if (!await IsAntiforgeryValidAsync(context, antiforgery))
    {
        return Results.BadRequest(new { error = "The antiforgery token is missing or invalid." });
    }

    try
    {
        await sessions.RespondAsync(context.Session.Id, sessionId, response, cancellationToken);
        return Results.Accepted();
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (KeyNotFoundException exception)
    {
        return Results.NotFound(new { error = exception.Message });
    }
}

static async Task<IResult> CloseAgentSessionAsync(
    HttpContext context,
    string sessionId,
    AgentSessionManager sessions,
    IAntiforgery antiforgery,
    CancellationToken cancellationToken)
{
    if (!await IsAntiforgeryValidAsync(context, antiforgery))
    {
        return Results.BadRequest(new { error = "The antiforgery token is missing or invalid." });
    }

    try
    {
        await sessions.CloseAsync(context.Session.Id, sessionId, cancellationToken);
        return Results.NoContent();
    }
    catch (KeyNotFoundException exception)
    {
        return Results.NotFound(new { error = exception.Message });
    }
}

static async Task StreamAgentEventsAsync(
    HttpContext context,
    string sessionId,
    AgentSessionManager sessions,
    CancellationToken requestAborted)
{
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Append("X-Accel-Buffering", "no");

    try
    {
        await foreach (var agentEvent in sessions.StreamAsync(context.Session.Id, sessionId, requestAborted))
        {
            await WriteSseAsync(context, agentEvent.Type, agentEvent, requestAborted);
        }
    }
    catch (KeyNotFoundException exception)
    {
        await WriteSseAsync(context, "error", new { error = exception.Message }, CancellationToken.None);
    }
    catch (OperationCanceledException) when (requestAborted.IsCancellationRequested)
    {
    }
}

static async Task<bool> IsAntiforgeryValidAsync(HttpContext context, IAntiforgery antiforgery)
{
    try
    {
        await antiforgery.ValidateRequestAsync(context);
        return true;
    }
    catch (AntiforgeryValidationException)
    {
        return false;
    }
}

static async Task WriteSseAsync(HttpContext context, string eventName, object payload, CancellationToken cancellationToken)
{
    var json = System.Text.Json.JsonSerializer.Serialize(
        payload,
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    await context.Response.WriteAsync($"event: {eventName}\ndata: {json}\n\n", cancellationToken);
    await context.Response.Body.FlushAsync(cancellationToken);
}