using TgCodexBridge.Core.Models;

namespace TgCodexBridge.Core.Abstractions;

public interface ITelegramClient
{
    Task SendTextAsync(long chatId, string text, int? messageThreadId = null, CancellationToken cancellationToken = default);
    Task UpdateTopicTitleAsync(long chatId, int messageThreadId, string title, CancellationToken cancellationToken = default);
}
