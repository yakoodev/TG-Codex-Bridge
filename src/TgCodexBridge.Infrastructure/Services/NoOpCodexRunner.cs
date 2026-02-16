using System.Runtime.CompilerServices;
using TgCodexBridge.Core.Abstractions;
using TgCodexBridge.Core.Models;

namespace TgCodexBridge.Infrastructure.Services;

public sealed class NoOpCodexRunner : ICodexRunner
{
    public async IAsyncEnumerable<CodexRunUpdate> RunAsync(CodexRunRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new CodexRunUpdate($"Stub runner accepted prompt for {request.ProjectDirectory}");
        await Task.Delay(10, cancellationToken);
        yield return new CodexRunUpdate("Done", IsFinal: true);
    }

    public Task CancelAsync(long chatId, int messageThreadId, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
