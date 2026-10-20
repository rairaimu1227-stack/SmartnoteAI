using System.ComponentModel;
using System.Text.Json;
using Microsoft.Win32;
using SmartNote.Core;
using SmartNote.Editor;
namespace SmartNote.Desktop;

public partial class MainWindow : Window
{
    private readonly App app;
    private EditorBridge? bridge;
    private readonly Dictionary<string, TaskCompletionSource<bool>> flushes = [];
    public ShellViewModel ViewModel { get; } = new();
    private bool initialized, failed;
    public MainWindow(App app)
    {
        this.app = app;
        InitializeComponent();
        DataContext = ViewModel;
        Closing += OnClosing;
    }
    public async Task InitializeAsync()
    {
        ViewModel.Preferences = app.Preferences;
        try
        {
            bridge = new(Web);
            bridge.Dispatch = Dispatch;
            bridge.Failure += message => { failed = true; ViewModel.Status = message; FailureText.Text = message; FailurePanel.Visibility = Visibility.Visible; foreach (var waiter in flushes.Values) waiter.TrySetResult(false); };
            await bridge.InitializeAsync(Path.Combine(AppContext.BaseDirectory, "EditorWeb"), app.MediaDirectory, Path.Combine(app.DataDirectory, "WebView2"));
            initialized = true;
            ViewModel.Status = "ローカル保存 / Ctrl+S";
        }
        catch (Exception ex) { failed = true; FailureText.Text = $"WebView2初期化失敗 ({ex.GetType().Name}, 0x{ex.HResult:X8})。WebView2 Evergreen Runtimeのインストール、EditorWebフォルダとユーザーデータ領域のアクセス権を確認してください。"; FailurePanel.Visibility = Visibility.Visible; ViewModel.Status = FailureText.Text; ShowMain(); }
    }
    private static T Parse<T>(JsonElement data) => data.Deserialize<T>(EditorBridge.Json) ?? throw new InvalidDataException("要求データがありません。");
    private async Task<object?> Dispatch(string command, JsonElement data)
    {
        switch (command)
        {
            case "bootstrap":
                var session = await app.Repository.ReadStateAsync("session");
                return new
                {
                    notes = await app.Repository.NotesAsync(),
                    tasks = await app.Repository.TasksAsync(),
                    preferences = app.Preferences,
                    session = session is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(session),
                    systemDark = App.SystemDark()
                };
            case "saveNote":
                var note = Parse<Note>(data);
                await app.Attachments.ValidateReferencesAsync(note.Document);
                return await app.Notes.SaveAsync(note);
            case "status":
                var status = data.GetProperty("text").GetString() ?? "";
                if (status.Length > 128)
                    throw new InvalidDataException("ステータスが長すぎます。");
                ViewModel.Status = status;
                return null;
            case "tasks":
                return await app.Repository.TasksAsync();
            case "toggleTask":
                return await app.Notes.ToggleTaskAsync(data.GetProperty("id").GetString() ?? "", data.GetProperty("completed").GetBoolean());
            case "session":
                if (data.GetRawText().Length > 500_000 || data.GetProperty("tabs").GetArrayLength() > 100)
                    throw new InvalidDataException("タブ情報が大きすぎます。");
                foreach (var tab in data.GetProperty("tabs").EnumerateArray())
                if (!Guid.TryParse(tab.GetProperty("id").GetString(), out _) || tab.GetProperty("mode").GetString() is not ("note" or "text"))
                    throw new InvalidDataException("タブ情報が不正です。");
                await app.Repository.WriteStateAsync("session", data.GetRawText());
                return null;
            case "preferences":
                var preferences = Parse<Preferences>(data);
                await app.SavePreferencesAsync(preferences);
                return app.Preferences;
            case "image":
                return await app.Attachments.ImportAsync(Convert.FromBase64String(data.GetProperty("base64").GetString() ?? ""));
            case "quick":
                app.ShowQuick();
                return null;
            case "flushed":
                if (flushes.TryGetValue(data.GetProperty("token").GetString() ?? "", out var completion))
                    completion.TrySetResult(data.GetProperty("ok").GetBoolean());
                return null;
            default:
                throw new InvalidDataException("許可されていない操作です。");
        }
    }
    public async Task<bool> FlushAsync()
    {
        if (!initialized)
            return true;
        if (failed)
            return false;
        var token = Guid.NewGuid().ToString();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        flushes[token] = completion;
        bridge!.Send(new
        {
            @event = "flush",
            token
        });
        try
        {
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch (TimeoutException) { return false; }
        finally { flushes.Remove(token); }
    }
    public void PreferencesChanged()
    {
        ViewModel.Preferences = app.Preferences;
        bridge?.Send(new
        {
            @event = "preferences",
            data = app.Preferences
        });
    }
    public void SystemThemeChanged() => bridge?.Send(new { @event = "systemTheme", data = App.SystemDark() });
    public void Refresh() => bridge?.Send(new { @event = "refresh" });
    public void ShowMain()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        _ = FlushAsync();
    }
    private async void PinClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await app.SavePreferencesAsync(app.Preferences with
            {
                Topmost = !app.Preferences.Topmost
            });
        }
        catch (Exception ex) { ViewModel.Status = $"設定保存失敗 ({ex.GetType().Name})"; }
    }
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void HideClick(object sender, RoutedEventArgs e) => Close();
    private async void RecoverClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("保存済みの内容から再読み込みします。Webエディタ内の未保存内容は失われる可能性があります。続けますか？", "復旧", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        // A browser-process exit invalidates the controller. Recreate the control rather than calling Reload on it.
        if (Web.Parent is System.Windows.Controls.Panel parent)
        {
            parent.Children.Remove(Web);
            Web.Dispose();
            Web = new Microsoft.Web.WebView2.Wpf.WebView2();
            parent.Children.Insert(0, Web);
        }
        initialized = false;
        failed = false;
        FailurePanel.Visibility = Visibility.Collapsed;
        await InitializeAsync();
    }
}
