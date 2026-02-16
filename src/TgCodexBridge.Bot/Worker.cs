using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TgCodexBridge.Core.Abstractions;

namespace TgCodexBridge.Bot;

public sealed class Worker(ILogger<Worker> logger, IStateStore stateStore) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var stateDir = Environment.GetEnvironmentVariable("STATE_DIR") ?? "data";
        var logDir = Environment.GetEnvironmentVariable("LOG_DIR") ?? Path.Combine(stateDir, "logs");
        var heartbeatPath = Path.Combine(stateDir, "heartbeat");
        var appLogPath = Path.Combine(logDir, "app.log");

        Directory.CreateDirectory(stateDir);
        Directory.CreateDirectory(logDir);

        // Force SQLite init + migrations on startup.
        _ = await stateStore.GetOrCreateProjectAsync(Environment.CurrentDirectory, stoppingToken);

        logger.LogInformation("Started");
        await File.AppendAllTextAsync(appLogPath, $"{DateTimeOffset.UtcNow:O} Started{Environment.NewLine}", stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Heartbeat {Timestamp}", DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(heartbeatPath, DateTimeOffset.UtcNow.ToString("O"), stoppingToken);
            await File.AppendAllTextAsync(appLogPath, $"{DateTimeOffset.UtcNow:O} Heartbeat{Environment.NewLine}", stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
