namespace TgCodexBridge.Core.Abstractions;

public interface ITopicTitleFormatter
{
    string Format(string projectName, string directoryPath, bool isBusy, int? contextLeftPercent = null, string? status = null);
}
