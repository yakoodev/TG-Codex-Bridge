using Microsoft.Data.Sqlite;
using TgCodexBridge.Infrastructure.Services;

namespace TgCodexBridge.Tests;

public sealed class StateStoreTests
{
    [Fact]
    public void TopicTitleFormatter_ProducesExpectedShape()
    {
        var formatter = new DefaultTopicTitleFormatter();

        var idle = formatter.Format("proj", "C:/work/repos/proj", isBusy: false, contextLeftPercent: 77, status: "idle");
        var working = formatter.Format("proj", "C:/work/repos/proj", isBusy: true, contextLeftPercent: 55, status: "working");
        var cancelled = formatter.Format("proj", "C:/work/repos/proj", isBusy: false, contextLeftPercent: 11, status: "cancelled");

        Assert.Equal("🟢 proj · 77% · repos/proj", idle);
        Assert.Equal("🟡 proj · 55% · repos/proj", working);
        Assert.Equal("🔴 proj · 11% · repos/proj", cancelled);
    }

    [Fact]
    public void TopicTitleFormatter_TruncatesToTelegramSafeLength()
    {
        var formatter = new DefaultTopicTitleFormatter();

        var title = formatter.Format(new string('a', 200), "C:/very/long/path/for/testing", isBusy: false, contextLeftPercent: null, status: "idle");

        Assert.True(title.Length < 120);
    }

    [Fact]
    public async Task SqliteSchemaIsCreatedAndCanBeReused()
    {
        var stateDir = CreateTempDir();
        try
        {
            var store1 = new SqliteStateStore(stateDir);
            _ = await store1.GetOrCreateProjectAsync("C:/repo/proj-a");

            var store2 = new SqliteStateStore(stateDir);
            _ = await store2.GetOrCreateProjectAsync("C:/repo/proj-a");

            await using var connection = new SqliteConnection($"Data Source={Path.Combine(stateDir, "state.db")}");
            await connection.OpenAsync();

            await using var tables = connection.CreateCommand();
            tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
            await using var reader = await tables.ExecuteReaderAsync();
            var tableNames = new List<string>();
            while (await reader.ReadAsync())
            {
                tableNames.Add(reader.GetString(0));
            }

            Assert.Contains("schema_version", tableNames);
            Assert.Contains("projects", tableNames);
            Assert.Contains("topics", tableNames);
            Assert.Contains("notify_users", tableNames);

            await using var version = connection.CreateCommand();
            version.CommandText = "SELECT version FROM schema_version LIMIT 1;";
            var schemaVersion = Convert.ToInt32(await version.ExecuteScalarAsync());
            Assert.Equal(1, schemaVersion);
        }
        finally
        {
            TryDeleteDirectory(stateDir);
        }
    }

    [Fact]
    public async Task DaoOperationsPersistAndUpdateTopicState()
    {
        var stateDir = CreateTempDir();
        try
        {
            var store = new SqliteStateStore(stateDir);

            var project = await store.GetOrCreateProjectAsync("C:/repo/proj-b");
            var loadedProject = await store.GetProjectByIdAsync(project.Id);
            Assert.NotNull(loadedProject);
            Assert.Equal(project.DirPath, loadedProject!.DirPath);

            var topic = await store.CreateTopicAsync(project.Id, groupChatId: -100123456, threadId: 42, name: "proj-b");

            var loaded = await store.GetTopicByThreadIdAsync(-100123456, 42);
            Assert.NotNull(loaded);
            Assert.Equal(topic.Id, loaded!.Id);
            Assert.Equal("idle", loaded.Status);

            await store.SetTopicBusyAsync(topic.Id, true);
            await store.UpdateTopicStatusAsync(topic.Id, "working");
            await store.UpdateTopicContextLeftAsync(topic.Id, 68);
            await store.UpdateTopicCodexChatIdAsync(topic.Id, "chat_abc");

            await store.StartTopicJobAsync(topic.Id);
            await store.FinishTopicJobAsync(topic.Id, "cancelled");

            var updated = await store.GetTopicByThreadIdAsync(-100123456, 42);
            Assert.NotNull(updated);
            Assert.False(updated!.Busy);
            Assert.Equal("cancelled", updated.Status);
            Assert.Equal(68, updated.ContextLeftPercent);
            Assert.Equal("chat_abc", updated.CodexChatId);
            Assert.NotNull(updated.LastJobStartedAt);
            Assert.NotNull(updated.LastJobFinishedAt);

            var notifyUsers = await store.ListNotifyUsersAsync();
            Assert.Empty(notifyUsers);
        }
        finally
        {
            TryDeleteDirectory(stateDir);
        }
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "tg-codex-bridge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        for (var i = 0; i < 5; i++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (IOException) when (i < 4)
            {
                Thread.Sleep(200);
            }
            catch (IOException)
            {
                return;
            }
        }
    }
}
