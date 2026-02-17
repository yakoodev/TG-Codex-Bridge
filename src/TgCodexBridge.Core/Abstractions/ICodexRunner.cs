using TgCodexBridge.Core.Models;

namespace TgCodexBridge.Core.Abstractions;

public interface ICodexRunner
{
    IAsyncEnumerable<CodexRunUpdate> RunAsync(CodexRunRequest request, CancellationToken cancellationToken = default);
    Task CancelAsync(long chatId, int messageThreadId, CancellationToken cancellationToken = default);
    Task SendInputAsync(long chatId, int messageThreadId, string input, CancellationToken cancellationToken = default);
    IReadOnlyList<CodexActiveRunInfo> GetActiveRuns();
}
