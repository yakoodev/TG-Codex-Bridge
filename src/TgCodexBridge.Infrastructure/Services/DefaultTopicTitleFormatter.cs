using TgCodexBridge.Core.Abstractions;

namespace TgCodexBridge.Infrastructure.Services;

public sealed class DefaultTopicTitleFormatter : ITopicTitleFormatter
{
    public string Format(string projectName, bool isBusy, int? contextLeftPercent = null, string? status = null)
    {
        var state = isBusy ? "busy" : "idle";
        var context = contextLeftPercent.HasValue ? $" ({contextLeftPercent.Value}% ctx)" : string.Empty;
        var suffix = string.IsNullOrWhiteSpace(status) ? string.Empty : $" - {status}";
        return $"{projectName} [{state}{context}]{suffix}";
    }
}
