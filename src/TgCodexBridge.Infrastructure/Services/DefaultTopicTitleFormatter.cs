using TgCodexBridge.Core.Abstractions;

namespace TgCodexBridge.Infrastructure.Services;

public sealed class DefaultTopicTitleFormatter : ITopicTitleFormatter
{
    private const int MaxTitleLength = 119;

    public string Format(string projectName, string directoryPath, bool isBusy, int? contextLeftPercent = null, string? status = null)
    {
        var emoji = ResolveEmoji(isBusy, status);
        var contextPart = contextLeftPercent.HasValue
            ? $"{Math.Clamp(contextLeftPercent.Value, 0, 100)}%"
            : "n/a";

        var title = $"{emoji} {projectName} \u00B7 {contextPart} \u00B7 {GetTail(directoryPath)}";
        return title.Length < 120 ? title : title[..MaxTitleLength];
    }

    private static string ResolveEmoji(bool isBusy, string? status)
    {
        if (isBusy)
        {
            return "\uD83D\uDFE1";
        }

        if (status is "error" or "cancelled")
        {
            return "\uD83D\uDD34";
        }

        return "\uD83D\uDFE2";
    }

    private static string GetTail(string path)
    {
        var parts = path
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
        {
            return path;
        }

        return parts.Length == 1 ? parts[0] : $"{parts[^2]}/{parts[^1]}";
    }
}
