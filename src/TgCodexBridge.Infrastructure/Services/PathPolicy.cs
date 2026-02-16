using Microsoft.Extensions.Options;
using TgCodexBridge.Core.Abstractions;
using TgCodexBridge.Infrastructure.Options;

namespace TgCodexBridge.Infrastructure.Services;

public sealed class PathPolicy(IOptions<PathPolicyOptions> options) : IPathPolicy
{
    private readonly PathPolicyOptions _options = options.Value;

    public bool IsAllowed(string path)
    {
        if (_options.Mode.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (_options.AllowedRoots.Length == 0)
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        return _options.AllowedRoots.Any(root =>
        {
            var fullRoot = Path.GetFullPath(root);
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        });
    }
}
