using System.ComponentModel;
using System.Runtime.CompilerServices;
using SmartNote.Core;
namespace SmartNote.Desktop;

public sealed class ShellViewModel : INotifyPropertyChanged
{
    private Preferences preferences = new(); private string status = "準備中";
    public Preferences Preferences
    {
        get => preferences; set
        {
            preferences = value;
            Changed();
            Changed(nameof(Topmost));
            Changed(nameof(PinLabel));
        }
    }
    public bool Topmost => preferences.Topmost;
    public string PinLabel => Topmost ? "📌 最前面 ON" : "📌 最前面 OFF";
    public string Status
    {
        get => status; set
        {
            status = value;
            Changed();
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
