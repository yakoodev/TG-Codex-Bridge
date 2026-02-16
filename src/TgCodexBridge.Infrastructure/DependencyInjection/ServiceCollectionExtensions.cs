using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TgCodexBridge.Core.Abstractions;
using TgCodexBridge.Infrastructure.Options;
using TgCodexBridge.Infrastructure.Services;

namespace TgCodexBridge.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PathPolicyOptions>(options =>
        {
            configuration.GetSection(PathPolicyOptions.SectionName).Bind(options);

            var mode = configuration["PATH_POLICY_MODE"];
            if (!string.IsNullOrWhiteSpace(mode))
            {
                options.Mode = mode;
            }

            var roots = configuration["ALLOWED_ROOTS"];
            if (!string.IsNullOrWhiteSpace(roots))
            {
                options.AllowedRoots = roots
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            }
        });

        services.AddSingleton<ITelegramClient, ConsoleTelegramClient>();
        services.AddSingleton<IStateStore, InMemoryStateStore>();
        services.AddSingleton<ICodexRunner, NoOpCodexRunner>();
        services.AddSingleton<ITopicTitleFormatter, DefaultTopicTitleFormatter>();
        services.AddSingleton<IPathPolicy, PathPolicy>();

        return services;
    }
}
