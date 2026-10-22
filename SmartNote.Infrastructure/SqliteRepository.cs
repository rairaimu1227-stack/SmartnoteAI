using Microsoft.Data.Sqlite;
using SmartNote.Core;
using SmartNote.Application;
using System.Text.Json.Nodes;
namespace SmartNote.Infrastructure;
// Microsoft.Data.Sqlite's async API performs synchronous SQLite I/O. Keep the entire transaction on a worker.
public sealed class SqliteRepository(string path) : INoteRepository
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private async Task<T> Run<T>(Func<SqliteConnection, T> action)
    {
        await gate.WaitAsync();
        try
        {
            return await Task.Run(() => { using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 5 }.ToString()); db.Open(); Execute(db, null, "PRAGMA foreign_keys=ON"); return action(db); });
        }
        finally { gate.Release(); }
    }
    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (key, value) in args)
            cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return cmd;
    }
    private static int Execute(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var cmd = Command(db, tx, sql, args);
        return cmd.ExecuteNonQuery();
    }
    public Task InitializeAsync() => Run(db =>
    {
        using var check = Command(db, null, "PRAGMA quick_check");
        if ((string?)check.ExecuteScalar() != "ok")
            throw new InvalidDataException("DB整合性検査に失敗しました。元のDBを保護し、バックアップから復旧してください。");
        using var version = Command(db, null, "PRAGMA user_version");
        var schema = Convert.ToInt32(version.ExecuteScalar());
        if (schema > 2)
            throw new InvalidDataException("新しい版のDBです。このPoCでは開かず元データを保護します。");
        if (schema == 1)
        {
            var backupPath = path + ".before-v2-" + Guid.NewGuid().ToString("N") + ".bak";
            using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath }.ToString());
            backup.Open();
            db.BackupDatabase(backup);
        }
        Execute(db, null, """
        PRAGMA journal_mode=WAL;
        CREATE TABLE IF NOT EXISTS notes(id TEXT PRIMARY KEY,title TEXT NOT NULL,document TEXT NOT NULL,revision INTEGER NOT NULL,created TEXT NOT NULL,updated TEXT NOT NULL,quick INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS tasks(id TEXT PRIMARY KEY,note_id TEXT NOT NULL REFERENCES notes(id),block_id TEXT NOT NULL,name TEXT NOT NULL,completed INTEGER NOT NULL,created TEXT NOT NULL,updated TEXT NOT NULL,revision INTEGER NOT NULL,due TEXT,recurrence TEXT,UNIQUE(note_id,block_id));
        CREATE TABLE IF NOT EXISTS history(note_id TEXT NOT NULL,revision INTEGER NOT NULL,document TEXT NOT NULL,title TEXT NOT NULL,created TEXT NOT NULL,PRIMARY KEY(note_id,revision));
        CREATE TABLE IF NOT EXISTS state(key TEXT PRIMARY KEY,json TEXT NOT NULL);

        """);
        using var migration = db.BeginTransaction();
        using var columns = Command(db, migration, "SELECT COUNT(*) FROM pragma_table_info('tasks') WHERE name='priority'");
        if (Convert.ToInt32(columns.ExecuteScalar()) == 0)
            Execute(db, migration, "ALTER TABLE tasks ADD COLUMN priority INTEGER NOT NULL DEFAULT 0");
        Execute(db, migration, "PRAGMA user_version=2");
        migration.Commit();
        return true;
    });
    private static Note ReadNote(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), JsonNode.Parse(r.GetString(2))!.AsObject(), r.GetInt64(3), r.GetString(4), r.GetString(5), r.GetBoolean(6));
    public Task<IReadOnlyList<Note>> NotesAsync() => Run<IReadOnlyList<Note>>(db => { using var cmd = Command(db, null, "SELECT * FROM notes ORDER BY updated DESC"); using var r = cmd.ExecuteReader(); var result = new List<Note>(); while (r.Read()) result.Add(ReadNote(r)); return result; });
    public Task<IReadOnlyList<NoteTask>> TasksAsync() => Run<IReadOnlyList<NoteTask>>(db => { using var cmd = Command(db, null, "SELECT * FROM tasks ORDER BY completed,updated DESC"); using var r = cmd.ExecuteReader(); var result = new List<NoteTask>(); while (r.Read()) result.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), r.GetString(5), r.GetString(6), r.GetInt64(7), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.GetInt32(10))); return result; });
    private static Note GetNote(SqliteConnection db, SqliteTransaction tx, string id)
    {
        using var cmd = Command(db, tx, "SELECT * FROM notes WHERE id=$id", ("$id", id));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadNote(r) : throw new InvalidDataException("メモがありません。");
    }
    private static Note Save(SqliteConnection db, SqliteTransaction tx, Note note, IReadOnlyList<Checklist> items)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var query = Command(db, tx, "SELECT revision,created,document FROM notes WHERE id=$id", ("$id", note.Id));
        long? revision = null;
        string created = now;
        using (var r = query.ExecuteReader())
        {
            if (r.Read())
            {
                revision = r.GetInt64(0);
                created = r.GetString(1);
                if (JsonNode.Parse(r.GetString(2))?["type"]?.GetValue<string>() != note.Document["type"]?.GetValue<string>())
                    throw new InvalidDataException("メモ種別は変更できません。別のメモとしてコピーしてください。");
            }
        }
        if ((revision ?? 0) != note.Revision || (revision is null && note.Revision != 0))
            throw new RevisionConflictException();
        Execute(db, tx, "INSERT OR IGNORE INTO history SELECT id,revision,document,title,updated FROM notes WHERE id=$id", ("$id", note.Id));
        var saved = note with
        {
            Revision = note.Revision + 1,
            CreatedAt = created,
            UpdatedAt = now
        };
        Execute(db, tx, "INSERT INTO notes VALUES($id,$title,$doc,$rev,$created,$updated,$quick) ON CONFLICT(id) DO UPDATE SET title=excluded.title,document=excluded.document,revision=excluded.revision,updated=excluded.updated,quick=excluded.quick", ("$id", saved.Id), ("$title", saved.Title), ("$doc", saved.Document.ToJsonString()), ("$rev", saved.Revision), ("$created", saved.CreatedAt), ("$updated", saved.UpdatedAt), ("$quick", saved.IsQuick));
        // Deleted checklist entries remove their derived task in the same transaction. History retains the previous note.
        var current = new HashSet<string>(items.Select(i => i.BlockId));
        var removed = new List<string>();
        using (var cmd = Command(db, tx, "SELECT block_id FROM tasks WHERE note_id=$id", ("$id", note.Id)))
        using (var r = cmd.ExecuteReader())
        while (r.Read())
        if (!current.Contains(r.GetString(0)))
            removed.Add(r.GetString(0));
        foreach (var id in removed)
            Execute(db, tx, "DELETE FROM tasks WHERE note_id=$note AND block_id=$block", ("$note", note.Id), ("$block", id));
        foreach (var item in items)
            Execute(db, tx, """
        INSERT INTO tasks(id,note_id,block_id,name,completed,created,updated,revision,due,recurrence) VALUES($id,$note,$block,$name,$done,$now,$now,1,NULL,NULL)
        ON CONFLICT(note_id,block_id) DO UPDATE SET name=excluded.name,completed=excluded.completed,updated=excluded.updated,revision=tasks.revision+1
        """, ("$id", Guid.NewGuid().ToString()), ("$note", note.Id), ("$block", item.BlockId), ("$name", item.Name), ("$done", item.Completed), ("$now", now));
        return saved;
    }
    public Task<Note> SaveAsync(Note note, IReadOnlyList<Checklist> items) => Run(db => { Documents.Validate(note.Document); using var tx = db.BeginTransaction(); var result = Save(db, tx, note, items); tx.Commit(); return result; });
    public Task<Note> ToggleTaskAsync(string id, bool completed) => Run(db =>
    {
        using var tx = db.BeginTransaction();
        string noteId, blockId;
        using (var cmd = Command(db, tx, "SELECT note_id,block_id FROM tasks WHERE id=$id", ("$id", id)))
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read())
                throw new InvalidDataException("タスクがありません。");
            noteId = r.GetString(0);
            blockId = r.GetString(1);
        }
        var note = GetNote(db, tx, noteId);
        Documents.SetChecked(note.Document, blockId, completed);
        var saved = Save(db, tx, note, Documents.Checklist(note.Document));
        tx.Commit();
        return saved;
    });
    public Task UpdateTaskAsync(string id, long revision, int priority, string? dueAt) => Run(db =>
    {
        if (!Guid.TryParse(id, out _) || revision < 1 || priority is < 0 or > 3 ||
            (dueAt is not null && !DateOnly.TryParseExact(dueAt, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)))
            throw new InvalidDataException("優先度または期限が不正です。");
        using var tx = db.BeginTransaction();
        if (Execute(db, tx, "UPDATE tasks SET priority=$priority,due=$due,revision=revision+1,updated=$now WHERE id=$id AND revision=$revision",
            ("$priority", priority), ("$due", dueAt), ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$id", id), ("$revision", revision)) != 1)
            throw new RevisionConflictException();
        tx.Commit();
        return true;
    });
    public Task<string?> ReadStateAsync(string key) => Run(db => { using var cmd = Command(db, null, "SELECT json FROM state WHERE key=$key", ("$key", key)); return cmd.ExecuteScalar() as string; });
    public Task WriteStateAsync(string key, string json) => Run(db => Execute(db, null, "INSERT INTO state VALUES($key,$json) ON CONFLICT(key) DO UPDATE SET json=excluded.json", ("$key", key), ("$json", json)));
    public Task BackupAsync(string destination) => Run(db => { using var backup = new SqliteConnection($"Data Source={destination}"); backup.Open(); db.BackupDatabase(backup); return true; });
}
