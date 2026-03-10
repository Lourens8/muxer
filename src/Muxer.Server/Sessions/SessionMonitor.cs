using Microsoft.AspNetCore.SignalR;
using Muxer.Server.Api;
using Muxer.Server.Psmux;
using Muxer.Shared;

namespace Muxer.Server.Sessions;

public class SessionMonitor : BackgroundService
{
    private readonly SessionManager _sessions;
    private readonly PsmuxClient _psmux;
    private readonly IHubContext<SessionHub, ISessionClient> _hub;
    private readonly ILogger<SessionMonitor> _logger;

    public SessionMonitor(
        SessionManager sessions,
        PsmuxClient psmux,
        IHubContext<SessionHub, ISessionClient> hub,
        ILogger<SessionMonitor> logger)
    {
        _sessions = sessions;
        _psmux = psmux;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Session monitor started");

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var session in await _sessions.GetSessionsAsync())
            {
                if (session.Status == SessionStatus.Stopped) continue;

                try
                {
                    var output = await _psmux.CapturePaneAsync(session.PsmuxSessionName);
                    var approval = ApprovalDetector.Detect(output);

                    if (approval != null && session.Status != SessionStatus.WaitingForApproval)
                    {
                        // Transition to waiting
                        session.Status = SessionStatus.WaitingForApproval;
                        session.ApprovalContext = approval.Context;
                        session.ApprovalOptions = approval.Options;
                        session.ApprovalDetectedAt = DateTimeOffset.UtcNow;

                        _logger.LogInformation("Approval required in session {Id}: {Context}",
                            session.Id, approval.Context[..Math.Min(80, approval.Context.Length)]);

                        await _hub.Clients.All.ApprovalRequired(new ApprovalRequestDto(
                            session.Id,
                            session.ProjectName,
                            approval.Context,
                            approval.Options,
                            session.ApprovalDetectedAt.Value
                        ));
                    }
                    else if (approval == null && session.Status == SessionStatus.WaitingForApproval)
                    {
                        // Resolved
                        session.Status = SessionStatus.Running;
                        session.ApprovalContext = null;
                        session.ApprovalOptions = null;
                        session.ApprovalDetectedAt = null;

                        _logger.LogInformation("Approval resolved in session {Id}", session.Id);
                        await _hub.Clients.All.ApprovalResolved(session.Id);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error monitoring session {Id}", session.Id);
                }
            }

            await Task.Delay(1000, stoppingToken);
        }
    }
}
