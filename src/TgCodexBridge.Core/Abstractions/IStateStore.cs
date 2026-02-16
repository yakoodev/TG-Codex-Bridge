using TgCodexBridge.Core.Models;

namespace TgCodexBridge.Core.Abstractions;

public interface IStateStore
{
    Task<ProjectRecord> GetOrCreateProjectAsync(string dirPath, CancellationToken cancellationToken = default);
    Task<TopicRecord> CreateTopicAsync(long projectId, long groupChatId, int threadId, string name, CancellationToken cancellationToken = default);
    Task<TopicRecord?> GetTopicByThreadIdAsync(long groupChatId, int threadId, CancellationToken cancellationToken = default);
    Task SetTopicBusyAsync(long topicId, bool busy, CancellationToken cancellationToken = default);
    Task UpdateTopicStatusAsync(long topicId, string status, CancellationToken cancellationToken = default);
    Task UpdateTopicContextLeftAsync(long topicId, int? percent, CancellationToken cancellationToken = default);
    Task UpdateTopicCodexChatIdAsync(long topicId, string? codexChatId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<long>> ListNotifyUsersAsync(CancellationToken cancellationToken = default);
    Task StartTopicJobAsync(long topicId, CancellationToken cancellationToken = default);
    Task FinishTopicJobAsync(long topicId, string finalStatus, CancellationToken cancellationToken = default);
}
