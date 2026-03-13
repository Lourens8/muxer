using System.Collections.Concurrent;
using Muxer.Server.Psmux;
using Muxer.Shared;

namespace Muxer.Server.Sessions;

public class ManagedSession
{
    public required string Id { get; init; }
    public required string ProjectName { get; init; }
    public required string ProjectDir { get; init; }
    public required string PsmuxSessionName { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public bool IsAdHoc { get; init; }
    public string? SourceServer { get; init; }
    public SessionStatus Status { get; set; } = SessionStatus.Running;
    public string? PendingToolName { get; set; }
    public string? PendingToolInput { get; set; }
    public DateTimeOffset? ApprovalRequestedAt { get; set; }
    public DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.UtcNow;

    public SessionDto ToDto() => new(
        Id, ProjectName, ProjectDir, PsmuxSessionName, Status,
        PendingToolName, PendingToolInput, StartedAt, ApprovalRequestedAt, IsAdHoc
    );
}

public class SessionManager
{
    private const string ProjectsRoot = @"C:\projects";

    private readonly ConcurrentDictionary<string, ManagedSession> _sessions = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingApprovals = new();
    private readonly PsmuxClient _psmux;
    private readonly ILogger<SessionManager> _logger;

    public SessionManager(PsmuxClient psmux, ILogger<SessionManager> logger)
    {
        _psmux = psmux;
        _logger = logger;
    }

    public async Task<IReadOnlyCollection<ManagedSession>> GetSessionsAsync()
    {
        foreach (var session in _sessions.Values)
            await EnsureAliveAsync(session);
        return _sessions.Values.ToList();
    }

    public async Task<ManagedSession?> GetSessionAsync(string id)
    {
        if (!_sessions.TryGetValue(id, out var s)) return null;
        await EnsureAliveAsync(s);
        return s;
    }

    public ManagedSession? FindSessionByCwd(string cwd)
    {
        var normalized = Path.GetFullPath(cwd);
        return _sessions.Values.FirstOrDefault(s =>
            string.Equals(Path.GetFullPath(s.ProjectDir), normalized, StringComparison.OrdinalIgnoreCase));
    }

    public ManagedSession FindOrCreateAdHocSession(string cwd)
    {
        var normalized = Path.GetFullPath(cwd);

        // Reuse existing ad-hoc session for same directory
        var existing = _sessions.Values.FirstOrDefault(s =>
            s.IsAdHoc && string.Equals(Path.GetFullPath(s.ProjectDir), normalized, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.LastActivityAt = DateTimeOffset.UtcNow;
            return existing;
        }

        var projectName = new DirectoryInfo(normalized).Name;
        var id = Guid.NewGuid().ToString("N")[..8];

        var session = new ManagedSession
        {
            Id = id,
            ProjectName = projectName,
            ProjectDir = normalized,
            PsmuxSessionName = "",
            IsAdHoc = true,
            StartedAt = DateTimeOffset.UtcNow
        };

        _sessions[id] = session;
        _logger.LogInformation("Ad-hoc session {Id} created for {Dir}", id, normalized);
        return session;
    }

    public ManagedSession FindOrCreateRelaySession(string serverName, string projectName, string projectDir)
    {
        var existing = _sessions.Values.FirstOrDefault(s =>
            s.IsAdHoc &&
            string.Equals(s.SourceServer, serverName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(s.ProjectDir, projectDir, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.LastActivityAt = DateTimeOffset.UtcNow;
            return existing;
        }

        var id = Guid.NewGuid().ToString("N")[..8];
        var session = new ManagedSession
        {
            Id = id,
            ProjectName = $"{projectName} ({serverName})",
            ProjectDir = projectDir,
            PsmuxSessionName = "",
            IsAdHoc = true,
            SourceServer = serverName,
            StartedAt = DateTimeOffset.UtcNow
        };

        _sessions[id] = session;
        _logger.LogInformation("Relay session {Id} created for {Project} on {Server}", id, projectName, serverName);
        return session;
    }

    public void CleanupStaleAdHocSessions(TimeSpan maxIdle)
    {
        var cutoff = DateTimeOffset.UtcNow - maxIdle;
        foreach (var (id, session) in _sessions)
        {
            if (session.IsAdHoc && session.Status != SessionStatus.WaitingForApproval
                && session.LastActivityAt < cutoff)
            {
                if (_sessions.TryRemove(id, out _))
                {
                    if (_pendingApprovals.TryRemove(id, out var tcs))
                        tcs.TrySetCanceled();
                    _logger.LogInformation("Cleaned up stale ad-hoc session {Id} ({Project})", id, session.ProjectName);
                }
            }
        }
    }

    public TaskCompletionSource<string> RegisterPendingApproval(string sessionId)
    {
        // Cancel any existing pending approval for this session
        if (_pendingApprovals.TryRemove(sessionId, out var existing))
            existing.TrySetCanceled();

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingApprovals[sessionId] = tcs;
        return tcs;
    }

    public bool ResolvePendingApproval(string sessionId, string behavior)
    {
        if (_pendingApprovals.TryRemove(sessionId, out var tcs))
        {
            tcs.TrySetResult(behavior);
            return true;
        }
        return false;
    }

    private async Task EnsureAliveAsync(ManagedSession session)
    {
        if (session.IsAdHoc) return;

        if (!await _psmux.HasSessionAsync(session.PsmuxSessionName))
        {
            _logger.LogWarning("psmux session {Name} for {Id} ({Project}) is dead, recreating",
                session.PsmuxSessionName, session.Id, session.ProjectName);
            await _psmux.NewSessionAsync(session.PsmuxSessionName, session.ProjectDir, "claude");
        }
    }

    public string[] GetProjectDirectories()
    {
        return Directory.GetDirectories(ProjectsRoot)
            .Select(d => new DirectoryInfo(d))
            .Where(d => !d.Name.Equals("muxer", StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d => d.Name)
            .ToArray();
    }

    public async Task<ManagedSession> CreateSessionAsync(string projectDir)
    {
        if (!Path.IsPathRooted(projectDir))
            projectDir = Path.Combine(ProjectsRoot, projectDir);

        if (!Directory.Exists(projectDir))
            Directory.CreateDirectory(projectDir);

        var projectName = new DirectoryInfo(projectDir).Name;
        var sessionName = $"claude-{projectName}".Replace(" ", "-").ToLowerInvariant();
        var id = Guid.NewGuid().ToString("N")[..8];

        await _psmux.KillSessionAsync(sessionName);
        await _psmux.NewSessionAsync(sessionName, projectDir, "claude");

        var session = new ManagedSession
        {
            Id = id,
            ProjectName = projectName,
            ProjectDir = projectDir,
            PsmuxSessionName = sessionName,
            StartedAt = DateTimeOffset.UtcNow
        };

        _sessions[id] = session;
        _logger.LogInformation("Session {Id} created: {Project}", id, projectName);

        return session;
    }

    public async Task DestroySessionAsync(string id)
    {
        if (!_sessions.TryRemove(id, out var session)) return;

        // Cancel any pending approval
        if (_pendingApprovals.TryRemove(id, out var tcs))
            tcs.TrySetCanceled();

        if (!session.IsAdHoc)
            await _psmux.KillSessionAsync(session.PsmuxSessionName);

        _logger.LogInformation("Session {Id} destroyed: {Project}", id, session.ProjectName);
    }

    public async Task DestroyAllAsync()
    {
        foreach (var id in _sessions.Keys.ToList())
            await DestroySessionAsync(id);
    }
}
