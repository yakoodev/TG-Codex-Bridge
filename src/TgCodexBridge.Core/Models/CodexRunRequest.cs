namespace TgCodexBridge.Core.Models;

public sealed record CodexRunRequest(
    long ChatId,
    int MessageThreadId,
    string ProjectDirectory,
    string Prompt,
    string? ResumeChatId = null);
