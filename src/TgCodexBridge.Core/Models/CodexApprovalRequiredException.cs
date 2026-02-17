namespace TgCodexBridge.Core.Models;

public sealed class CodexApprovalRequiredException(string command)
    : Exception($"Codex command requires approval: {command}")
{
    public string Command { get; } = command;
}
