using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using TgCodexBridge.Core.Abstractions;
using TgCodexBridge.Core.Models;

namespace TgCodexBridge.Infrastructure.Services;

public sealed class CodexCliRunner : ICodexRunner
{
    private const int StderrTailLines = 20;
    private readonly ConcurrentDictionary<(long ChatId, int ThreadId), ActiveRun> _activeRuns = new();

    public async IAsyncEnumerable<CodexRunUpdate> RunAsync(
        CodexRunRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var key = (request.ChatId, request.MessageThreadId);
        var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var activeRun = new ActiveRun(runCts);

        if (!_activeRuns.TryAdd(key, activeRun))
        {
            throw new InvalidOperationException("Run is already active for this topic.");
        }

        try
        {
            var codexBin = Environment.GetEnvironmentVariable("CODEX_BIN");
            if (string.IsNullOrWhiteSpace(codexBin))
            {
                codexBin = "codex";
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = codexBin,
                WorkingDirectory = request.ProjectDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add("--json");
            startInfo.ArgumentList.Add("--skip-git-repo-check");

            if (!string.IsNullOrWhiteSpace(request.ResumeChatId))
            {
                startInfo.ArgumentList.Add("resume");
                startInfo.ArgumentList.Add(request.ResumeChatId!);
                startInfo.ArgumentList.Add(request.Prompt);
            }
            else
            {
                startInfo.ArgumentList.Add(request.Prompt);
            }

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            activeRun.AttachProcess(process);

            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start codex process.");
            }

            var stderrTail = new Queue<string>(StderrTailLines);
            var channel = Channel.CreateUnbounded<CodexRunUpdate>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

            var stdoutTask = PumpStdoutAsync(process.StandardOutput, channel.Writer, runCts.Token);
            var stderrTask = PumpStderrAsync(process.StandardError, stderrTail, runCts.Token);
            var channelCompletionTask = CompleteChannelWhenReadersFinishAsync(channel.Writer, stdoutTask, stderrTask);

            await foreach (var update in channel.Reader.ReadAllAsync(runCts.Token))
            {
                yield return update;
            }

            await process.WaitForExitAsync(runCts.Token);
            await channelCompletionTask;

            if (runCts.IsCancellationRequested)
            {
                throw new OperationCanceledException(runCts.Token);
            }

            if (process.ExitCode != 0)
            {
                throw BuildCodexRunException(process.ExitCode, stderrTail, "Codex exited with error.");
            }

            yield return new CodexRunUpdate("✅ Завершено", Kind: "status", IsFinal: true);
        }
        finally
        {
            _activeRuns.TryRemove(key, out _);
            runCts.Dispose();
        }
    }

    public Task CancelAsync(long chatId, int messageThreadId, CancellationToken cancellationToken = default)
    {
        var key = (chatId, messageThreadId);
        if (!_activeRuns.TryGetValue(key, out var activeRun))
        {
            return Task.CompletedTask;
        }

        activeRun.Cancel();
        return Task.CompletedTask;
    }

    private static async Task PumpStdoutAsync(
        StreamReader reader,
        ChannelWriter<CodexRunUpdate> writer,
        CancellationToken cancellationToken)
    {
        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (TryExtractDisplayUpdateFromJsonLine(line, out var update) && !string.IsNullOrWhiteSpace(update.Chunk))
            {
                await writer.WriteAsync(update, cancellationToken);
            }
        }
    }

    private static async Task PumpStderrAsync(
        StreamReader reader,
        Queue<string> stderrTail,
        CancellationToken cancellationToken)
    {
        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            lock (stderrTail)
            {
                if (stderrTail.Count == StderrTailLines)
                {
                    _ = stderrTail.Dequeue();
                }

                stderrTail.Enqueue(line);
            }
        }
    }

    private static bool TryExtractDisplayUpdateFromJsonLine(string line, out CodexRunUpdate update)
    {
        update = new CodexRunUpdate(string.Empty);
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{')
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var eventTypeElement))
            {
                return false;
            }

            var eventType = eventTypeElement.GetString();
            if (!string.Equals(eventType, "item.completed", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(eventType, "item.started", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!root.TryGetProperty("item", out var item))
            {
                return false;
            }

            var itemType = item.TryGetProperty("type", out var itemTypeElement)
                ? itemTypeElement.GetString()
                : null;

            if (string.Equals(itemType, "command_execution", StringComparison.OrdinalIgnoreCase))
            {
                var command = item.TryGetProperty("command", out var cmdElement) && cmdElement.ValueKind == JsonValueKind.String
                    ? cmdElement.GetString() ?? string.Empty
                    : string.Empty;

                if (string.IsNullOrWhiteSpace(command))
                {
                    return false;
                }

                if (string.Equals(eventType, "item.started", StringComparison.OrdinalIgnoreCase))
                {
                    update = new CodexRunUpdate(command, Kind: "command_start");
                    return true;
                }

                var output = item.TryGetProperty("aggregated_output", out var outElement) && outElement.ValueKind == JsonValueKind.String
                    ? outElement.GetString() ?? string.Empty
                    : string.Empty;
                var exitCode = item.TryGetProperty("exit_code", out var exitElement) && exitElement.ValueKind != JsonValueKind.Null
                    ? exitElement.ToString()
                    : "?";

                var text = string.IsNullOrWhiteSpace(output)
                    ? $"$ {command}\n(exit: {exitCode})"
                    : $"$ {command}\n{output.TrimEnd()}\n(exit: {exitCode})";

                update = new CodexRunUpdate(text, Kind: "command");
                return true;
            }

            if (!string.Equals(eventType, "item.completed", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!item.TryGetProperty("text", out var textElement) || textElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var value = textElement.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var kind = string.Equals(itemType, "reasoning", StringComparison.OrdinalIgnoreCase)
                ? "reasoning"
                : "answer";

            update = new CodexRunUpdate(value, Kind: kind);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task CompleteChannelWhenReadersFinishAsync(ChannelWriter<CodexRunUpdate> writer, params Task[] readerTasks)
    {
        try
        {
            await Task.WhenAll(readerTasks);
            writer.TryComplete();
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex);
        }
    }

    private static InvalidOperationException BuildCodexRunException(int exitCode, Queue<string> stderrTail, string message)
    {
        string tail;
        lock (stderrTail)
        {
            tail = string.Join(Environment.NewLine, stderrTail);
        }

        if (string.IsNullOrWhiteSpace(tail))
        {
            return new InvalidOperationException($"{message} Exit code: {exitCode}.");
        }

        return new InvalidOperationException($"{message} Exit code: {exitCode}. Stderr tail:{Environment.NewLine}{tail}");
    }

    private sealed class ActiveRun(CancellationTokenSource cts)
    {
        private readonly object _sync = new();
        private Process? _process;

        public void AttachProcess(Process process)
        {
            lock (_sync)
            {
                _process = process;
            }
        }

        public void Cancel()
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            lock (_sync)
            {
                if (_process is null || _process.HasExited)
                {
                    return;
                }

                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Ignore kill failures: process may have already exited.
                }
            }
        }
    }
}
