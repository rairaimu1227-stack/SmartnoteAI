using System.Text.Json.Nodes;
using SmartNote.Core;
namespace SmartNote.Application;

public interface INoteRepository
{
    Task InitializeAsync();
    Task<IReadOnlyList<Note>> NotesAsync();
    Task<IReadOnlyList<NoteTask>> TasksAsync();
    Task<Note> SaveAsync(Note note, IReadOnlyList<Checklist> checklist);
    Task<Note> ToggleTaskAsync(string taskId, bool completed);
    Task<string?> ReadStateAsync(string key);
    Task WriteStateAsync(string key, string json);
}
public interface ITaskRegistrationPolicy
{
    IReadOnlyList<Checklist> Select(JsonObject document);
}
public sealed class AllChecklistPolicy : ITaskRegistrationPolicy
{
    public IReadOnlyList<Checklist> Select(JsonObject document) => Documents.Checklist(document);
}
public interface INotificationService
{
    void Show(string title, string message);
}
// Future adapters have no credentials or network implementation in this PoC.
public interface IAiOrganizer
{
    Task<string> SuggestAsync(string input, CancellationToken cancellationToken);
}
public interface ISyncService
{
    Task SyncAsync(CancellationToken cancellationToken);
}
public sealed class NoteService(INoteRepository repository, ITaskRegistrationPolicy policy)
{
    public Task<Note> SaveAsync(Note note)
    {
        if (!Guid.TryParse(note.Id, out _) || note.Title.Length > 500 || note.Revision < 0)
            throw new InvalidDataException("メモ情報が不正です。");
        Documents.Validate(note.Document);
        return repository.SaveAsync(note, policy.Select(note.Document));
    }
    public Task<Note> ToggleTaskAsync(string id, bool completed) => repository.ToggleTaskAsync(id, completed);
}
