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
    DateTimeOffset? LastJobStartedAt,
    DateTimeOffset? LastJobFinishedAt);
