using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Muxer.Server.Api;
using Muxer.Server.Psmux;
using Muxer.Server.Sessions;
using Muxer.Shared;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddSignalR();
builder.Services.AddSingleton<PsmuxClient>();
builder.Services.AddSingleton<SessionManager>();

var app = builder.Build();

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
    var output = await psmux.CapturePaneAsync(session.PsmuxSessionName);
    return Results.Text(output);
});

// --- Approval via hooks ---

app.MapPost("/api/sessions/{id}/approve", async (string id, ApproveRequest req,
    SessionManager sm) =>
{
    var session = await sm.GetSessionAsync(id);
    if (session is null) return Results.NotFound();

    if (!sm.ResolvePendingApproval(id, req.Behavior))
        return Results.BadRequest("No pending approval for this session");

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

    // Find the matching Muxer session by working directory
    var session = sm.FindSessionByCwd(cwd);
    if (session is null)
    {
        logger.LogWarning("Hook: No session found for cwd {Cwd}, passing through", cwd);
        return Results.Json(new { });
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

app.MapHub<SessionHub>("/hubs/sessions");

// --- Lifecycle ---

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
var sessionManager = app.Services.GetRequiredService<SessionManager>();

lifetime.ApplicationStopping.Register(() =>
{
    sessionManager.DestroyAllAsync().GetAwaiter().GetResult();
});

app.Run("http://0.0.0.0:5199");

record CreateSessionRequest(string ProjectDir);
record ApproveRequest(string Behavior);
