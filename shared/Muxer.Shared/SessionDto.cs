namespace Muxer.Shared;

public record SessionDto(
    string Id,
    string ProjectName,
    string ProjectDir,
    string PsmuxSessionName,
    SessionStatus Status,
    string? ApprovalContext,
    string[]? ApprovalOptions,
    DateTimeOffset StartedAt,
    DateTimeOffset? ApprovalDetectedAt
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
    string Context,
    string[] Options,
    DateTimeOffset DetectedAt
);

public record ApprovalResponseDto(
    string SessionId,
    int OptionNumber
);
