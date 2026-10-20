using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using System.Windows.Threading;
using SmartNote.Core;
namespace SmartNote.Desktop;

public sealed class QuickMemoWindow : Window
{
    private readonly App app;
    private readonly TextBox input = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 16, Margin = new Thickness(12) };
    private readonly TextBlock status = new() { Margin = new Thickness(12, 4, 12, 8), Text = "一時メモ / 自動保存" };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly SemaphoreSlim gate = new(1, 1);
    private Note? note;
    private int generation;
    private bool dirty, loading;
    public QuickMemoWindow(App app)
    {
        this.app = app;
        Title = "クイックメモ — SmartNote AI";
        Width = 430;
        Height = 330;
        Topmost = app.Preferences.QuickTopmost;
        var panel = new DockPanel();
        var next = new Button { Content = "保存して次のメモ", Margin = new Thickness(12, 4, 12, 4) };
        DockPanel.SetDock(next, Dock.Bottom);
        DockPanel.SetDock(status, Dock.Bottom);
        panel.Children.Add(next);
        panel.Children.Add(status);
        panel.Children.Add(input);
        Content = panel;
        input.TextChanged += (_, _) => { if (loading) return; dirty = true; generation++; status.Text = "未保存"; timer.Stop(); timer.Start(); };
        timer.Tick += async (_, _) => { timer.Stop(); await SaveAsync(); };
        next.Click += async (_, _) => { if (await SaveAsync()) { note = null; loading = true; input.Clear(); loading = false; input.Focus(); } };
        Closing += OnClosing;
        Activated += (_, _) => input.Focus();
    }
    public async Task<bool> SaveAsync()
    {
        timer.Stop();
        await gate.WaitAsync();
        try
        {
            if (!dirty)
                return true;
            var captured = generation;
            var value = input.Text;
            var doc = new JsonObject { ["type"] = "doc", ["content"] = new JsonArray(new JsonObject { ["type"] = "paragraph", ["content"] = value.Length == 0 ? new JsonArray() : new JsonArray(new JsonObject { ["type"] = "text", ["text"] = value }) }) };
            var now = DateTimeOffset.UtcNow.ToString("O");
            note ??= new Note(Guid.NewGuid().ToString(), "クイックメモ", doc, 0, now, now, true);
            status.Text = "保存中…";
            note = await app.Notes.SaveAsync(note with
            {
                Document = doc,
                Title = value.Split('\n')[0][..Math.Min(value.Split('\n')[0].Length, 80)]
            });
            if (captured == generation)
                dirty = false;
            status.Text = dirty ? "未保存" : "保存しました（一時メモ）";
            app.Shell?.Refresh();
            return !dirty;
        }
        catch (Exception ex) { status.Text = $"保存失敗 ({ex.GetType().Name})。入力内容を保持しています。"; return false; }
        finally { gate.Release(); }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        if (await SaveAsync())
            Hide();
    }
    public void ApplyTheme()
    {
        var dark = app.Preferences.Theme == "dark" || (app.Preferences.Theme == "system" && App.SystemDark());
        Background = dark ? System.Windows.Media.Brushes.DarkSlateGray : System.Windows.Media.Brushes.WhiteSmoke;
        input.Background = dark ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.White;
        input.Foreground = dark ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Black;
        status.Foreground = dark ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Black;
    }
    public void Display()
    {
        ApplyTheme();
        Topmost = app.Preferences.QuickTopmost;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        input.Focus();
    }
}
