using System.Collections.Concurrent;
using TgCodexBridge.Core.Abstractions;
using TgCodexBridge.Core.Models;

namespace TgCodexBridge.Infrastructure.Services;

public sealed class InMemoryStateStore : IStateStore
{
    private readonly ConcurrentDictionary<string, ProjectRecord> _projectsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<long, ProjectRecord> _projectsById = new();
    private readonly ConcurrentDictionary<long, TopicRecord> _topicsById = new();
    private readonly ConcurrentDictionary<(long ChatId, int ThreadId), long> _topicIdByThread = new();
    private long _projectId;
    private long _topicId;

    public Task<ProjectRecord> GetOrCreateProjectAsync(string dirPath, CancellationToken cancellationToken = default)
    {
        var normalizedPath = Path.GetFullPath(dirPath);
        var project = _projectsByPath.GetOrAdd(normalizedPath, path =>
        {
            var created = new ProjectRecord(Interlocked.Increment(ref _projectId), path, DateTimeOffset.UtcNow);
            _projectsById[created.Id] = created;
            return created;
        });

        _projectsById[project.Id] = project;
        return Task.FromResult(project);
    }

    public Task<ProjectRecord?> GetProjectByIdAsync(long projectId, CancellationToken cancellationToken = default)
    {
        _projectsById.TryGetValue(projectId, out var project);
        return Task.FromResult<ProjectRecord?>(project);
    }

    public Task<TopicRecord> CreateTopicAsync(long projectId, long groupChatId, int threadId, string name, CancellationToken cancellationToken = default)
    {
        if (_topicIdByThread.TryGetValue((groupChatId, threadId), out var existingTopicId) && _topicsById.TryGetValue(existingTopicId, out var existingTopic))
        {
            return Task.FromResult(existingTopic);
        }

        var topic = new TopicRecord(
            Id: Interlocked.Increment(ref _topicId),
            ProjectId: projectId,
            GroupChatId: groupChatId,
            MessageThreadId: threadId,
            CodexChatId: null,
            Name: name,
            Busy: false,
            Status: "idle",
            ContextLeftPercent: null,
            LaunchBackend: CodexLaunchBackend.Docker,
            LastJobStartedAt: null,
            LastJobFinishedAt: null);

        _topicsById[topic.Id] = topic;
        _topicIdByThread[(groupChatId, threadId)] = topic.Id;
        return Task.FromResult(topic);
    }

    public Task<TopicRecord?> GetTopicByThreadIdAsync(long groupChatId, int threadId, CancellationToken cancellationToken = default)
    {
        if (_topicIdByThread.TryGetValue((groupChatId, threadId), out var topicId) && _topicsById.TryGetValue(topicId, out var topic))
        {
            return Task.FromResult<TopicRecord?>(topic);
        }

        return Task.FromResult<TopicRecord?>(null);
    }

    public Task SetTopicBusyAsync(long topicId, bool busy, CancellationToken cancellationToken = default)
    {
        Update(topicId, t => t with { Busy = busy });
        return Task.CompletedTask;
    }

    public Task UpdateTopicStatusAsync(long topicId, string status, CancellationToken cancellationToken = default)
    {
        Update(topicId, t => t with { Status = status });
        return Task.CompletedTask;
    }

    public Task UpdateTopicContextLeftAsync(long topicId, int? percent, CancellationToken cancellationToken = default)
    {
        Update(topicId, t => t with { ContextLeftPercent = percent });
        return Task.CompletedTask;
    }

    public Task UpdateTopicCodexChatIdAsync(long topicId, string? codexChatId, CancellationToken cancellationToken = default)
    {
        Update(topicId, t => t with { CodexChatId = codexChatId });
        return Task.CompletedTask;
    }

    public Task UpdateTopicLaunchBackendAsync(long topicId, string launchBackend, CancellationToken cancellationToken = default)
    {
        var normalized = CodexLaunchBackend.NormalizeOrDefault(launchBackend);
        Update(topicId, t => t with { LaunchBackend = normalized });
        return Task.CompletedTask;
    }

    public Task DeleteTopicAsync(long topicId, CancellationToken cancellationToken = default)
    {
        if (_topicsById.TryRemove(topicId, out var topic))
        {
            _topicIdByThread.TryRemove((topic.GroupChatId, topic.MessageThreadId), out _);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<long>> ListNotifyUsersAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<long>>([]);
    }

    public Task StartTopicJobAsync(long topicId, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        Update(topicId, t => t with { Busy = true, Status = "working", LastJobStartedAt = now, LastJobFinishedAt = null });
        return Task.CompletedTask;
    }

    public Task FinishTopicJobAsync(long topicId, string finalStatus, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        Update(topicId, t => t with { Busy = false, Status = finalStatus, LastJobFinishedAt = now });
        return Task.CompletedTask;
    }

    private void Update(long topicId, Func<TopicRecord, TopicRecord> updater)
    {
        if (_topicsById.TryGetValue(topicId, out var topic))
        {
            _topicsById[topicId] = updater(topic);
        }
    }
}
