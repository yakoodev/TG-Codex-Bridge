using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TgCodexBridge.Core.Abstractions;
using TgCodexBridge.Core.Models;

namespace TgCodexBridge.Infrastructure.Services;

public sealed class CodexCliRunner(ILogger<CodexCliRunner> logger) : ICodexRunner
{
    private const int StderrTailLines = 20;
    private static readonly HashSet<string> ChatIdPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chat_id",
        "chatId",
        "session_id",
        "sessionId",
        "conversation_id",
        "conversationId",
        "thread_id",
        "threadId"
    };
    private static readonly Regex ContextPercentRegex =
        new(@"(?im)\b(?:(?<percent>\d{1,3})\s*%\s*(?:context|remaining)|context(?:\s+window)?(?:\s+left|\s+remaining)?\s*[:=]?\s*(?<percent2>\d{1,3})\s*%)\b", RegexOptions.Compiled);

    private readonly ConcurrentDictionary<(long ChatId, int ThreadId), ActiveRun> _activeRuns = new();

    public async IAsyncEnumerable<CodexRunUpdate> RunAsync(
        CodexRunRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var key = (request.ChatId, request.MessageThreadId);
        var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var activeRun = new ActiveRun(runCts, request.ProjectDirectory, request.Prompt);

        if (!_activeRuns.TryAdd(key, activeRun))
        {
            throw new InvalidOperationException("Run is already active for this topic.");
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                WorkingDirectory = request.ProjectDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            ConfigureStartInfo(startInfo, request);

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            activeRun.AttachProcess(process);

            logger.LogInformation(
                "Starting codex process; chatId={ChatId}, threadId={ThreadId}, cwd={WorkingDirectory}, cmd={Command}",
                request.ChatId,
                request.MessageThreadId,
                startInfo.WorkingDirectory,
                BuildDisplayCommand(startInfo));

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
                if (request.StopOnCommandStart &&
                    string.Equals(update.Kind, "command_start", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(update.Chunk))
                {
                    await CancelAsync(request.ChatId, request.MessageThreadId, cancellationToken);
                    throw new CodexApprovalRequiredException(update.Chunk.Trim());
                }

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

            logger.LogInformation(
                "Codex completed successfully; chatId={ChatId}, threadId={ThreadId}, exitCode={ExitCode}",
                request.ChatId,
                request.MessageThreadId,
                process.ExitCode);

            yield return new CodexRunUpdate("Completed", Kind: "status", IsFinal: true);
        }
        finally
        {
            _activeRuns.TryRemove(key, out _);
            runCts.Dispose();
        }
    }

    public async Task CancelAsync(long chatId, int messageThreadId, CancellationToken cancellationToken = default)
    {
        var key = (chatId, messageThreadId);
        if (!_activeRuns.TryGetValue(key, out var activeRun))
        {
            return;
        }

        var softCommand = Environment.GetEnvironmentVariable("CANCEL_SOFT_COMMAND") ?? string.Empty;
        var softTimeout = ReadTimeout("CANCEL_SOFT_TIMEOUT_SEC", 10);
        var killTimeout = ReadTimeout("CANCEL_KILL_TIMEOUT_SEC", 5);

        await activeRun.CancelAsync(
            softCommand,
            TimeSpan.FromSeconds(softTimeout),
            TimeSpan.FromSeconds(killTimeout),
            cancellationToken);
    }

    public Task SendInputAsync(long chatId, int messageThreadId, string input, CancellationToken cancellationToken = default)
    {
        var key = (chatId, messageThreadId);
        if (!_activeRuns.TryGetValue(key, out var activeRun))
        {
            return Task.CompletedTask;
        }

        return activeRun.SendInputAsync(input, cancellationToken);
    }

    public IReadOnlyList<CodexActiveRunInfo> GetActiveRuns()
    {
        return _activeRuns
            .Select(static pair => new CodexActiveRunInfo(
                pair.Key.ChatId,
                pair.Key.ThreadId,
                pair.Value.StartedAt,
                pair.Value.ProjectDirectory,
                pair.Value.Prompt))
            .OrderBy(info => info.StartedAt)
            .ToArray();
    }

    private static int ReadTimeout(string envName, int fallbackSeconds)
    {
        var raw = Environment.GetEnvironmentVariable(envName);
        if (int.TryParse(raw, out var parsed) && parsed > 0)
        {
            return parsed;
        }

        return fallbackSeconds;
    }

    private async Task PumpStdoutAsync(
        StreamReader reader,
        ChannelWriter<CodexRunUpdate> writer,
        CancellationToken cancellationToken)
    {
        string? lastCodexChatId;
        lastCodexChatId = null;

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (ShouldLogJsonEvents())
            {
                logger.LogInformation("Codex stdout event: {JsonLine}", line);
            }

            if (TryExtractCodexChatId(line, out var codexChatId) &&
                !string.Equals(codexChatId, lastCodexChatId, StringComparison.Ordinal))
            {
                lastCodexChatId = codexChatId;
                await writer.WriteAsync(new CodexRunUpdate(string.Empty, Kind: "meta", CodexChatId: codexChatId), cancellationToken);
            }

            if (TryExtractDisplayUpdateFromJsonLine(line, out var update) && !string.IsNullOrWhiteSpace(update.Chunk))
            {
                logger.LogInformation("Codex parsed update; kind={Kind}, size={Size}", update.Kind, update.Chunk.Length);
                await writer.WriteAsync(update, cancellationToken);
            }
        }
    }

    private async Task PumpStderrAsync(
        StreamReader reader,
        Queue<string> stderrTail,
        CancellationToken cancellationToken)
    {
        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null || string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            logger.LogWarning("Codex stderr: {Line}", line);

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
            if (string.Equals(eventType, "turn.completed", StringComparison.OrdinalIgnoreCase))
            {
                var contextPercent = TryExtractContextLeftPercent(root);
                if (contextPercent.HasValue)
                {
                    update = new CodexRunUpdate($"Context left: {contextPercent.Value}%", Kind: "status");
                    return true;
                }

                return false;
            }

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

            if (!TryExtractItemText(item, out var value) || string.IsNullOrWhiteSpace(value))
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

    private static bool TryExtractCodexChatId(string line, out string codexChatId)
    {
        codexChatId = string.Empty;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        if (line[0] == '{')
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("type", out var typeElement) &&
                    string.Equals(typeElement.GetString(), "thread.started", StringComparison.OrdinalIgnoreCase) &&
                    root.TryGetProperty("thread_id", out var threadIdElement) &&
                    threadIdElement.ValueKind == JsonValueKind.String)
                {
                    var threadId = threadIdElement.GetString();
                    if (!string.IsNullOrWhiteSpace(threadId))
                    {
                        codexChatId = threadId.Trim();
                        return true;
                    }
                }

                if (TryExtractCodexChatIdFromJsonElement(doc.RootElement, out codexChatId))
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                // Non-JSON fallback below.
            }
        }

        return false;
    }

    private static bool TryExtractCodexChatIdFromJsonElement(JsonElement element, out string codexChatId)
    {
        codexChatId = string.Empty;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String &&
                    ChatIdPropertyNames.Contains(property.Name))
                {
                    var value = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        codexChatId = value.Trim();
                        return true;
                    }
                }

                if (TryExtractCodexChatIdFromJsonElement(property.Value, out codexChatId))
                {
                    return true;
                }
            }

            return false;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryExtractCodexChatIdFromJsonElement(item, out codexChatId))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static int? TryExtractContextLeftPercent(JsonElement root)
    {
        if (TryExtractContextLeftPercentFromJson(root, out var percent))
        {
            return Math.Clamp(percent, 0, 100);
        }

        var raw = root.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var match = ContextPercentRegex.Match(raw);
        if (!match.Success)
        {
            return null;
        }

        var percentText = match.Groups["percent"].Success
            ? match.Groups["percent"].Value
            : match.Groups["percent2"].Value;
        return int.TryParse(percentText, out percent)
            ? Math.Clamp(percent, 0, 100)
            : null;

    }

    private static bool TryExtractContextLeftPercentFromJson(JsonElement element, out int percent)
    {
        percent = 0;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (IsContextPercentKey(property.Name) &&
                    TryParsePercentJsonValue(property.Value, out percent))
                {
                    return true;
                }

                if (TryExtractContextLeftPercentFromJson(property.Value, out percent))
                {
                    return true;
                }
            }

            return false;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryExtractContextLeftPercentFromJson(item, out percent))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsContextPercentKey(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var normalized = name.Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant();
        if (normalized is "context_left_percent" or "contextleftpercent" or "remaining_context_percent" or "remainingcontextpercent")
        {
            return true;
        }

        var hasContext = normalized.Contains("context", StringComparison.Ordinal);
        var hasPercent = normalized.Contains("percent", StringComparison.Ordinal) || normalized.Contains("pct", StringComparison.Ordinal);
        var hasRemaining = normalized.Contains("left", StringComparison.Ordinal) ||
                           normalized.Contains("remain", StringComparison.Ordinal) ||
                           normalized.Contains("window", StringComparison.Ordinal);

        return hasContext && hasPercent && hasRemaining;
    }

    private static bool TryParsePercentJsonValue(JsonElement value, out int percent)
    {
        percent = 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out percent))
        {
            percent = Math.Clamp(percent, 0, 100);
            return true;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetDouble(out var ratio))
            {
                if (ratio is >= 0 and <= 1)
                {
                    percent = (int)Math.Round(ratio * 100, MidpointRounding.AwayFromZero);
                    percent = Math.Clamp(percent, 0, 100);
                    return true;
                }

                if (ratio is > 1 and <= 100)
                {
                    percent = (int)Math.Round(ratio, MidpointRounding.AwayFromZero);
                    percent = Math.Clamp(percent, 0, 100);
                    return true;
                }
            }

            return false;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = ContextPercentRegex.Match(text);
        if (!match.Success)
        {
            return false;
        }

        var raw = match.Groups["percent"].Success
            ? match.Groups["percent"].Value
            : match.Groups["percent2"].Value;

        if (!int.TryParse(raw, out percent))
        {
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio))
            {
                return false;
            }

            if (ratio is >= 0 and <= 1)
            {
                percent = (int)Math.Round(ratio * 100, MidpointRounding.AwayFromZero);
            }
            else if (ratio is > 1 and <= 100)
            {
                percent = (int)Math.Round(ratio, MidpointRounding.AwayFromZero);
            }
            else
            {
                return false;
            }
        }

        percent = Math.Clamp(percent, 0, 100);
        return true;
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

    private static bool TryExtractItemText(JsonElement item, out string text)
    {
        if (item.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
        {
            text = textElement.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(text);
        }

        if (item.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var contentPart in contentElement.EnumerateArray())
            {
                if (!contentPart.TryGetProperty("text", out var partTextElement) || partTextElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var partText = partTextElement.GetString();
                if (string.IsNullOrWhiteSpace(partText))
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.AppendLine().AppendLine();
                }

                sb.Append(partText.TrimEnd());
            }

            text = sb.ToString();
            return !string.IsNullOrWhiteSpace(text);
        }

        text = string.Empty;
        return false;
    }

    private static string BuildDisplayCommand(ProcessStartInfo startInfo)
    {
        var args = startInfo.ArgumentList.Select(EscapeArg);
        return $"{startInfo.FileName} {string.Join(' ', args)}".TrimEnd();
    }

    private static void ConfigureStartInfo(ProcessStartInfo startInfo, CodexRunRequest request)
    {
        var backend = CodexLaunchBackend.NormalizeOrDefault(request.LaunchBackend);
        var sandbox = request.SandboxModeOverride ?? (Environment.GetEnvironmentVariable("CODEX_SANDBOX_MODE") ?? "workspace-write");
        var enableSearch = IsTrue(Environment.GetEnvironmentVariable("CODEX_ENABLE_WEB_SEARCH"));
        var approvalMode = Environment.GetEnvironmentVariable("CODEX_APPROVAL_MODE");
        if (string.IsNullOrWhiteSpace(approvalMode))
        {
            approvalMode = "untrusted";
        }

        var codexArgs = BuildCodexExecArgs(request, sandbox, enableSearch);
        var preArgs = new List<string> { "-a", approvalMode.Trim() };
        switch (backend)
        {
            case CodexLaunchBackend.Windows:
            {
                startInfo.FileName = Environment.GetEnvironmentVariable("CODEX_BIN_WINDOWS") ??
                                     Environment.GetEnvironmentVariable("CODEX_BIN") ??
                                     "codex";
                foreach (var arg in preArgs)
                {
                    startInfo.ArgumentList.Add(arg);
                }
                foreach (var arg in codexArgs)
                {
                    startInfo.ArgumentList.Add(arg);
                }

                break;
            }
            case CodexLaunchBackend.Wsl:
            {
                startInfo.FileName = Environment.GetEnvironmentVariable("CODEX_BIN_WSL") ?? "wsl";
                var distro = Environment.GetEnvironmentVariable("CODEX_WSL_DISTRO");
                if (!string.IsNullOrWhiteSpace(distro))
                {
                    startInfo.ArgumentList.Add("-d");
                    startInfo.ArgumentList.Add(distro.Trim());
                }

                startInfo.ArgumentList.Add("--cd");
                startInfo.ArgumentList.Add(request.ProjectDirectory);
                startInfo.ArgumentList.Add("--");
                startInfo.ArgumentList.Add(Environment.GetEnvironmentVariable("CODEX_WSL_CODEX_BIN") ?? "codex");
                foreach (var arg in preArgs)
                {
                    startInfo.ArgumentList.Add(arg);
                }
                foreach (var arg in codexArgs)
                {
                    startInfo.ArgumentList.Add(arg);
                }

                break;
            }
            default:
            {
                startInfo.FileName = Environment.GetEnvironmentVariable("CODEX_BIN_DOCKER") ??
                                     Environment.GetEnvironmentVariable("CODEX_BIN") ??
                                     "codex";
                foreach (var arg in preArgs)
                {
                    startInfo.ArgumentList.Add(arg);
                }
                foreach (var arg in codexArgs)
                {
                    startInfo.ArgumentList.Add(arg);
                }

                break;
            }
        }
    }

    private static List<string> BuildCodexExecArgs(CodexRunRequest request, string sandbox, bool enableSearch)
    {
        var args = new List<string>
        {
            "exec",
            "--json",
            "--skip-git-repo-check",
            "--sandbox",
            sandbox
        };

        if (enableSearch)
        {
            args.Add("--search");
        }

        if (!string.IsNullOrWhiteSpace(request.ResumeChatId))
        {
            args.Add("resume");
            args.Add(request.ResumeChatId!);
            args.Add(request.Prompt);
            return args;
        }

        args.Add(request.Prompt);
        return args;
    }

    private static string EscapeArg(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "\"\"";
        }

        return value.Contains(' ', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal)
            ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : value;
    }

    private static bool ShouldLogJsonEvents()
    {
        var raw = Environment.GetEnvironmentVariable("CODEX_LOG_JSON_EVENTS");
        return IsTrue(raw);
    }

    private static bool IsTrue(string? raw)
    {
        return string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase);
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

    private sealed class ActiveRun(CancellationTokenSource cts, string projectDirectory, string prompt)
    {
        private readonly object _sync = new();
        private Process? _process;

        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public string ProjectDirectory { get; } = projectDirectory;
        public string Prompt { get; } = prompt;

        public void AttachProcess(Process process)
        {
            lock (_sync)
            {
                _process = process;
            }
        }

        public async Task SendInputAsync(string input, CancellationToken cancellationToken)
        {
            Process? process;
            lock (_sync)
            {
                process = _process;
            }

            if (process is null || process.HasExited)
            {
                return;
            }

            await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
        }

        public async Task CancelAsync(
            string softCommand,
            TimeSpan softTimeout,
            TimeSpan killTimeout,
            CancellationToken cancellationToken)
        {
            Process? process;
            lock (_sync)
            {
                process = _process;
            }

            if (process is null || process.HasExited)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(softCommand))
            {
                var decoded = DecodeEscapes(softCommand);
                await SendInputAsync(decoded, cancellationToken);
            }

            if (await WaitForExitAsync(process, softTimeout, cancellationToken))
            {
                cts.Cancel();
                return;
            }

            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                cts.Cancel();
                return;
            }

            _ = await WaitForExitAsync(process, killTimeout, cancellationToken);
            cts.Cancel();
        }

        private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (process.HasExited)
            {
                return true;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
                return true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return process.HasExited;
            }
        }

        private static string DecodeEscapes(string value)
        {
            var sb = new StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                var ch = value[i];
                if (ch != '\\' || i + 1 >= value.Length)
                {
                    sb.Append(ch);
                    continue;
                }

                var next = value[i + 1];
                switch (next)
                {
                    case 'n':
                        sb.Append('\n');
                        i++;
                        break;
                    case 'r':
                        sb.Append('\r');
                        i++;
                        break;
                    case 't':
                        sb.Append('\t');
                        i++;
                        break;
                    case '\\':
                        sb.Append('\\');
                        i++;
                        break;
                    case 'x' when i + 3 < value.Length &&
                                  int.TryParse(value.Substring(i + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex):
                        sb.Append((char)hex);
                        i += 3;
                        break;
                    default:
                        sb.Append(ch);
                        break;
                }
            }

            return sb.ToString();
        }
    }
}
