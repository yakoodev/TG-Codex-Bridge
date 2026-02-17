namespace TgCodexBridge.Core.Models;

public sealed record TopicRecord(
    long Id,
    long ProjectId,
    long GroupChatId,
    int MessageThreadId,
    string? CodexChatId,
    string Name,
    bool Busy,
    string Status,
    int? ContextLeftPercent,
    string LaunchBackend,
    DateTimeOffset? LastJobStartedAt,
    DateTimeOffset? LastJobFinishedAt);
