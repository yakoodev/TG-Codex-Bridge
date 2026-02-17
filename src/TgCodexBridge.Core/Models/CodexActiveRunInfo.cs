namespace TgCodexBridge.Core.Models;

public sealed record CodexActiveRunInfo(
    long ChatId,
    int MessageThreadId,
    DateTimeOffset StartedAt,
    string ProjectDirectory,
    string Prompt);
