using SmartNote.Core;
namespace SmartNote.Application;

public interface IShortcutRegistrar
{
    bool Register(int id, uint modifiers, uint key);
    void Unregister(int id);
}

// Keep the old registration live until both the new registration and persistence succeed.
// The Windows adapter must call this on its window's dispatcher so Win32 registration stays on the owning thread.
public sealed class ShortcutController(IShortcutRegistrar registrar) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public int? ActiveId
    {
        get; private set;
    }
    public string Current { get; private set; } = "";
    public async Task ChangeAsync(string value, Func<Task> persist)
    {
        var shortcut = QuickShortcut.Parse(value);
        await gate.WaitAsync();
        try
        {
            if (shortcut.Text == Current)
            {
                await persist();
                return;
            }
            var nextId = ActiveId == 0x534e ? 0x534f : 0x534e;
            if (shortcut.Enabled && !registrar.Register(nextId, shortcut.Modifiers | 0x4000, shortcut.Key))
                throw new InvalidDataException($"{shortcut.Text} を登録できません。他のアプリで使用中、またはWindowsが予約している可能性があります。元のショートカットは変更していません。");
            try
            {
                await persist();
            }
            catch { if (shortcut.Enabled) registrar.Unregister(nextId); throw; }
            var previousId = ActiveId;
            ActiveId = shortcut.Enabled ? nextId : null;
            Current = shortcut.Text;
            if (previousId.HasValue)
                registrar.Unregister(previousId.Value);
        }
        finally { gate.Release(); }
    }
    public void Dispose()
    {
        if (ActiveId.HasValue)
            registrar.Unregister(ActiveId.Value);
        ActiveId = null;
    }
}
