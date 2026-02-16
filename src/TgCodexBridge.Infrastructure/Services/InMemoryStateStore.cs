using System.Collections.Concurrent;
using TgCodexBridge.Core.Abstractions;
using TgCodexBridge.Core.Models;

namespace TgCodexBridge.Infrastructure.Services;

public sealed class InMemoryStateStore : IStateStore
{
    private readonly ConcurrentDictionary<(long ChatId, int ThreadId), TopicBinding> _bindings = new();
    private readonly ConcurrentDictionary<(long ChatId, int ThreadId), TopicStatus> _statuses = new();

    public Task SaveTopicBindingAsync(TopicBinding binding, CancellationToken cancellationToken = default)
    {
        _bindings[(binding.ChatId, binding.MessageThreadId)] = binding;
        return Task.CompletedTask;
    }

    public Task<TopicBinding?> GetTopicBindingAsync(long chatId, int messageThreadId, CancellationToken cancellationToken = default)
    {
        _bindings.TryGetValue((chatId, messageThreadId), out var binding);
        return Task.FromResult(binding);
    }

    public Task SaveTopicStatusAsync(long chatId, int messageThreadId, TopicStatus status, CancellationToken cancellationToken = default)
    {
        _statuses[(chatId, messageThreadId)] = status;
        return Task.CompletedTask;
    }

    public Task<TopicStatus?> GetTopicStatusAsync(long chatId, int messageThreadId, CancellationToken cancellationToken = default)
    {
        _statuses.TryGetValue((chatId, messageThreadId), out var status);
        return Task.FromResult(status);
    }
}
