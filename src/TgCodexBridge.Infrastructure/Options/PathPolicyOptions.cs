namespace TgCodexBridge.Infrastructure.Options;

public sealed class PathPolicyOptions
{
    public const string SectionName = "PathPolicy";

    public string Mode { get; set; } = "all";
    public string[] AllowedRoots { get; set; } = [];
}
