using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TgCodexBridge.Bot;

public sealed class Worker(ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var stateDir = Environment.GetEnvironmentVariable("STATE_DIR") ?? "data";
        var logDir = Environment.GetEnvironmentVariable("LOG_DIR") ?? Path.Combine(stateDir, "logs");
        var stateDbPath = Path.Combine(stateDir, "state.db");
        var heartbeatPath = Path.Combine(stateDir, "heartbeat");
        var appLogPath = Path.Combine(logDir, "app.log");

        Directory.CreateDirectory(stateDir);
        Directory.CreateDirectory(logDir);
        if (!File.Exists(stateDbPath))
        {
            await File.WriteAllTextAsync(stateDbPath, "-- sqlite bootstrap placeholder\n", stoppingToken);
        }

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
