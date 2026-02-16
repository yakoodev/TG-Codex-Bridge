namespace TgCodexBridge.Core.Abstractions;

public interface ITopicTitleFormatter
{
    string Format(string projectName, bool isBusy, int? contextLeftPercent = null, string? status = null);
}
