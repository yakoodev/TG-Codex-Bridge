using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TgCodexBridge.Bot;

public sealed class Worker(ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Started");

        while (!stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Heartbeat {Timestamp}", DateTimeOffset.UtcNow);
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
