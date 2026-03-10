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
    public SessionStatus Status { get; set; } = SessionStatus.Running;
    public string? ApprovalContext { get; set; }
    public string[]? ApprovalOptions { get; set; }
    public DateTimeOffset? ApprovalDetectedAt { get; set; }

    public SessionDto ToDto() => new(
        Id, ProjectName, ProjectDir, PsmuxSessionName, Status,
        ApprovalContext, ApprovalOptions, StartedAt, ApprovalDetectedAt
    );
}

public class SessionManager
{
    private const string ProjectsRoot = @"C:\projects";

    private readonly ConcurrentDictionary<string, ManagedSession> _sessions = new();
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

    private async Task EnsureAliveAsync(ManagedSession session)
    {
        // Check psmux session is alive, recreate if not
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
        // Resolve relative names to full path
        if (!Path.IsPathRooted(projectDir))
            projectDir = Path.Combine(ProjectsRoot, projectDir);

        if (!Directory.Exists(projectDir))
            Directory.CreateDirectory(projectDir);

        var projectName = new DirectoryInfo(projectDir).Name;
        var sessionName = $"claude-{projectName}".Replace(" ", "-").ToLowerInvariant();
        var id = Guid.NewGuid().ToString("N")[..8];

        // Kill any existing session with same name
        await _psmux.KillSessionAsync(sessionName);

        // Start psmux session
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

        await _psmux.KillSessionAsync(session.PsmuxSessionName);

        _logger.LogInformation("Session {Id} destroyed: {Project}", id, session.ProjectName);
    }

    public async Task DestroyAllAsync()
    {
        foreach (var id in _sessions.Keys.ToList())
            await DestroySessionAsync(id);
    }
}
