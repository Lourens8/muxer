namespace Muxer.Shared;

public record SessionDto(
    string Id,
    string ProjectName,
    string ProjectDir,
    string PsmuxSessionName,
    SessionStatus Status,
    string? PendingToolName,
    string? PendingToolInput,
    DateTimeOffset StartedAt,
    DateTimeOffset? ApprovalRequestedAt,
    bool IsAdHoc = false
);

public enum SessionStatus
{
    Running,
    WaitingForApproval,
    Stopped
}

public record ApprovalRequestDto(
    string SessionId,
    string ProjectName,
    string ToolName,
    string ToolInput,
    DateTimeOffset DetectedAt
);
