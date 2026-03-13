using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Muxer.Server.Api;
using Muxer.Server.Psmux;
using Muxer.Server.Relay;
using Muxer.Server.Sessions;
using Muxer.Shared;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddSignalR();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.SetIsOriginAllowed(_ => true)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials());
});
builder.Services.AddSingleton<PsmuxClient>();
builder.Services.AddSingleton<SessionManager>();

// Relay: if configured, this server forwards approvals to a primary server
var relayUrl = builder.Configuration["Muxer:RelayUrl"];
if (!string.IsNullOrEmpty(relayUrl))
{
    builder.Services.AddSingleton(sp =>
        new RelayClient(relayUrl, sp.GetRequiredService<ILogger<RelayClient>>()));
}

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

// --- REST API ---

app.MapGet("/api/projects", (SessionManager sm) => sm.GetProjectDirectories());

app.MapGet("/api/sessions", async (SessionManager sm) =>
    (await sm.GetSessionsAsync()).Select(s => s.ToDto()));

app.MapPost("/api/sessions", async (CreateSessionRequest req, SessionManager sm,
    IHubContext<SessionHub, ISessionClient> hub) =>
{
    var session = await sm.CreateSessionAsync(req.ProjectDir);
    await hub.Clients.All.SessionCreated(session.ToDto());
    return Results.Ok(session.ToDto());
});

app.MapGet("/api/sessions/{id}", async (string id, SessionManager sm) =>
{
    var session = await sm.GetSessionAsync(id);
    return session is null ? Results.NotFound() : Results.Ok(session.ToDto());
});

app.MapDelete("/api/sessions/{id}", async (string id, SessionManager sm,
    IHubContext<SessionHub, ISessionClient> hub) =>
{
    await sm.DestroySessionAsync(id);
    await hub.Clients.All.SessionDestroyed(id);
    return Results.Ok();
});

app.MapGet("/api/sessions/{id}/screen", async (string id, SessionManager sm, PsmuxClient psmux) =>
{
    var session = await sm.GetSessionAsync(id);
    if (session is null) return Results.NotFound();
    if (session.IsAdHoc) return Results.Text("(ad-hoc session — no terminal attached)");
    var output = await psmux.CapturePaneAsync(session.PsmuxSessionName);
    return Results.Text(output);
});

// --- Approval via hooks ---

app.MapPost("/api/sessions/{id}/approve", async (string id, ApproveRequest req,
    SessionManager sm, IHubContext<SessionHub, ISessionClient> hub) =>
{
    var session = await sm.GetSessionAsync(id);
    if (session is null) return Results.NotFound();
    if (session.Status != SessionStatus.WaitingForApproval)
        return Results.BadRequest("No pending approval for this session");

    // Resolve local TCS if present (hook-based flow handles its own cleanup)
    var resolvedLocally = sm.ResolvePendingApproval(id, req.Behavior);

    // Broadcast for relay clients listening on SignalR
    await hub.Clients.All.RelayDecision(id, req.Behavior);

    // For relay sessions (no local TCS), clean up state directly
    if (!resolvedLocally)
    {
        session.Status = SessionStatus.Running;
        session.PendingToolName = null;
        session.PendingToolInput = null;
        session.ApprovalRequestedAt = null;
        await hub.Clients.All.ApprovalResolved(id);
    }

    return Results.Ok();
});

app.MapPost("/api/hooks/permission-request", async (HttpContext ctx, SessionManager sm,
    IHubContext<SessionHub, ISessionClient> hub, ILogger<Program> logger) =>
{
    // Parse the Claude Code hook payload
    var body = await JsonDocument.ParseAsync(ctx.Request.Body);
    var root = body.RootElement;

    var cwd = root.GetProperty("cwd").GetString()!;
    var toolName = root.GetProperty("tool_name").GetString()!;
    var toolInput = root.GetProperty("tool_input").ToString();

    logger.LogWarning("Hook: PermissionRequest for {Tool} in {Cwd}", toolName, cwd);

    // Only handle known tool permissions; pass through anything else (e.g. elicitation/questions)
    var knownTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Bash", "Edit", "Write", "Read", "Glob", "Grep",
        "WebFetch", "WebSearch", "NotebookEdit", "Agent", "Skill", "ToolSearch",
        "TaskCreate", "TaskGet", "TaskList", "TaskOutput", "TaskStop", "TaskUpdate"
    };
    if (!knownTools.Contains(toolName) && !toolName.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase))
    {
        logger.LogWarning("Hook: Unknown tool {Tool}, passing through (likely elicitation)", toolName);
        return Results.Json(new { });
    }

    // If relay is configured, forward all approvals to the primary server
    var relay = ctx.RequestServices.GetService<RelayClient>();
    if (relay is not null)
    {
        var localSession = sm.FindSessionByCwd(cwd);
        var projectName = localSession?.ProjectName ?? new DirectoryInfo(cwd).Name;

        if (localSession is not null)
        {
            localSession.Status = SessionStatus.WaitingForApproval;
            localSession.PendingToolName = toolName;
            localSession.PendingToolInput = toolInput;
            localSession.ApprovalRequestedAt = DateTimeOffset.UtcNow;
        }

        string relayBehavior;
        try
        {
            relayBehavior = await relay.ForwardApprovalAsync(projectName, cwd, toolName, toolInput, ctx.RequestAborted);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            logger.LogWarning("Relay: Timed out or cancelled for {Tool} in {Cwd}", toolName, cwd);
            if (localSession is not null)
            {
                localSession.Status = SessionStatus.Running;
                localSession.PendingToolName = null;
                localSession.PendingToolInput = null;
                localSession.ApprovalRequestedAt = null;
            }
            return Results.Json(new { });
        }

        if (localSession is not null)
        {
            localSession.Status = SessionStatus.Running;
            localSession.PendingToolName = null;
            localSession.PendingToolInput = null;
            localSession.ApprovalRequestedAt = null;
        }

        logger.LogWarning("Relay: Returning {Behavior} for {Tool} in {Cwd}", relayBehavior, toolName, cwd);
        return Results.Json(new
        {
            hookSpecificOutput = new
            {
                hookEventName = "PermissionRequest",
                decision = new { behavior = relayBehavior }
            }
        });
    }

    // Find the matching Muxer session by working directory, or create an ad-hoc one
    var session = sm.FindSessionByCwd(cwd);
    if (session is null)
    {
        session = sm.FindOrCreateAdHocSession(cwd);
        await hub.Clients.All.SessionCreated(session.ToDto());
    }

    // Update session state
    session.Status = SessionStatus.WaitingForApproval;
    session.PendingToolName = toolName;
    session.PendingToolInput = toolInput;
    session.ApprovalRequestedAt = DateTimeOffset.UtcNow;

    // Broadcast to all connected dashboards
    await hub.Clients.All.ApprovalRequired(new ApprovalRequestDto(
        session.Id, session.ProjectName, toolName, toolInput, session.ApprovalRequestedAt.Value));

    // Wait for user decision (long-poll)
    var tcs = sm.RegisterPendingApproval(session.Id);
    string behavior;
    try
    {
        behavior = await tcs.Task.WaitAsync(TimeSpan.FromMinutes(30), ctx.RequestAborted);
    }
    catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
    {
        logger.LogWarning("Hook: Approval timed out or cancelled for session {Id}", session.Id);
        sm.ResolvePendingApproval(session.Id, "deny");
        session.Status = SessionStatus.Running;
        session.PendingToolName = null;
        session.PendingToolInput = null;
        session.ApprovalRequestedAt = null;
        // Return empty to let Claude Code handle normally
        return Results.Json(new { });
    }

    // Clear approval state
    session.Status = SessionStatus.Running;
    session.PendingToolName = null;
    session.PendingToolInput = null;
    session.ApprovalRequestedAt = null;

    await hub.Clients.All.ApprovalResolved(session.Id);

    logger.LogWarning("Hook: Returning {Behavior} for {Tool} in session {Id}",
        behavior, toolName, session.Id);

    // Return decision to Claude Code
    return Results.Json(new
    {
        hookSpecificOutput = new
        {
            hookEventName = "PermissionRequest",
            decision = new
            {
                behavior
            }
        }
    });
});

// --- Relay: incoming from remote servers ---

app.MapPost("/api/relay/approval-request", async (RelayApprovalRequest req, SessionManager sm,
    IHubContext<SessionHub, ISessionClient> hub, ILogger<Program> logger) =>
{
    logger.LogWarning("Relay: Incoming from {Server} — {Tool} in {Project}",
        req.ServerName, req.ToolName, req.ProjectName);

    var session = sm.FindOrCreateRelaySession(req.ServerName, req.ProjectName, req.ProjectDir);

    session.Status = SessionStatus.WaitingForApproval;
    session.PendingToolName = req.ToolName;
    session.PendingToolInput = req.ToolInput;
    session.ApprovalRequestedAt = DateTimeOffset.UtcNow;

    await hub.Clients.All.SessionCreated(session.ToDto());
    await hub.Clients.All.ApprovalRequired(new ApprovalRequestDto(
        session.Id, session.ProjectName, req.ToolName, req.ToolInput, session.ApprovalRequestedAt.Value));

    return Results.Json(new { sessionId = session.Id });
});

app.MapHub<SessionHub>("/hubs/sessions");

// --- Lifecycle ---

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
var sessionManager = app.Services.GetRequiredService<SessionManager>();
var relayClient = app.Services.GetService<RelayClient>();

// Connect relay client to primary if configured
if (relayClient is not null)
    await relayClient.ConnectAsync();

// Periodically clean up stale ad-hoc sessions (no activity for 30 minutes)
var cleanupTimer = new Timer(_ => sessionManager.CleanupStaleAdHocSessions(TimeSpan.FromMinutes(30)),
    null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

lifetime.ApplicationStopping.Register(() =>
{
    cleanupTimer.Dispose();
    relayClient?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    sessionManager.DestroyAllAsync().GetAwaiter().GetResult();
});

app.Run("http://0.0.0.0:5199");

record CreateSessionRequest(string ProjectDir);
record ApproveRequest(string Behavior);
record RelayApprovalRequest(string ServerName, string ProjectName, string ProjectDir, string ToolName, string ToolInput);
