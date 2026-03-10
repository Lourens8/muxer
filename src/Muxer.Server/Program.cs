using Microsoft.AspNetCore.SignalR;
using Muxer.Server.Api;
using Muxer.Server.Psmux;
using Muxer.Server.Sessions;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddSignalR();
builder.Services.AddSingleton<PsmuxClient>();
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddHostedService<SessionMonitor>();

var app = builder.Build();

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

app.MapPost("/api/sessions/{id}/approve", async (string id, ApproveRequest req,
    SessionManager sm, PsmuxClient psmux, IHubContext<SessionHub, ISessionClient> hub) =>
{
    var session = await sm.GetSessionAsync(id);
    if (session is null) return Results.NotFound();

    await psmux.SendKeysAsync(session.PsmuxSessionName, req.Option.ToString());

    session.Status = Muxer.Shared.SessionStatus.Running;
    session.ApprovalContext = null;
    session.ApprovalOptions = null;
    session.ApprovalDetectedAt = null;

    await hub.Clients.All.ApprovalResolved(id);
    return Results.Ok();
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
record ApproveRequest(int Option);
