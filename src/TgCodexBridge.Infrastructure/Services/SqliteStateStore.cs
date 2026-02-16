using Microsoft.Data.Sqlite;
using TgCodexBridge.Core.Abstractions;
using TgCodexBridge.Core.Models;

namespace TgCodexBridge.Infrastructure.Services;

public sealed class SqliteStateStore : IStateStore
{
    private const int CurrentSchemaVersion = 1;
    private static readonly byte[] SqliteHeader = "SQLite format 3\0"u8.ToArray();

    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private volatile bool _isInitialized;

    public SqliteStateStore(string stateDir)
    {
        Directory.CreateDirectory(stateDir);
        _dbPath = Path.Combine(stateDir, "state.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
    }

    public async Task<ProjectRecord> GetOrCreateProjectAsync(string dirPath, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var normalizedPath = Path.GetFullPath(dirPath);
        await using var connection = await OpenConnectionAsync(cancellationToken);

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT OR IGNORE INTO projects(dir_path, created_at)
                VALUES ($dirPath, $createdAt);
                """;
            insert.Parameters.AddWithValue("$dirPath", normalizedPath);
            insert.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT id, dir_path, created_at
            FROM projects
            WHERE dir_path = $dirPath;
            """;
        select.Parameters.AddWithValue("$dirPath", normalizedPath);

        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Project record was not found after insert.");
        }

        return new ProjectRecord(
            Id: reader.GetInt64(0),
            DirPath: reader.GetString(1),
            CreatedAt: DateTimeOffset.Parse(reader.GetString(2)));
    }

    public async Task<TopicRecord> CreateTopicAsync(long projectId, long groupChatId, int threadId, string name, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT OR IGNORE INTO topics(
                    project_id,
                    group_chat_id,
                    message_thread_id,
                    codex_chat_id,
                    name,
                    busy,
                    status,
                    context_left_percent,
                    last_job_started_at,
                    last_job_finished_at)
                VALUES ($projectId, $groupChatId, $threadId, NULL, $name, 0, 'idle', NULL, NULL, NULL);
                """;
            insert.Parameters.AddWithValue("$projectId", projectId);
            insert.Parameters.AddWithValue("$groupChatId", groupChatId);
            insert.Parameters.AddWithValue("$threadId", threadId);
            insert.Parameters.AddWithValue("$name", name);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        var topic = await GetTopicByThreadIdAsync(groupChatId, threadId, cancellationToken);
        return topic ?? throw new InvalidOperationException("Topic record was not found after insert.");
    }

    public async Task<TopicRecord?> GetTopicByThreadIdAsync(long groupChatId, int threadId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,
                   project_id,
                   group_chat_id,
                   message_thread_id,
                   codex_chat_id,
                   name,
                   busy,
                   status,
                   context_left_percent,
                   last_job_started_at,
                   last_job_finished_at
            FROM topics
            WHERE group_chat_id = $groupChatId AND message_thread_id = $threadId;
            """;
        command.Parameters.AddWithValue("$groupChatId", groupChatId);
        command.Parameters.AddWithValue("$threadId", threadId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapTopic(reader) : null;
    }

    public async Task SetTopicBusyAsync(long topicId, bool busy, CancellationToken cancellationToken = default)
    {
        await ExecuteTopicUpdateAsync(topicId, "UPDATE topics SET busy = $busy WHERE id = $topicId;", cancellationToken, ("$busy", busy ? 1 : 0));
    }

    public async Task UpdateTopicStatusAsync(long topicId, string status, CancellationToken cancellationToken = default)
    {
        await ExecuteTopicUpdateAsync(topicId, "UPDATE topics SET status = $status WHERE id = $topicId;", cancellationToken, ("$status", status));
    }

    public async Task UpdateTopicContextLeftAsync(long topicId, int? percent, CancellationToken cancellationToken = default)
    {
        await ExecuteTopicUpdateAsync(topicId, "UPDATE topics SET context_left_percent = $percent WHERE id = $topicId;", cancellationToken, ("$percent", percent));
    }

    public async Task UpdateTopicCodexChatIdAsync(long topicId, string? codexChatId, CancellationToken cancellationToken = default)
    {
        await ExecuteTopicUpdateAsync(topicId, "UPDATE topics SET codex_chat_id = $codexChatId WHERE id = $topicId;", cancellationToken, ("$codexChatId", codexChatId));
    }

    public async Task<IReadOnlyList<long>> ListNotifyUsersAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var result = new List<long>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT user_id FROM notify_users ORDER BY id;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetInt64(0));
        }

        return result;
    }

    public async Task StartTopicJobAsync(long topicId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var tx = connection.BeginTransaction();

        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            UPDATE topics
            SET busy = 1,
                status = 'working',
                last_job_started_at = $startedAt,
                last_job_finished_at = NULL
            WHERE id = $topicId;
            """;
        command.Parameters.AddWithValue("$startedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$topicId", topicId);
        await command.ExecuteNonQueryAsync(cancellationToken);

        tx.Commit();
    }

    public async Task FinishTopicJobAsync(long topicId, string finalStatus, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var tx = connection.BeginTransaction();

        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            UPDATE topics
            SET busy = 0,
                status = $finalStatus,
                last_job_finished_at = $finishedAt
            WHERE id = $topicId;
            """;
        command.Parameters.AddWithValue("$finalStatus", finalStatus);
        command.Parameters.AddWithValue("$finishedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$topicId", topicId);
        await command.ExecuteNonQueryAsync(cancellationToken);

        tx.Commit();
    }

    private async Task ExecuteTopicUpdateAsync(long topicId, string commandText, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Parameters.AddWithValue("$topicId", topicId);

        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_isInitialized)
        {
            return;
        }

        await _initGate.WaitAsync(cancellationToken);
        try
        {
            if (_isInitialized)
            {
                return;
            }

            EnsureValidDatabaseFile();

            await using var connection = await OpenConnectionAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    CREATE TABLE IF NOT EXISTS schema_version (
                        version INTEGER NOT NULL
                    );

                    INSERT INTO schema_version(version)
                    SELECT 0
                    WHERE NOT EXISTS (SELECT 1 FROM schema_version);
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            var version = 0;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT version FROM schema_version LIMIT 1;";
                var raw = await command.ExecuteScalarAsync(cancellationToken);
                version = Convert.ToInt32(raw);
            }

            if (version < 1)
            {
                await using var migration = connection.CreateCommand();
                migration.Transaction = transaction;
                migration.CommandText = """
                    CREATE TABLE IF NOT EXISTS projects (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        dir_path TEXT NOT NULL UNIQUE,
                        created_at TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS topics (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        project_id INTEGER NOT NULL,
                        group_chat_id INTEGER NOT NULL,
                        message_thread_id INTEGER NOT NULL UNIQUE,
                        codex_chat_id TEXT NULL,
                        name TEXT NOT NULL,
                        busy INTEGER NOT NULL DEFAULT 0,
                        status TEXT NOT NULL DEFAULT 'idle',
                        context_left_percent INTEGER NULL,
                        last_job_started_at TEXT NULL,
                        last_job_finished_at TEXT NULL,
                        FOREIGN KEY(project_id) REFERENCES projects(id)
                    );

                    CREATE TABLE IF NOT EXISTS notify_users (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        user_id INTEGER NOT NULL UNIQUE
                    );

                    CREATE TABLE IF NOT EXISTS audit_log (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        ts TEXT NOT NULL,
                        type TEXT NOT NULL,
                        payload_json TEXT NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS idx_topics_project_id ON topics(project_id);
                    CREATE INDEX IF NOT EXISTS idx_topics_message_thread_id ON topics(message_thread_id);

                    UPDATE schema_version SET version = 1;
                    """;
                await migration.ExecuteNonQueryAsync(cancellationToken);
            }

            transaction.Commit();
            _isInitialized = true;
        }
        finally
        {
            _initGate.Release();
        }
    }

    private void EnsureValidDatabaseFile()
    {
        if (!File.Exists(_dbPath))
        {
            return;
        }

        var fileInfo = new FileInfo(_dbPath);
        if (fileInfo.Length == 0)
        {
            return;
        }

        var isCorrupt = false;
        using (var stream = File.Open(_dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            if (stream.Length < SqliteHeader.Length)
            {
                isCorrupt = true;
            }
            else
            {
                Span<byte> header = stackalloc byte[16];
                _ = stream.Read(header);
                isCorrupt = !header.SequenceEqual(SqliteHeader);
            }
        }

        if (isCorrupt)
        {
            MoveCorruptDbAside();
        }
    }

    private void MoveCorruptDbAside()
    {
        try
        {
            var corruptPath = $"{_dbPath}.corrupt.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            if (File.Exists(corruptPath))
            {
                File.Delete(corruptPath);
            }

            File.Move(_dbPath, corruptPath);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"Detected corrupt SQLite file at '{_dbPath}', but it is locked by another process. " +
                "Stop running bot processes and remove or rename state.db manually, then start again.",
                ex);
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static TopicRecord MapTopic(SqliteDataReader reader)
    {
        return new TopicRecord(
            Id: reader.GetInt64(0),
            ProjectId: reader.GetInt64(1),
            GroupChatId: reader.GetInt64(2),
            MessageThreadId: reader.GetInt32(3),
            CodexChatId: reader.IsDBNull(4) ? null : reader.GetString(4),
            Name: reader.GetString(5),
            Busy: reader.GetInt32(6) == 1,
            Status: reader.GetString(7),
            ContextLeftPercent: reader.IsDBNull(8) ? null : reader.GetInt32(8),
            LastJobStartedAt: reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)),
            LastJobFinishedAt: reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10)));
    }
}
