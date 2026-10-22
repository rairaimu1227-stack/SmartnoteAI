using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SmartNote.Core;
using SmartNote.Application;
using SmartNote.Infrastructure;
using Xunit;
namespace SmartNote.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "smartnote-tests-" + Guid.NewGuid());
    private string Db => Path.Combine(directory, "notes.db");
    private readonly string block = Guid.NewGuid().ToString();
    public PersistenceTests() => Directory.CreateDirectory(directory);
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, true);
    }
    private Note Fixture(bool done = false)
    {
        var doc = JsonNode.Parse("""{"type":"doc","content":[{"type":"taskList","content":[{"type":"taskItem","attrs":{"checked":false},"content":[{"type":"paragraph","content":[{"type":"text","text":"検証タスク"}]}]}]}]}""")!.AsObject();
        doc["content"]![0]!["content"]![0]!["attrs"]!["blockId"] = block;
        doc["content"]![0]!["content"]![0]!["attrs"]!["checked"] = done;
        var now = DateTimeOffset.UtcNow.ToString("O");
        return new(Guid.NewGuid().ToString(), "検証用", doc, 0, now, now);
    }
    private async Task<(SqliteRepository, NoteService)> Open()
    {
        var repo = new SqliteRepository(Db);
        await repo.InitializeAsync();
        return (repo, new(repo, new AllChecklistPolicy()));
    }
    [Fact]
    public async Task POC009_SaveReopenRestoresJsonAndTasks()
    {
        var (repo, service) = await Open();
        var saved = await service.SaveAsync(Fixture());
        var (reopened, _) = await Open();
        var loaded = Assert.Single(await reopened.NotesAsync());
        Assert.Equal(saved.Document.ToJsonString(), loaded.Document.ToJsonString());
        Assert.Equal(1, loaded.Revision);
        Assert.Single(await reopened.TasksAsync());
    }
    [Fact]
    public async Task POC010_012_RepeatedSaveUpsertsTaskAndUpdatesBothStates()
    {
        var (repo, service) = await Open();
        var note = await service.SaveAsync(Fixture());
        var taskId = Assert.Single(await repo.TasksAsync()).Id;
        for (var i = 0; i < 20; i++)
        {
            Documents.SetChecked(note.Document, block, i % 2 == 0);
            note = await service.SaveAsync(note);
            var task = Assert.Single(await repo.TasksAsync());
            Assert.Equal(taskId, task.Id);
            Assert.Equal(i % 2 == 0, task.Completed);
        }
        Assert.Equal(21, note.Revision);
    }
    [Fact]
    public async Task POC011_TaskCompletionAndReopeningPatchNoteAtomically()
    {
        var (repo, service) = await Open();
        await service.SaveAsync(Fixture());
        var task = Assert.Single(await repo.TasksAsync());
        foreach (var value in new[] { true, false })
        {
            var note = await service.ToggleTaskAsync(task.Id, value);
            Assert.Equal(value, Assert.Single(Documents.Checklist(note.Document)).Completed);
            Assert.Equal(value, Assert.Single(await repo.TasksAsync()).Completed);
        }
    }
    [Fact]
    public async Task POC019_StaleRevisionDoesNotOverwrite()
    {
        var (repo, service) = await Open();
        var original = Fixture();
        var saved = await service.SaveAsync(original);
        await Assert.ThrowsAsync<RevisionConflictException>(() => service.SaveAsync(original with { Title = "上書き禁止" }));
        Assert.Equal(saved.Title, Assert.Single(await repo.NotesAsync()).Title);
        Assert.Single(await repo.TasksAsync());
    }
    [Fact]
    public async Task TransactionFailureRollsBackNoteHistoryAndTask()
    {
        var (repo, service) = await Open();
        var note = await service.SaveAsync(Fixture());
        using (var db = new SqliteConnection($"Data Source={Db}"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER reject_task BEFORE UPDATE ON tasks BEGIN SELECT RAISE(ABORT,'injected failure'); END";
            cmd.ExecuteNonQuery();
        }
        Documents.SetChecked(note.Document, block, true);
        await Assert.ThrowsAsync<SqliteException>(() => service.SaveAsync(note));
        var loaded = Assert.Single(await repo.NotesAsync());
        Assert.Equal(1, loaded.Revision);
        Assert.False(Assert.Single(Documents.Checklist(loaded.Document)).Completed);
        Assert.False(Assert.Single(await repo.TasksAsync()).Completed);
    }
    [Fact]
    public async Task TaskTransactionFailureRollsBackBothSides()
    {
        var (repo, service) = await Open();
        await service.SaveAsync(Fixture());
        using (var db = new SqliteConnection($"Data Source={Db}"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER reject_note BEFORE UPDATE ON notes BEGIN SELECT RAISE(ABORT,'injected failure'); END";
            cmd.ExecuteNonQuery();
        }
        var task = Assert.Single(await repo.TasksAsync());
        await Assert.ThrowsAsync<SqliteException>(() => service.ToggleTaskAsync(task.Id, true));
        Assert.False(Assert.Single(await repo.TasksAsync()).Completed);
        Assert.False(Assert.Single(Documents.Checklist(Assert.Single(await repo.NotesAsync()).Document)).Completed);
    }
    [Fact]
    public async Task ReorderingPreservesTaskIdentityAndDeletingRemovesDerivedTask()
    {
        var (repo, service) = await Open();
        var original = Fixture();
        var items = original.Document["content"]![0]!["content"]!.AsArray();
        var other = items[0]!.DeepClone();
        var second = Guid.NewGuid().ToString();
        other["attrs"]!["blockId"] = second;
        items.Add(other);
        var saved = await service.SaveAsync(original);
        var ids = (await repo.TasksAsync()).ToDictionary(t => t.BlockId, t => t.Id);
        var first = items[0];
        items.RemoveAt(0);
        items.Add(first);
        saved = await service.SaveAsync(saved);
        Assert.All(await repo.TasksAsync(), t => Assert.Equal(ids[t.BlockId], t.Id));
        items.RemoveAt(0);
        await service.SaveAsync(saved);
        Assert.Equal(block, Assert.Single(await repo.TasksAsync()).BlockId);
    }
    [Fact]
    public async Task InvalidDuplicateBlockIdsAreRejectedBeforeWrite()
    {
        var (repo, service) = await Open();
        var note = Fixture();
        var content = note.Document["content"]![0]!["content"]!.AsArray();
        content.Add(content[0]!.DeepClone());
        await Assert.ThrowsAsync<InvalidDataException>(async () => await service.SaveAsync(note));
        Assert.Empty(await repo.NotesAsync());
    }
    [Fact]
    public async Task POC013_015_TenNotesAndSessionSettingsSurviveReopen()
    {
        var (repo, service) = await Open();
        for (var i = 0; i < 10; i++)
            await service.SaveAsync(Fixture() with
            {
                Title = $"メモ {i}"
            });
        await repo.WriteStateAsync("preferences", "{\"topmost\":true}");
        await repo.WriteStateAsync("session", "{\"tabs\":[1,2,3,4,5,6,7,8,9,10]}");
        var (reopened, _) = await Open();
        Assert.Equal(10, (await reopened.NotesAsync()).Count);
        Assert.Equal("{\"topmost\":true}", await reopened.ReadStateAsync("preferences"));
        Assert.Contains("10", await reopened.ReadStateAsync("session"));
    }
    [Fact]
    public async Task POC008_ImageReferenceAndBytesSurviveSaveReopen()
    {
        var (repo, service) = await Open();
        var store = new AttachmentStore(directory);
        var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aPrsAAAAASUVORK5CYII=");
        var image = await store.ImportAsync(bytes);
        var note = Fixture();
        note.Document["content"]!.AsArray().Add(new JsonObject { ["type"] = "image", ["attrs"] = new JsonObject { ["attachmentId"] = image.Id, ["src"] = image.Src } });
        await service.SaveAsync(note);
        var (reopened, _) = await Open();
        Assert.Contains(image.Id, Assert.Single(await reopened.NotesAsync()).Document.ToJsonString());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(directory, image.Id + ".img")));
    }
    [Fact]
    public async Task AttachmentFailureDoesNotRegisterAReference()
    {
        var store = new AttachmentStore(Path.Combine(directory, "missing"));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => store.ImportAsync(new byte[] { 255, 216, 255, 1 }));
        Assert.Empty(Directory.GetFiles(directory));
        await Assert.ThrowsAsync<InvalidDataException>(() => new AttachmentStore(directory).ImportAsync(new byte[] { 1, 2, 3, 4 }));
    }
    [Fact]
    public async Task BackupAndHistoryRetainPreviousVersions()
    {
        var (repo, service) = await Open();
        var note = await service.SaveAsync(Fixture());
        await service.SaveAsync(note with
        {
            Title = "変更後"
        });
        var backup = Path.Combine(directory, "backup.db");
        await repo.BackupAsync(backup);
        var restored = new SqliteRepository(backup);
        Assert.Equal("変更後", Assert.Single(await restored.NotesAsync()).Title);
        using var db = new SqliteConnection($"Data Source={backup}");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT title FROM history WHERE revision=1";
        Assert.Equal("検証用", cmd.ExecuteScalar());
    }
    [Fact]
    public async Task POC018_QuickMemoRestoresAsTemporaryNote()
    {
        var (repo, service) = await Open();
        await service.SaveAsync(Fixture() with
        {
            IsQuick = true
        });
        var (reopened, _) = await Open();
        Assert.True(Assert.Single(await reopened.NotesAsync()).IsQuick);
    }
    [Fact]
    public async Task MissingManagedAttachmentRejectsSaveValidation()
    {
        var note = Fixture();
        note.Document["content"]!.AsArray().Add(new JsonObject { ["type"] = "image", ["attrs"] = new JsonObject { ["attachmentId"] = block, ["src"] = $"https://media.smartnote.local/{block}.img" } });
        await Assert.ThrowsAsync<InvalidDataException>(() => new AttachmentStore(directory).ValidateReferencesAsync(note.Document));
    }
    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("https://media.smartnote.local:444/")]
    [InlineData("https://user@media.smartnote.local/")]
    public void InvalidAttachmentOriginsAreRejected(string origin)
    {
        var note = Fixture();
        note.Document["content"]!.AsArray().Add(new JsonObject { ["type"] = "image", ["attrs"] = new JsonObject { ["attachmentId"] = block, ["src"] = origin + block + ".img" } });
        Assert.Throws<InvalidDataException>(() => Documents.Validate(note.Document));
    }
    [Fact]
    public async Task FutureSchemaIsNotDowngraded()
    {
        await Open();
        using (var db = new SqliteConnection($"Data Source={Db}"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "PRAGMA user_version=3";
            cmd.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => new SqliteRepository(Db).InitializeAsync());
        using var verify = new SqliteConnection($"Data Source={Db}");
        verify.Open();
        using var query = verify.CreateCommand();
        query.CommandText = "PRAGMA user_version";
        Assert.Equal(3L, query.ExecuteScalar());
    }
    [Fact]
    public async Task CorruptDatabaseIsNotReplaced()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("invalid-database-for-test");
        await File.WriteAllBytesAsync(Db, bytes);
        await Assert.ThrowsAsync<SqliteException>(() => new SqliteRepository(Db).InitializeAsync());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Db));
    }
    [Fact]
    public async Task QuickShortcutSettingSurvivesDatabaseReopen()
    {
        var (repo, _) = await Open();
        await repo.WriteStateAsync("preferences", System.Text.Json.JsonSerializer.Serialize(new Preferences(QuickShortcut: "Ctrl+Shift+Q")));
        var (reopened, _) = await Open();
        var restored = System.Text.Json.JsonSerializer.Deserialize<Preferences>((await reopened.ReadStateAsync("preferences"))!);
        Assert.Equal("Ctrl+Shift+Q", restored!.QuickShortcut);
    }

    [Fact]
    public async Task TaskMetadataSurvivesNoteSaveToggleAndReopen()
    {
        var (repo, service) = await Open();
        var note = await service.SaveAsync(Fixture());
        var task = Assert.Single(await repo.TasksAsync());
        await repo.UpdateTaskAsync(task.Id, task.Revision, 3, "2026-11-01");
        await Assert.ThrowsAsync<RevisionConflictException>(() => repo.UpdateTaskAsync(task.Id, task.Revision, 1, null));
        note = await service.SaveAsync(note);
        await repo.ToggleTaskAsync(task.Id, true);
        var (reopened, _) = await Open();
        task = Assert.Single(await reopened.TasksAsync());
        Assert.True(task.Completed);
        Assert.Equal(3, task.Priority);
        Assert.Equal("2026-11-01", task.DueAt);
        await repo.UpdateTaskAsync(task.Id, task.Revision, 0, null);
        Assert.Null(Assert.Single(await repo.TasksAsync()).DueAt);
    }
    [Theory]
    [InlineData(-1, null)]
    [InlineData(4, null)]
    [InlineData(1, "2026-02-30")]
    [InlineData(1, "arbitrary")]
    public async Task InvalidTaskMetadataDoesNotChangeData(int priority, string? due)
    {
        var (repo, service) = await Open();
        await service.SaveAsync(Fixture());
        var task = Assert.Single(await repo.TasksAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => repo.UpdateTaskAsync(task.Id, task.Revision, priority, due));
        Assert.Equal(task, Assert.Single(await repo.TasksAsync()));
    }
    [Fact]
    public async Task TextMemoRoundTripsRawTextAndRejectsChangingNoteKind()
    {
        var (repo, service) = await Open();
        var document = new JsonObject { ["type"] = "textDocument", ["text"] = "日本語\r\n{ \"enabled\": true }", ["attrs"] = new JsonObject { ["language"] = "json" } };
        var note = await service.SaveAsync(Fixture() with { Document = document });
        var (reopened, _) = await Open();
        Assert.Equal(document.ToJsonString(), Assert.Single(await reopened.NotesAsync()).Document.ToJsonString());
        Assert.Empty(await reopened.TasksAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => service.SaveAsync(note with { Document = Documents.Empty() }));
        Assert.Equal(document.ToJsonString(), Assert.Single(await repo.NotesAsync()).Document.ToJsonString());
    }
    [Fact]
    public async Task VersionOneMigrationPreservesDataAndCreatesRecoveryBackup()
    {
        var (repo, service) = await Open();
        var note = await service.SaveAsync(Fixture());
        using (var db = new SqliteConnection($"Data Source={Db}"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "ALTER TABLE tasks DROP COLUMN priority; PRAGMA user_version=1";
            cmd.ExecuteNonQuery();
        }
        var (upgraded, _) = await Open();
        Assert.Equal(note.Document.ToJsonString(), Assert.Single(await upgraded.NotesAsync()).Document.ToJsonString());
        Assert.Equal(0, Assert.Single(await upgraded.TasksAsync()).Priority);
        var backup = Assert.Single(Directory.GetFiles(directory, "*.before-v2-*.bak"));
        using var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup }.ToString());
        snapshot.Open();
        using var version = snapshot.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        Assert.Equal(1L, version.ExecuteScalar());
    }
}
