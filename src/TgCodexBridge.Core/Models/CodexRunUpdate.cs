namespace TgCodexBridge.Core.Models;

public sealed record CodexRunUpdate(string Chunk, string Kind = "answer", bool IsFinal = false, string? CodexChatId = null);
