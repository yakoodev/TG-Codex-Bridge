namespace TgCodexBridge.Core.Models;

public static class CodexLaunchBackend
{
    public const string Docker = "docker";
    public const string Windows = "windows";
    public const string Wsl = "wsl";

    public static bool IsSupported(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Equals(Docker, StringComparison.OrdinalIgnoreCase) ||
               value.Equals(Windows, StringComparison.OrdinalIgnoreCase) ||
               value.Equals(Wsl, StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeOrDefault(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Docker;
        }

        if (value.Equals(Windows, StringComparison.OrdinalIgnoreCase))
        {
            return Windows;
        }

        if (value.Equals(Wsl, StringComparison.OrdinalIgnoreCase))
        {
            return Wsl;
        }

        return Docker;
    }
}
