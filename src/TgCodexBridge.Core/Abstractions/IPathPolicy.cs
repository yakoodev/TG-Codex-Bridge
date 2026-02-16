namespace TgCodexBridge.Core.Abstractions;

public interface IPathPolicy
{
    bool IsAllowed(string path);
}
