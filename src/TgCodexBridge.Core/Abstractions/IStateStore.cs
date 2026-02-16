using TgCodexBridge.Core.Models;

namespace TgCodexBridge.Core.Abstractions;

public interface IStateStore
{
    Task SaveTopicBindingAsync(TopicBinding binding, CancellationToken cancellationToken = default);
    Task<TopicBinding?> GetTopicBindingAsync(long chatId, int messageThreadId, CancellationToken cancellationToken = default);
    Task SaveTopicStatusAsync(long chatId, int messageThreadId, TopicStatus status, CancellationToken cancellationToken = default);
    Task<TopicStatus?> GetTopicStatusAsync(long chatId, int messageThreadId, CancellationToken cancellationToken = default);
}
