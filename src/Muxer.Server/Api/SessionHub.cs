using Microsoft.AspNetCore.SignalR;
using Muxer.Shared;

namespace Muxer.Server.Api;

public interface ISessionClient
{
    Task ApprovalRequired(ApprovalRequestDto request);
    Task ApprovalResolved(string sessionId);
    Task SessionCreated(SessionDto session);
    Task SessionDestroyed(string sessionId);
    Task SessionStatusChanged(SessionDto session);
    Task RelayDecision(string sessionId, string behavior);
}

public class SessionHub : Hub<ISessionClient>
{
    private readonly ILogger<SessionHub> _logger;

    public SessionHub(ILogger<SessionHub> logger)
    {
        _logger = logger;
    }

    public override Task OnConnectedAsync()
    {
        _logger.LogInformation("Client connected: {Id}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client disconnected: {Id}", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
