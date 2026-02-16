using Microsoft.Extensions.Logging;
using TgCodexBridge.Core.Abstractions;

namespace TgCodexBridge.Infrastructure.Services;

public sealed class ConsoleTelegramClient(ILogger<ConsoleTelegramClient> logger) : ITelegramClient
{
    public Task SendTextAsync(long chatId, string text, int? messageThreadId = null, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Telegram message => chatId={ChatId}, threadId={ThreadId}, text={Text}", chatId, messageThreadId, text);
        return Task.CompletedTask;
    }

    public Task UpdateTopicTitleAsync(long chatId, int messageThreadId, string title, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Telegram topic title => chatId={ChatId}, threadId={ThreadId}, title={Title}", chatId, messageThreadId, title);
        return Task.CompletedTask;
    }
}
