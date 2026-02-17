using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TgCodexBridge.Core.Abstractions;
using TgCodexBridge.Core.Models;

namespace TgCodexBridge.Bot;

public sealed class Worker(
    ILogger<Worker> logger,
    IStateStore stateStore,
    ICodexRunner codexRunner,
    IPathPolicy pathPolicy,
    ITopicTitleFormatter topicTitleFormatter) : BackgroundService
{
    private const int PageSize = 30;
    private const int MaxListElements = 300;
    private const int TelegramMessageLimit = 4000;
    private static readonly TimeSpan TitleUpdateDebounce = TimeSpan.FromSeconds(2);
    private static readonly Regex ContextLeftRegex =
        new(@"\b(?<percent>\d{1,3})\s*%\s*(?:context\s+left|remaining(?:\s+context)?|context\s+remaining)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ContextLeftReverseRegex =
        new(@"\bcontext(?:\s+window)?\s*(?:left|remaining)\s*[:=]?\s*(?<percent>\d{1,3})\s*%\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ContextLeftAssignmentRegex =
        new(@"\bcontext(?:_left|(?:\s+left)|(?:\s+remaining))?\s*[:=]\s*(?<percent>\d{1,3})\s*%\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SandboxDeniedRegex =
        new(@"(?im)\b(read-?only|permission denied|operation not permitted|sandbox|только\s+для\s+чтения|заблокир(?:ован|ована)\s+политикой|доступ\s+запрещ[её]н)\b", RegexOptions.Compiled);
    private static readonly Regex ApprovalCommandRegex = new(@"(?m)^\$\s*(?<cmd>.+?)\s*$", RegexOptions.Compiled);
    private static readonly string[] ApprovalPromptMarkers =
    [
        "Would you like to run the following command?",
        "Would you like to run this command?",
        "run the following command",
        "run this command"
    ];
    private static readonly Regex ApprovalFencedCommandRegex =
        new(@"```(?:bash|sh|zsh|pwsh|powershell)?\s*(?<cmd>.+?)\s*```", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly HashSet<string> HiddenDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        "node_modules",
        "bin",
        "obj",
        "Library",
        "Temp"
    };

    private readonly ConcurrentDictionary<long, MkprojSession> _mkprojSessions = new();
    private readonly ConcurrentDictionary<(long ChatId, int ThreadId), DateTimeOffset> _lastTitleUpdateAt = new();
    private readonly ConcurrentDictionary<(long ChatId, int ThreadId), PendingApproval> _pendingApprovals = new();
    private readonly ConcurrentDictionary<(long ChatId, int ThreadId), int> _pendingDeleteConfirmations = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var stateDir = Environment.GetEnvironmentVariable("STATE_DIR") ?? "data";
        var logDir = Environment.GetEnvironmentVariable("LOG_DIR") ?? Path.Combine(stateDir, "logs");
        var heartbeatPath = Path.Combine(stateDir, "heartbeat");
        var appLogPath = Path.Combine(logDir, "app.log");

        Directory.CreateDirectory(stateDir);
        Directory.CreateDirectory(logDir);

        _ = await stateStore.GetOrCreateProjectAsync(Environment.CurrentDirectory, stoppingToken);

        var botToken = GetRequired("BOT_TOKEN");
        var allowedUserId = long.Parse(GetRequired("ALLOWED_USER_ID"));
        var groupChatId = long.Parse(GetRequired("GROUP_CHAT_ID"));

        var bot = new TelegramBotClient(botToken);
        var offset = 0;

        logger.LogInformation("Started telegram polling");
        await File.AppendAllTextAsync(appLogPath, $"{DateTimeOffset.UtcNow:O} Started{Environment.NewLine}", stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updates = await bot.GetUpdates(
                    offset: offset,
                    timeout: 30,
                    allowedUpdates: [UpdateType.Message, UpdateType.CallbackQuery],
                    cancellationToken: stoppingToken);

                foreach (var update in updates)
                {
                    offset = update.Id + 1;
                    await HandleUpdateAsync(bot, update, allowedUserId, groupChatId, stoppingToken);
                }

                await File.WriteAllTextAsync(heartbeatPath, DateTimeOffset.UtcNow.ToString("O"), stoppingToken);
                await File.AppendAllTextAsync(appLogPath, $"{DateTimeOffset.UtcNow:O} Heartbeat{Environment.NewLine}", stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Polling loop failed");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private async Task HandleUpdateAsync(
        TelegramBotClient bot,
        Update update,
        long allowedUserId,
        long groupChatId,
        CancellationToken cancellationToken)
    {
        var fromUserId = update.Message?.From?.Id ?? update.CallbackQuery?.From.Id;
        var chatId = update.Message?.Chat.Id ?? update.CallbackQuery?.Message?.Chat.Id;
        var threadId = update.Message?.MessageThreadId ?? update.CallbackQuery?.Message?.MessageThreadId;

        logger.LogInformation(
            "Update {Type}; chatId={ChatId}; threadId={ThreadId}; fromUser={FromUser}",
            update.Type,
            chatId,
            threadId,
            fromUserId);

        if (fromUserId is null || fromUserId.Value != allowedUserId)
        {
            return;
        }

        if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery is not null)
        {
            await HandleCallbackAsync(bot, update.CallbackQuery, groupChatId, cancellationToken);
            return;
        }

        if (update.Type != UpdateType.Message || update.Message is null)
        {
            return;
        }

        var message = update.Message;
        if (message.Type != MessageType.Text || string.IsNullOrWhiteSpace(message.Text))
        {
            return;
        }

        var command = ParseCommand(message.Text);
        if (command == "/mkproj" && message.Chat.Type != ChatType.Private)
        {
            await HandleMkprojFromNonPrivateChatAsync(bot, message, groupChatId, cancellationToken);
            return;
        }

        if (message.Chat.Type == ChatType.Private)
        {
            await HandlePrivateMessageAsync(bot, message, groupChatId, cancellationToken);
            return;
        }

        if (message.Chat.Id == groupChatId && message.MessageThreadId.HasValue)
        {
            await HandleTopicMessageAsync(bot, message, cancellationToken);
        }
    }

    private async Task HandlePrivateMessageAsync(TelegramBotClient bot, Message message, long groupChatId, CancellationToken cancellationToken)
    {
        var command = ParseCommand(message.Text!);
        var userId = message.From!.Id;

        switch (command)
        {
            case "/help":
                await bot.SendMessage(
                    chatId: message.Chat.Id,
                    text: "Команды: /mkproj, /help.\n/mkproj открывает выбор директории и создаёт topic в группе.",
                    cancellationToken: cancellationToken);
                return;
            case "/mkproj":
                await StartMkprojSessionAsync(bot, message.Chat.Id, userId, groupChatId, cancellationToken);
                return;
        }

        if (_mkprojSessions.TryGetValue(userId, out var session))
        {
            if (session.InputMode == MkprojInputMode.AwaitingSearch)
            {
                session.Filter = message.Text!.Trim();
                session.InputMode = MkprojInputMode.None;
                session.Page = 0;
                await RenderSessionAsync(bot, session, cancellationToken);
                return;
            }

            if (session.InputMode == MkprojInputMode.AwaitingPath)
            {
                await HandleEnteredPathAsync(bot, session, message.Text!, cancellationToken);
                return;
            }
        }

        await bot.SendMessage(
            chatId: message.Chat.Id,
            text: "Поддерживаются /mkproj и /help.",
            cancellationToken: cancellationToken);
    }

    private async Task HandleMkprojFromNonPrivateChatAsync(
        TelegramBotClient bot,
        Message message,
        long groupChatId,
        CancellationToken cancellationToken)
    {
        var userId = message.From!.Id;
        try
        {
            await StartMkprojSessionAsync(bot, userId, userId, groupChatId, cancellationToken);
            await bot.SendMessage(
                chatId: message.Chat.Id,
                text: "Открыл выбор папки в личке с ботом.",
                messageThreadId: message.MessageThreadId,
                cancellationToken: cancellationToken);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 403)
        {
            await bot.SendMessage(
                chatId: message.Chat.Id,
                text: "Не могу написать в личку. Сначала открой чат с ботом и нажми Start, потом повтори /mkproj.",
                messageThreadId: message.MessageThreadId,
                cancellationToken: cancellationToken);
        }
    }

    private async Task HandleTopicMessageAsync(TelegramBotClient bot, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var threadId = message.MessageThreadId!.Value;
        var command = ParseCommand(message.Text!);

        switch (command)
        {
            case "/status":
                await SendTopicStatusAsync(bot, chatId, threadId, cancellationToken);
                return;
            case "/chats":
                await SendActiveChatsAsync(bot, chatId, threadId, cancellationToken);
                return;
            case "/cancel":
                await codexRunner.CancelAsync(chatId, threadId, cancellationToken);
                var topicToCancel = await stateStore.GetTopicByThreadIdAsync(chatId, threadId, cancellationToken);
                if (topicToCancel is not null)
                {
                    await stateStore.FinishTopicJobAsync(topicToCancel.Id, "cancelled", cancellationToken);
                    await RefreshTopicTitleAsync(bot, topicToCancel with { Busy = false, Status = "cancelled" }, cancellationToken, force: true);
                }

                _pendingApprovals.TryRemove((chatId, threadId), out _);
                await bot.SendMessage(chatId, "Cancelled by user.", messageThreadId: threadId, cancellationToken: cancellationToken);
                return;
            case "/new":
                await HandleNewTopicCommandAsync(bot, chatId, threadId, cancellationToken);
                return;
            case "/backend":
                await HandleBackendCommandAsync(bot, chatId, threadId, message.Text!, cancellationToken);
                return;
            case "/delete":
                await DeleteCurrentTopicAsync(bot, chatId, threadId, cancellationToken);
                return;
        }

        LaunchTopicJob(bot, chatId, threadId, message.Text!, RunLaunchMode.Normal, cancellationToken);
    }

    private async Task HandleNewTopicCommandAsync(TelegramBotClient bot, long chatId, int threadId, CancellationToken cancellationToken)
    {
        var sourceTopic = await stateStore.GetTopicByThreadIdAsync(chatId, threadId, cancellationToken);
        if (sourceTopic is null)
        {
            await bot.SendMessage(chatId, "Топик не привязан к проекту.", messageThreadId: threadId, cancellationToken: cancellationToken);
            return;
        }

        if (sourceTopic.Busy)
        {
            await bot.SendMessage(chatId, "⏳ Нельзя выполнить /new, пока текущий topic занят. Доступна команда /cancel.", messageThreadId: threadId, cancellationToken: cancellationToken);
            return;
        }

        var project = await stateStore.GetProjectByIdAsync(sourceTopic.ProjectId, cancellationToken);
        if (project is null)
        {
            await bot.SendMessage(chatId, "Ошибка: проект не найден в БД.", messageThreadId: threadId, cancellationToken: cancellationToken);
            return;
        }

        var newTopic = await CreateTopicForDirectoryAsync(bot, chatId, project.DirPath, cancellationToken);

        await bot.SendMessage(
            chatId,
            $"🆕 Создан новый topic (thread {newTopic.MessageThreadId}). Инициализирую новую сессию Codex...",
            messageThreadId: threadId,
            cancellationToken: cancellationToken);

        LaunchTopicJob(bot, chatId, newTopic.MessageThreadId, "/new", RunLaunchMode.Normal, cancellationToken);
    }

    private void LaunchTopicJob(TelegramBotClient bot, long chatId, int threadId, string prompt, RunLaunchMode launchMode, CancellationToken cancellationToken)
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await RunTopicJobAsync(bot, chatId, threadId, prompt, launchMode, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Background topic job failed; chatId={ChatId}, threadId={ThreadId}", chatId, threadId);
                }
            },
            CancellationToken.None);
    }

    private async Task SendActiveChatsAsync(TelegramBotClient bot, long chatId, int threadId, CancellationToken cancellationToken)
    {
        var runs = codexRunner.GetActiveRuns();
        if (runs.Count == 0)
        {
            await bot.SendMessage(chatId, "Сейчас нет активных codex-чатов.", messageThreadId: threadId, cancellationToken: cancellationToken);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var lines = runs.Select((run, index) =>
        {
            var elapsed = now - run.StartedAt;
            var prompt = run.Prompt.Length > 60 ? $"{run.Prompt[..60]}..." : run.Prompt;
            return $"{index + 1}. thread={run.MessageThreadId}, started={run.StartedAt:O}, elapsed={elapsed:hh\\:mm\\:ss}, prompt={prompt}";
        });

        var text = "Активные codex-рансы:\n" + string.Join('\n', lines);
        await bot.SendMessage(chatId, EscapeTelegramHtml(text), parseMode: ParseMode.Html, messageThreadId: threadId, cancellationToken: cancellationToken);
    }

    private async Task DeleteCurrentTopicAsync(TelegramBotClient bot, long chatId, int threadId, CancellationToken cancellationToken)
    {
        var key = (chatId, threadId);
        if (_pendingDeleteConfirmations.TryRemove(key, out var oldMessageId))
        {
            try
            {
                await bot.EditMessageReplyMarkup(chatId, oldMessageId, replyMarkup: null, cancellationToken: cancellationToken);
            }
            catch (ApiRequestException)
            {
                // Ignore stale confirmations.
            }
        }

        var confirmation = await bot.SendMessage(
            chatId,
            "⚠️ Точно удаляем этот topic?",
            messageThreadId: threadId,
            replyMarkup: new InlineKeyboardMarkup(
            [
                [
                    InlineKeyboardButton.WithCallbackData("Да, удалить", "del:yes"),
                    InlineKeyboardButton.WithCallbackData("Отмена", "del:no")
                ]
            ]),
            cancellationToken: cancellationToken);

        _pendingDeleteConfirmations[key] = confirmation.Id;
    }

    private async Task SendTopicStatusAsync(TelegramBotClient bot, long chatId, int threadId, CancellationToken cancellationToken)
    {
        var topic = await stateStore.GetTopicByThreadIdAsync(chatId, threadId, cancellationToken);
        if (topic is null)
        {
            await bot.SendMessage(chatId, "Топик не привязан к проекту.", messageThreadId: threadId, cancellationToken: cancellationToken);
            return;
        }

        var contextPart = topic.ContextLeftPercent.HasValue
            ? $"{topic.ContextLeftPercent.Value}%"
            : "n/a";
        var status = $"busy={topic.Busy}, status={topic.Status}, backend={topic.LaunchBackend}, context={contextPart}";
        await bot.SendMessage(chatId, $"📊 {status}", messageThreadId: threadId, cancellationToken: cancellationToken);
    }

    private async Task HandleBackendCommandAsync(
        TelegramBotClient bot,
        long chatId,
        int threadId,
        string rawText,
        CancellationToken cancellationToken)
    {
        var topic = await stateStore.GetTopicByThreadIdAsync(chatId, threadId, cancellationToken);
        if (topic is null)
        {
            await bot.SendMessage(chatId, "Топик не привязан к проекту.", messageThreadId: threadId, cancellationToken: cancellationToken);
            return;
        }

        var parts = rawText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            await bot.SendMessage(
                chatId,
                $"Текущий backend: <b>{EscapeTelegramHtml(topic.LaunchBackend)}</b>\nИспользование: <code>/backend docker</code> | <code>/backend windows</code> | <code>/backend wsl</code>",
                parseMode: ParseMode.Html,
                messageThreadId: threadId,
                cancellationToken: cancellationToken);
            return;
        }

        if (topic.Busy)
        {
            await bot.SendMessage(chatId, "Нельзя менять backend во время активной задачи.", messageThreadId: threadId, cancellationToken: cancellationToken);
            return;
        }

        var requested = parts[1].Trim();
        if (!CodexLaunchBackend.IsSupported(requested))
        {
            await bot.SendMessage(chatId, "Поддерживаются только: docker, windows, wsl.", messageThreadId: threadId, cancellationToken: cancellationToken);
            return;
        }

        var normalized = CodexLaunchBackend.NormalizeOrDefault(requested);
        await stateStore.UpdateTopicLaunchBackendAsync(topic.Id, normalized, cancellationToken);
        await bot.SendMessage(
            chatId,
            $"Backend переключен на: <b>{EscapeTelegramHtml(normalized)}</b>",
            parseMode: ParseMode.Html,
            messageThreadId: threadId,
            cancellationToken: cancellationToken);
    }

    private async Task RunTopicJobAsync(
        TelegramBotClient bot,
        long chatId,
        int threadId,
        string prompt,
        RunLaunchMode launchMode,
        CancellationToken cancellationToken)
    {
        var topic = await stateStore.GetTopicByThreadIdAsync(chatId, threadId, cancellationToken);
        if (topic is null)
        {
            await bot.SendMessage(chatId, "Топик не привязан к проекту. Создайте проект через /mkproj в личке.", messageThreadId: threadId, cancellationToken: cancellationToken);
            return;
        }

        if (topic.Busy)
        {
            await bot.SendMessage(chatId, "⏳ Сообщение проигнорировано: выполняется задача. Доступна команда /cancel.", messageThreadId: threadId, cancellationToken: cancellationToken);
            return;
        }

        var project = await stateStore.GetProjectByIdAsync(topic.ProjectId, cancellationToken);
        if (project is null)
        {
            await bot.SendMessage(chatId, "Ошибка: проект не найден в БД.", messageThreadId: threadId, cancellationToken: cancellationToken);
            return;
        }

        await stateStore.StartTopicJobAsync(topic.Id, cancellationToken);
        await RefreshTopicTitleAsync(bot, topic with { Busy = true, Status = "working" }, cancellationToken, force: true);
        var keepPendingApproval = false;

        if (!string.IsNullOrWhiteSpace(topic.CodexChatId) && !IsLikelyCodexChatId(topic.CodexChatId))
        {
            topic = topic with { CodexChatId = null };
            await stateStore.UpdateTopicCodexChatIdAsync(topic.Id, null, cancellationToken);
        }

        try
        {
            var sandboxMode = Environment.GetEnvironmentVariable("CODEX_SANDBOX_MODE") ?? "workspace-write";

            var request = new CodexRunRequest(
                chatId,
                threadId,
                project.DirPath,
                prompt,
                topic.CodexChatId,
                topic.LaunchBackend,
                SandboxModeOverride: sandboxMode,
                StopOnCommandStart: false);
            await foreach (var update in codexRunner.RunAsync(request, cancellationToken))
            {
                logger.LogInformation(
                    "Codex update; chatId={ChatId}, threadId={ThreadId}, kind={Kind}, size={Size}",
                    chatId,
                    threadId,
                    update.Kind,
                    update.Chunk?.Length ?? 0);

                var chunk = update.Chunk ?? string.Empty;
                var parsedCodexChatId = update.CodexChatId;

                if (!string.IsNullOrWhiteSpace(parsedCodexChatId) &&
                    IsLikelyCodexChatId(parsedCodexChatId) &&
                    !string.Equals(parsedCodexChatId, topic.CodexChatId, StringComparison.Ordinal))
                {
                    topic = topic with { CodexChatId = parsedCodexChatId };
                    await stateStore.UpdateTopicCodexChatIdAsync(topic.Id, parsedCodexChatId, cancellationToken);
                    logger.LogInformation(
                        "Stored codex_chat_id; chatId={ChatId}, threadId={ThreadId}, codexChatId={CodexChatId}",
                        chatId,
                        threadId,
                        parsedCodexChatId);
                }

                var contextLeftPercent = ParseContextLeftPercent(chunk);
                if (contextLeftPercent.HasValue && contextLeftPercent != topic.ContextLeftPercent)
                {
                    topic = topic with { ContextLeftPercent = contextLeftPercent };
                    await stateStore.UpdateTopicContextLeftAsync(topic.Id, contextLeftPercent, cancellationToken);
                    await RefreshTopicTitleAsync(bot, topic with { Busy = true, Status = "working" }, cancellationToken);
                }

                if (string.IsNullOrWhiteSpace(chunk))
                {
                    continue;
                }

                if (string.Equals(update.Kind, "status", StringComparison.OrdinalIgnoreCase) &&
                    IsContextOnlyStatus(chunk))
                {
                    continue;
                }

                if (TryExtractApprovalCommand(chunk, out var approvalCommand))
                {
                    await stateStore.UpdateTopicStatusAsync(topic.Id, "waiting_approval", cancellationToken);
                    topic = topic with { Status = "waiting_approval" };
                    await RefreshTopicTitleAsync(bot, topic with { Busy = true }, cancellationToken, force: true);
                    await ShowApprovalPromptAsync(bot, chatId, threadId, approvalCommand, prompt, isNativeInput: true, cancellationToken);
                    keepPendingApproval = true;
                    continue;
                }

                await SendFormattedOutputAsync(bot, chatId, threadId, update.Kind, chunk, cancellationToken);
            }
            await stateStore.FinishTopicJobAsync(topic.Id, "idle", cancellationToken);
            topic = topic with { Busy = false, Status = "idle" };
            await RefreshTopicTitleAsync(bot, topic, cancellationToken, force: true);
        }
        catch (CodexApprovalRequiredException ex)
        {
            await stateStore.FinishTopicJobAsync(topic.Id, "waiting_approval", cancellationToken);
            topic = topic with { Busy = false, Status = "waiting_approval" };
            await ShowApprovalPromptAsync(bot, chatId, threadId, ex.Command, prompt, isNativeInput: true, cancellationToken);
            keepPendingApproval = true;
        }
        catch (OperationCanceledException)
        {
            await stateStore.FinishTopicJobAsync(topic.Id, "cancelled", cancellationToken);
            topic = topic with { Busy = false, Status = "cancelled" };
            await RefreshTopicTitleAsync(bot, topic, cancellationToken, force: true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Topic job failed; chatId={ChatId}, threadId={ThreadId}", chatId, threadId);
            await stateStore.FinishTopicJobAsync(topic.Id, "error", cancellationToken);
            topic = topic with { Busy = false, Status = "error" };
            await RefreshTopicTitleAsync(bot, topic, cancellationToken, force: true);
            await SendFormattedOutputAsync(bot, chatId, threadId, "status", $"Error: {ex.Message}", cancellationToken);
        }
        finally
        {
            if (!keepPendingApproval)
            {
                _pendingApprovals.TryRemove((chatId, threadId), out _);
            }
        }
    }

    private async Task StartMkprojSessionAsync(
        TelegramBotClient bot,
        long privateChatId,
        long userId,
        long groupChatId,
        CancellationToken cancellationToken)
    {
        var startDirectory = GetInitialDirectory();
        var session = new MkprojSession(userId, privateChatId, groupChatId)
        {
            CurrentDirectory = startDirectory
        };

        var view = BuildMkprojView(session);
        var message = await bot.SendMessage(privateChatId, view.Text, replyMarkup: view.Keyboard, cancellationToken: cancellationToken);
        session.MessageId = message.Id;
        _mkprojSessions[userId] = session;
    }

    private async Task HandleCallbackAsync(TelegramBotClient bot, CallbackQuery callbackQuery, long groupChatId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(callbackQuery.Data) || callbackQuery.Message is null)
        {
            await bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
            return;
        }

        if (callbackQuery.Data.StartsWith("appr:", StringComparison.Ordinal))
        {
            await HandleApprovalCallbackAsync(bot, callbackQuery, cancellationToken);
            return;
        }

        if (callbackQuery.Data.StartsWith("del:", StringComparison.Ordinal))
        {
            await HandleDeleteCallbackAsync(bot, callbackQuery, cancellationToken);
            return;
        }

        if (callbackQuery.Message.Chat.Type != ChatType.Private)
        {
            await bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
            return;
        }

        var userId = callbackQuery.From.Id;
        if (!_mkprojSessions.TryGetValue(userId, out var session))
        {
            await bot.AnswerCallbackQuery(callbackQuery.Id, "Сессия не найдена. Введите /mkproj снова.", cancellationToken: cancellationToken);
            return;
        }

        session.GroupChatId = groupChatId;

        var data = callbackQuery.Data!;
        if (data.StartsWith("mk:cd:", StringComparison.Ordinal))
        {
            if (int.TryParse(data["mk:cd:".Length..], out var index))
            {
                var entries = ListEntries(session);
                if (index >= 0 && index < entries.Count)
                {
                    session.CurrentDirectory = entries[index].FullPath;
                    session.Page = 0;
                }
            }
        }
        else if (data == "mk:up")
        {
            GoUp(session);
        }
        else if (data == "mk:prev")
        {
            if (session.Page > 0)
            {
                session.Page--;
            }
        }
        else if (data == "mk:next")
        {
            var entries = ListEntries(session);
            var totalPages = Math.Max(1, (int)Math.Ceiling(entries.Count / (double)PageSize));
            if (session.Page + 1 < totalPages)
            {
                session.Page++;
            }
        }
        else if (data == "mk:search")
        {
            session.InputMode = MkprojInputMode.AwaitingSearch;
        }
        else if (data == "mk:path")
        {
            session.InputMode = MkprojInputMode.AwaitingPath;
        }
        else if (data == "mk:select")
        {
            await bot.AnswerCallbackQuery(callbackQuery.Id, "Создаю проект...", cancellationToken: cancellationToken);
            await CompleteSelectionAsync(bot, session, cancellationToken);
            return;
        }

        await RenderSessionAsync(bot, session, cancellationToken);
        await bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
    }

    private async Task HandleApprovalCallbackAsync(TelegramBotClient bot, CallbackQuery callbackQuery, CancellationToken cancellationToken)
    {
        var threadId = callbackQuery.Message?.MessageThreadId;
        if (!threadId.HasValue)
        {
            await bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
            return;
        }

        var key = (ChatId: callbackQuery.Message!.Chat.Id, ThreadId: threadId.Value);
        if (!_pendingApprovals.TryRemove(key, out var pending))
        {
            await bot.AnswerCallbackQuery(callbackQuery.Id, "Approval is not active.", cancellationToken: cancellationToken);
            return;
        }

        var choice = callbackQuery.Data!["appr:".Length..];
        var label = choice switch
        {
            "y" => "Yes (y)",
            "p" => "Yes (y)",
            "n" => "No (esc)",
            _ => "Yes (y)"
        };

        try
        {
            await bot.EditMessageText(
                key.ChatId,
                pending.MessageId,
                $"{pending.PromptHtml}\n\nSelected: <b>{EscapeTelegramHtml(label)}</b>",
                parseMode: ParseMode.Html,
                replyMarkup: null,
                cancellationToken: cancellationToken);
        }
        catch (ApiRequestException)
        {
            // Ignore stale approval prompt edits.
        }

        await bot.AnswerCallbackQuery(callbackQuery.Id, label, cancellationToken: cancellationToken);

        if (pending.IsNativeInput)
        {
            var input = choice == "n" ? "n\n" : "y\n";
            await codexRunner.SendInputAsync(key.ChatId, key.ThreadId, input, cancellationToken);
            if (choice == "n")
            {
                await bot.SendMessage(
                    key.ChatId,
                    "Команда отклонена пользователем.",
                    messageThreadId: key.ThreadId,
                    cancellationToken: cancellationToken);
            }

            return;
        }

        if (choice == "n")
        {
            await bot.SendMessage(
                key.ChatId,
                "Команда отклонена. Задача остановлена.",
                messageThreadId: key.ThreadId,
                cancellationToken: cancellationToken);
            return;
        }

        LaunchTopicJob(bot, key.ChatId, key.ThreadId, pending.OriginalPrompt, RunLaunchMode.Normal, cancellationToken);
    }

    private async Task HandleDeleteCallbackAsync(TelegramBotClient bot, CallbackQuery callbackQuery, CancellationToken cancellationToken)
    {
        var threadId = callbackQuery.Message?.MessageThreadId;
        if (!threadId.HasValue || callbackQuery.Message is null)
        {
            await bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
            return;
        }

        var key = (ChatId: callbackQuery.Message.Chat.Id, ThreadId: threadId.Value);
        if (!_pendingDeleteConfirmations.TryGetValue(key, out var expectedMessageId) || expectedMessageId != callbackQuery.Message.Id)
        {
            await bot.AnswerCallbackQuery(callbackQuery.Id, "Подтверждение удаления не активно.", cancellationToken: cancellationToken);
            return;
        }

        var action = callbackQuery.Data!["del:".Length..];
        if (action == "no")
        {
            _pendingDeleteConfirmations.TryRemove(key, out _);
            await bot.EditMessageText(
                key.ChatId,
                callbackQuery.Message.Id,
                "Удаление отменено.",
                replyMarkup: null,
                cancellationToken: cancellationToken);
            await bot.AnswerCallbackQuery(callbackQuery.Id, "Отмена", cancellationToken: cancellationToken);
            return;
        }

        _pendingDeleteConfirmations.TryRemove(key, out _);

        var topic = await stateStore.GetTopicByThreadIdAsync(key.ChatId, key.ThreadId, cancellationToken);
        if (topic is not null && topic.Busy)
        {
            await codexRunner.CancelAsync(key.ChatId, key.ThreadId, cancellationToken);
            await stateStore.FinishTopicJobAsync(topic.Id, "cancelled", cancellationToken);
        }

        _pendingApprovals.TryRemove(key, out _);

        try
        {
            await bot.DeleteForumTopic(key.ChatId, key.ThreadId, cancellationToken: cancellationToken);
        }
        catch (ApiRequestException ex)
        {
            logger.LogWarning(ex, "Failed to delete Telegram topic; chatId={ChatId}, threadId={ThreadId}", key.ChatId, key.ThreadId);
            await bot.EditMessageText(
                key.ChatId,
                callbackQuery.Message.Id,
                $"Не удалось удалить topic: {ex.Message}",
                replyMarkup: null,
                cancellationToken: cancellationToken);
            await bot.AnswerCallbackQuery(callbackQuery.Id, "Ошибка удаления", cancellationToken: cancellationToken);
            return;
        }

        if (topic is not null)
        {
            await stateStore.DeleteTopicAsync(topic.Id, cancellationToken);
        }

        await bot.AnswerCallbackQuery(callbackQuery.Id, "Topic удалён", cancellationToken: cancellationToken);
    }

    private async Task HandleEnteredPathAsync(TelegramBotClient bot, MkprojSession session, string rawPath, CancellationToken cancellationToken)
    {
        session.InputMode = MkprojInputMode.None;
        var candidatePath = rawPath.Trim();
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            await RenderSessionAsync(bot, session, cancellationToken);
            return;
        }

        try
        {
            var fullPath = ResolveUserSuppliedPath(candidatePath);
            if (!Directory.Exists(fullPath))
            {
                await bot.SendMessage(session.PrivateChatId, $"Папка не найдена: {fullPath}", cancellationToken: cancellationToken);
                await RenderSessionAsync(bot, session, cancellationToken);
                return;
            }

            session.CurrentDirectory = fullPath;
            session.Page = 0;
            await RenderSessionAsync(bot, session, cancellationToken);
        }
        catch (Exception ex)
        {
            await bot.SendMessage(session.PrivateChatId, $"Неверный путь: {ex.Message}", cancellationToken: cancellationToken);
            await RenderSessionAsync(bot, session, cancellationToken);
        }
    }

    private async Task CompleteSelectionAsync(TelegramBotClient bot, MkprojSession session, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.CurrentDirectory))
        {
            await bot.SendMessage(session.PrivateChatId, "Сначала выберите папку.", cancellationToken: cancellationToken);
            return;
        }

        var selected = session.CurrentDirectory!;
        if (!Directory.Exists(selected))
        {
            await bot.SendMessage(session.PrivateChatId, $"Папка недоступна: {selected}", cancellationToken: cancellationToken);
            return;
        }

        if (!pathPolicy.IsAllowed(selected))
        {
            await bot.SendMessage(session.PrivateChatId, $"Путь запрещён политикой: {selected}", cancellationToken: cancellationToken);
            return;
        }

        try
        {
            _ = Directory.EnumerateDirectories(selected).Take(1).ToList();
        }
        catch (Exception ex)
        {
            await bot.SendMessage(session.PrivateChatId, $"Нет доступа к папке: {ex.Message}", cancellationToken: cancellationToken);
            return;
        }

        await bot.EditMessageText(
            session.PrivateChatId,
            session.MessageId,
            $"Создаю проект для {selected}...",
            cancellationToken: cancellationToken);

        var topicRecord = await CreateTopicForDirectoryAsync(bot, session.GroupChatId, selected, cancellationToken);
        var projectName = Path.GetFileName(selected.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var confirmation = $"Готово: создан topic {projectName} (thread {topicRecord.MessageThreadId})";

        await bot.EditMessageText(
            session.PrivateChatId,
            session.MessageId,
            confirmation,
            cancellationToken: cancellationToken);

        _mkprojSessions.TryRemove(session.UserId, out _);
    }

    private async Task<TopicRecord> CreateTopicForDirectoryAsync(
        TelegramBotClient bot,
        long groupChatId,
        string selectedDirectory,
        CancellationToken cancellationToken)
    {
        var project = await stateStore.GetOrCreateProjectAsync(selectedDirectory, cancellationToken);
        var projectName = Path.GetFileName(selectedDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(projectName))
        {
            projectName = selectedDirectory;
        }

        var title = topicTitleFormatter.Format(projectName, selectedDirectory, isBusy: false, contextLeftPercent: null, status: "idle");
        var forumTopic = await bot.CreateForumTopic(groupChatId, title, cancellationToken: cancellationToken);

        var topic = await stateStore.CreateTopicAsync(
            project.Id,
            groupChatId,
            forumTopic.MessageThreadId,
            projectName,
            cancellationToken);

        await bot.SendMessage(
            groupChatId,
            "Привет! Этот topic привязан к директории проекта.\n" +
            "Пишите обычный текст для запуска задачи Codex.\n" +
            "Команды: /status, /backend, /chats, /cancel, /new, /delete",
            messageThreadId: topic.MessageThreadId,
            cancellationToken: cancellationToken);

        return topic;
    }

    private async Task RenderSessionAsync(TelegramBotClient bot, MkprojSession session, CancellationToken cancellationToken)
    {
        var view = BuildMkprojView(session);
        try
        {
            await bot.EditMessageText(
                session.PrivateChatId,
                session.MessageId,
                view.Text,
                replyMarkup: view.Keyboard,
                cancellationToken: cancellationToken);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 && ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
        {
            // User clicked a button that did not change state; nothing to update.
        }
    }

    private MkprojView BuildMkprojView(MkprojSession session)
    {
        var entries = ListEntries(session);
        var totalPages = Math.Max(1, (int)Math.Ceiling(entries.Count / (double)PageSize));
        if (session.Page >= totalPages)
        {
            session.Page = totalPages - 1;
        }

        var start = session.Page * PageSize;
        var pageEntries = entries.Skip(start).Take(PageSize).ToList();

        var rows = new List<InlineKeyboardButton[]>();
        foreach (var item in pageEntries)
        {
            rows.Add([InlineKeyboardButton.WithCallbackData($"📁 {item.Name}", $"mk:cd:{item.Index}")]);
        }

        rows.Add(
        [
            InlineKeyboardButton.WithCallbackData("⬆️ Up", "mk:up"),
            InlineKeyboardButton.WithCallbackData("◀️ Prev", "mk:prev"),
            InlineKeyboardButton.WithCallbackData("Next ▶️", "mk:next")
        ]);
        rows.Add(
        [
            InlineKeyboardButton.WithCallbackData("🔎 Search", "mk:search"),
            InlineKeyboardButton.WithCallbackData("⌨️ Enter path", "mk:path"),
            InlineKeyboardButton.WithCallbackData("✅ Select", "mk:select")
        ]);

        var text =
            "🗂 Выбор директории\n" +
            $"Текущая: {session.CurrentDirectory ?? "[Список дисков]"}\n" +
            $"Элементов: {entries.Count} (показано максимум {MaxListElements})\n" +
            $"Страница: {session.Page + 1}/{totalPages}\n" +
            $"Фильтр: {(string.IsNullOrWhiteSpace(session.Filter) ? "нет" : session.Filter)}\n" +
            (session.InputMode switch
            {
                MkprojInputMode.AwaitingSearch => "\nВведите текст поиска следующим сообщением.",
                MkprojInputMode.AwaitingPath => "\nВведите полный путь следующим сообщением.",
                _ => string.Empty
            });

        return new MkprojView(text, new InlineKeyboardMarkup(rows));
    }

    private List<MkprojEntry> ListEntries(MkprojSession session)
    {
        IEnumerable<string> candidates;

        if (string.IsNullOrWhiteSpace(session.CurrentDirectory))
        {
            candidates = DriveInfo.GetDrives()
                .Where(drive => drive.IsReady)
                .Select(drive => drive.RootDirectory.FullName);
        }
        else
        {
            try
            {
                candidates = Directory.EnumerateDirectories(session.CurrentDirectory);
            }
            catch (Exception)
            {
                candidates = [];
            }
        }

        var items = candidates
            .Select(path => new DirectoryInfo(path))
            .Where(info => !HiddenDirectoryNames.Contains(info.Name))
            .OrderBy(info => info.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxListElements)
            .ToList();

        if (!string.IsNullOrWhiteSpace(session.Filter))
        {
            items = items
                .Where(info => info.Name.Contains(session.Filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var result = new List<MkprojEntry>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var displayName = string.IsNullOrWhiteSpace(items[i].Name)
                ? items[i].FullName
                : items[i].Name;
            result.Add(new MkprojEntry(i, displayName, items[i].FullName));
        }

        return result;
    }

    private static void GoUp(MkprojSession session)
    {
        if (string.IsNullOrWhiteSpace(session.CurrentDirectory))
        {
            return;
        }

        var parent = Directory.GetParent(session.CurrentDirectory);
        session.CurrentDirectory = parent?.FullName;
        session.Page = 0;
    }

    private static IEnumerable<string> SplitForTelegram(string text, int limit = TelegramMessageLimit)
    {
        if (text.Length <= limit)
        {
            yield return text;
            yield break;
        }

        var start = 0;
        while (start < text.Length)
        {
            var length = Math.Min(limit, text.Length - start);
            yield return text.Substring(start, length);
            start += length;
        }
    }

    private static async Task SendFormattedOutputAsync(
        TelegramBotClient bot,
        long chatId,
        int threadId,
        string kind,
        string text,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var payload = text.TrimEnd();

        foreach (var chunk in SplitForTelegram(payload, 3200))
        {
            if (string.IsNullOrWhiteSpace(chunk))
            {
                continue;
            }

            var formatted = FormatTelegramOutput(kind, chunk);
            await bot.SendMessage(
                chatId,
                formatted,
                parseMode: ParseMode.Html,
                messageThreadId: threadId,
                cancellationToken: cancellationToken);
        }
    }

    private static string FormatTelegramOutput(string kind, string text)
    {
        return kind.ToLowerInvariant() switch
        {
            "reasoning" => $"<b>🧠 Размышление</b>\n{WrapExpandableQuote(EscapeTelegramHtml(text.TrimEnd()))}",
            "command_start" => WrapExpandableQuote($"<b>⚙️ Команда</b>\n<code>{EscapeTelegramHtml(text.TrimEnd())}</code>"),
            "command" => WrapExpandableQuote($"<b>⚙️ Выполнение</b>\n<pre><code>{EscapeTelegramHtml(text.TrimEnd())}</code></pre>"),
            "status" => FormatStatusOutput(text.TrimEnd()),
            _ => $"<b>🤖 Ответ</b>\n{RenderAnswerMarkdownAsHtml(text.TrimEnd())}"
        };
    }

    private static string FormatStatusOutput(string text)
    {
        if (text.StartsWith("🛠", StringComparison.Ordinal) || text.StartsWith("✅", StringComparison.Ordinal))
        {
            return $"<b>{EscapeTelegramHtml(text)}</b>";
        }

        if (string.Equals(text.Trim(), "Completed", StringComparison.OrdinalIgnoreCase))
        {
            return "<b>✅ Completed</b>";
        }

        return WrapExpandableQuote(EscapeTelegramHtml(text));
    }

    private static string WrapExpandableQuote(string htmlContent)
    {
        return $"<blockquote expandable>{htmlContent}</blockquote>";
    }

    private static string EscapeTelegramHtml(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }

    private static string RenderAnswerMarkdownAsHtml(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var inCodeBlock = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine ?? string.Empty;

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                if (!inCodeBlock)
                {
                    var lang = line.Length > 3 ? line[3..].Trim() : string.Empty;
                    if (string.IsNullOrWhiteSpace(lang))
                    {
                        sb.Append("<pre><code>");
                    }
                    else
                    {
                        sb.Append($"<pre><code class=\"language-{EscapeTelegramHtml(lang)}\">");
                    }

                    inCodeBlock = true;
                }
                else
                {
                    sb.Append("</code></pre>\n");
                    inCodeBlock = false;
                }

                continue;
            }

            if (inCodeBlock)
            {
                sb.Append(EscapeTelegramHtml(line)).Append('\n');
                continue;
            }

            sb.Append(RenderInlineMarkdownAsHtml(line)).Append('\n');
        }

        if (inCodeBlock)
        {
            sb.Append("</code></pre>");
        }

        return sb.ToString().TrimEnd();
    }

    private static string RenderInlineMarkdownAsHtml(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return string.Empty;
        }

        if (line.StartsWith("#", StringComparison.Ordinal))
        {
            var heading = line.TrimStart('#', ' ');
            return $"<b>{EscapeTelegramHtml(heading)}</b>";
        }

        var placeholders = new List<string>();
        var source = line;

        source = Regex.Replace(source, "`([^`\n]+)`", match =>
        {
            var token = $"\u0000{placeholders.Count}\u0000";
            placeholders.Add($"<code>{EscapeTelegramHtml(match.Groups[1].Value)}</code>");
            return token;
        });

        var escaped = EscapeTelegramHtml(source);
        escaped = Regex.Replace(escaped, @"\*\*(.+?)\*\*", "<b>$1</b>");
        escaped = Regex.Replace(escaped, @"\*(.+?)\*", "<i>$1</i>");

        for (var i = 0; i < placeholders.Count; i++)
        {
            escaped = escaped.Replace($"\u0000{i}\u0000", placeholders[i], StringComparison.Ordinal);
        }

        return escaped;
    }

    private async Task RefreshTopicTitleAsync(TelegramBotClient bot, TopicRecord topic, CancellationToken cancellationToken, bool force = false)
    {
        if (!force && !CanUpdateTopicTitleNow(topic.GroupChatId, topic.MessageThreadId))
        {
            return;
        }

        var project = await stateStore.GetProjectByIdAsync(topic.ProjectId, cancellationToken);
        if (project is null)
        {
            return;
        }

        var title = topicTitleFormatter.Format(topic.Name, project.DirPath, topic.Busy, topic.ContextLeftPercent, topic.Status);
        try
        {
            await bot.EditForumTopic(topic.GroupChatId, topic.MessageThreadId, title, cancellationToken: cancellationToken);
            _lastTitleUpdateAt[(topic.GroupChatId, topic.MessageThreadId)] = DateTimeOffset.UtcNow;
        }
        catch (ApiRequestException ex)
        {
            logger.LogWarning(ex, "Failed to update topic title; chatId={ChatId}, threadId={ThreadId}", topic.GroupChatId, topic.MessageThreadId);
        }
    }

    private bool CanUpdateTopicTitleNow(long chatId, int threadId)
    {
        var key = (chatId, threadId);
        if (!_lastTitleUpdateAt.TryGetValue(key, out var lastUpdate))
        {
            return true;
        }

        return DateTimeOffset.UtcNow - lastUpdate >= TitleUpdateDebounce;
    }

    private async Task ShowApprovalPromptAsync(
        TelegramBotClient bot,
        long chatId,
        int threadId,
        string command,
        string originalPrompt,
        bool isNativeInput,
        CancellationToken cancellationToken)
    {
        var promptHtml =
            "<b>Would you like to run the following command?</b>\n" +
            $"<blockquote expandable><code>$ {EscapeTelegramHtml(command)}</code></blockquote>";

        if (_pendingApprovals.TryGetValue((chatId, threadId), out var existing))
        {
            try
            {
                await bot.EditMessageText(
                    chatId,
                    existing.MessageId,
                    promptHtml,
                    parseMode: ParseMode.Html,
                    replyMarkup: BuildApprovalKeyboard(),
                    cancellationToken: cancellationToken);

                _pendingApprovals[(chatId, threadId)] = existing with { PromptHtml = promptHtml, Command = command, OriginalPrompt = originalPrompt, IsNativeInput = isNativeInput };
                return;
            }
            catch (ApiRequestException)
            {
                _pendingApprovals.TryRemove((chatId, threadId), out _);
            }
        }

        var message = await bot.SendMessage(
            chatId,
            promptHtml,
            parseMode: ParseMode.Html,
            messageThreadId: threadId,
            replyMarkup: BuildApprovalKeyboard(),
            cancellationToken: cancellationToken);

        _pendingApprovals[(chatId, threadId)] = new PendingApproval(message.Id, promptHtml, command, originalPrompt, isNativeInput);
    }

    private static InlineKeyboardMarkup BuildApprovalKeyboard()
    {
        return new InlineKeyboardMarkup(
        [
            [
                InlineKeyboardButton.WithCallbackData("Yes (y)", "appr:y"),
                InlineKeyboardButton.WithCallbackData("No (esc)", "appr:n")
            ]
        ]);
    }

    private static bool TryExtractApprovalCommand(string text, out string command)
    {
        command = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var looksLikeApproval = ApprovalPromptMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
        if (!looksLikeApproval)
        {
            return false;
        }

        var fencedMatch = ApprovalFencedCommandRegex.Match(text);
        if (fencedMatch.Success)
        {
            command = fencedMatch.Groups["cmd"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(command))
            {
                return true;
            }
        }

        var match = ApprovalCommandRegex.Match(text);
        if (!match.Success)
        {
            return false;
        }

        command = match.Groups["cmd"].Value.Trim();
        return !string.IsNullOrWhiteSpace(command);
    }

    private static int? ParseContextLeftPercent(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = ContextLeftRegex.Match(text);
        if (!match.Success)
        {
            match = ContextLeftReverseRegex.Match(text);
        }
        if (!match.Success)
        {
            match = ContextLeftAssignmentRegex.Match(text);
        }

        if (!match.Success || !int.TryParse(match.Groups["percent"].Value, out var percent))
        {
            return null;
        }

        return Math.Clamp(percent, 0, 100);
    }

    private static bool IsSandboxDeniedText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return SandboxDeniedRegex.IsMatch(text);
    }

    private static bool IsContextOnlyStatus(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith("Tokens:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return trimmed.StartsWith("Context left:", StringComparison.OrdinalIgnoreCase) ||
               ParseContextLeftPercent(trimmed).HasValue;
    }

    private static bool IsLikelyCodexChatId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length < 12)
        {
            return false;
        }

        if (trimmed.All(char.IsDigit))
        {
            return false;
        }

        return trimmed.Contains('-', StringComparison.Ordinal) || trimmed.Contains('_', StringComparison.Ordinal);
    }

    private static string? ParseCommand(string text)
    {
        var firstToken = text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstToken) || !firstToken.StartsWith('/'))
        {
            return null;
        }

        var atIndex = firstToken.IndexOf('@', StringComparison.Ordinal);
        return atIndex > 0 ? firstToken[..atIndex] : firstToken;
    }

    private static string? GetInitialDirectory()
    {
        const string hostRoot = "/host";
        if (Directory.Exists(hostRoot))
        {
            return hostRoot;
        }

        if (OperatingSystem.IsWindows())
        {
            var firstDrive = DriveInfo.GetDrives().FirstOrDefault(drive => drive.IsReady);
            return firstDrive?.RootDirectory.FullName;
        }

        return "/";
    }

    private static string ResolveUserSuppliedPath(string candidatePath)
    {
        var trimmed = candidatePath.Trim().Trim('"');

        // In Linux container mode support host-style Windows paths like C:\repo\proj.
        if (!OperatingSystem.IsWindows() && TryMapWindowsPathToHostPath(trimmed, out var mapped))
        {
            return mapped;
        }

        return Path.GetFullPath(trimmed);
    }

    private static bool TryMapWindowsPathToHostPath(string input, out string mappedPath)
    {
        mappedPath = string.Empty;
        if (input.Length < 3)
        {
            return false;
        }

        if (!char.IsAsciiLetter(input[0]) || input[1] != ':' || (input[2] != '\\' && input[2] != '/'))
        {
            return false;
        }

        var drive = char.ToUpperInvariant(input[0]);
        var remainder = input[3..].Replace('\\', '/').TrimStart('/');
        mappedPath = string.IsNullOrWhiteSpace(remainder)
            ? $"/host/{drive}"
            : $"/host/{drive}/{remainder}";

        return true;
    }

    private static string GetRequired(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Environment variable '{name}' is required.");
        }

        return value;
    }

    private sealed class MkprojSession(long userId, long privateChatId, long groupChatId)
    {
        public long UserId { get; } = userId;
        public long PrivateChatId { get; } = privateChatId;
        public long GroupChatId { get; set; } = groupChatId;
        public int MessageId { get; set; }
        public string? CurrentDirectory { get; set; }
        public int Page { get; set; }
        public string? Filter { get; set; }
        public MkprojInputMode InputMode { get; set; }
    }

    private sealed record PendingApproval(int MessageId, string PromptHtml, string Command, string OriginalPrompt, bool IsNativeInput);
    private sealed record MkprojEntry(int Index, string Name, string FullPath);
    private sealed record MkprojView(string Text, InlineKeyboardMarkup Keyboard);

    private enum RunLaunchMode
    {
        Normal
    }

    private enum MkprojInputMode
    {
        None,
        AwaitingSearch,
        AwaitingPath
    }
}


