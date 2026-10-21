using System.Text.Json;
using Microsoft.Win32;
using SmartNote.Application;
using SmartNote.Core;
using SmartNote.Infrastructure;
using Forms = System.Windows.Forms;
namespace SmartNote.Desktop;

public partial class App : System.Windows.Application, INotificationService
{
    private readonly SingleInstance instance = new();
    private Forms.NotifyIcon? tray;
    private GlobalHotkey? hotkey;
    private QuickMemoWindow? quick;
    private bool exiting;
    public string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartNoteAI");
    public string MediaDirectory => Path.Combine(DataDirectory, "attachments");
    public SqliteRepository Repository { get; private set; } = null!;
    public NoteService Notes { get; private set; } = null!;
    public AttachmentStore Attachments { get; private set; } = null!;
    public Preferences Preferences { get; private set; } = new();
    public MainWindow? Shell
    {
        get; private set;
    }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!instance.Acquire())
        {
            try
            {
                await instance.SignalAsync();
            }
            catch (Exception ex) { MessageBox.Show($"既存プロセスに接続できませんでした ({ex.GetType().Name})。起動処理が完了してから再実行してください。"); }
            Shutdown();
            return;
        }
        try
        {
            await Task.Run(() => { Directory.CreateDirectory(MediaDirectory); Directory.CreateDirectory(Path.Combine(DataDirectory, "backups")); });
            Repository = new(Path.Combine(DataDirectory, "smartnote.db"));
            await Repository.InitializeAsync();
            // Online SQLite backup is consistent even with a WAL; never copy a live .db alone.
            await Repository.BackupAsync(Path.Combine(DataDirectory, "backups", $"startup-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db"));
            var settings = await Repository.ReadStateAsync("preferences");
            if (settings is not null)
                Preferences = JsonSerializer.Deserialize<Preferences>(settings) ?? new();
            Notes = new(Repository, new AllChecklistPolicy());
            Attachments = new(MediaDirectory);
            Shell = new(this);
            MainWindow = Shell;
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("メイン画面を表示", null, (_, _) => Shell.ShowMain());
            menu.Items.Add("クイックメモ", null, (_, _) => ShowQuick());
            menu.Items.Add("完全終了", null, async (_, _) => await ExitAsync());
            tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "SmartNote AI", Visible = true, ContextMenuStrip = menu };
            tray.DoubleClick += (_, _) => Shell.ShowMain();
            hotkey = new(Shell, ShowQuick);
            try
            {
                await hotkey.ChangeAsync(Preferences.QuickShortcut, () => Task.CompletedTask);
            }
            catch (InvalidDataException ex) { Show("ショートカット登録失敗", ex.Message + " 設定画面で変更するか、通知領域メニューから開いてください。"); }
            _ = instance.ListenAsync(() => Dispatcher.BeginInvoke(() => Shell.ShowMain()));
            SystemEvents.UserPreferenceChanged += OnSystemPreferencesChanged;
            // Create WebView2 without showing the main window. All first launches start in the notification area.
            await Shell.InitializeAsync();
            if (e.Args.Contains("--show"))
                Shell.ShowMain();
        }
        catch (Exception ex) { MessageBox.Show($"初期化に失敗しました ({ex.GetType().Name})。データは削除していません。保存先: {DataDirectory}", "SmartNote AI", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
    }
    private void OnSystemPreferencesChanged(object sender, UserPreferenceChangedEventArgs e) => Dispatcher.BeginInvoke(() => { Shell?.SystemThemeChanged(); quick?.ApplyTheme(); });
    public static bool SystemDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }
    private readonly SemaphoreSlim preferencesGate = new(1, 1);
    public async Task SavePreferencesAsync(Preferences preferences)
    {
        if (preferences.Theme is not ("system" or "light" or "dark"))
            throw new InvalidDataException("テーマが不正です。");
        preferences = preferences with
        {
            QuickShortcut = QuickShortcut.Parse(preferences.QuickShortcut).Text
        };
        await preferencesGate.WaitAsync();
        try
        {
            var previous = Preferences;
            await hotkey!.ChangeAsync(preferences.QuickShortcut, async () =>
            {
                if (previous.AutoStart != preferences.AutoStart)
                    await StartupRegistration.SetAsync(preferences.AutoStart);
                try
                {
                    await Repository.WriteStateAsync("preferences", JsonSerializer.Serialize(preferences));
                }
                catch
                {
                    if (previous.AutoStart != preferences.AutoStart)
                        await StartupRegistration.SetAsync(previous.AutoStart);
                    throw;
                }
            });
            Preferences = preferences;
            Shell?.PreferencesChanged();
            if (quick is not null)
            {
                quick.Topmost = preferences.QuickTopmost;
                quick.ApplyTheme();
            }
        }
        finally { preferencesGate.Release(); }
    }
    public void ShowQuick()
    {
        quick ??= new(this);
        quick.Display();
    }
    public void Show(string title, string message)
    {
        tray?.ShowBalloonTip(8000, title, message, Forms.ToolTipIcon.Warning);
        if (Shell is not null)
            Shell.ViewModel.Status = message;
    }
    public async Task ExitAsync()
    {
        if (exiting)
            return;
        exiting = true;
        try
        {
            if (quick is not null && !await quick.SaveAsync())
            {
                quick.Display();
                return;
            }
            if (Shell is not null && !await Shell.FlushAsync())
            {
                Shell.ShowMain();
                MessageBox.Show("保存を確認できないため終了を中止しました。未保存内容を確認し、再度保存してください。エディタ異常時は復旧操作が必要です。", "終了中止");
                return;
            }
            await Repository.BackupAsync(Path.Combine(DataDirectory, "backups", $"exit-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db"));
            Shutdown();
        }
        catch (Exception ex) { Show("終了処理失敗", $"終了を中止しました ({ex.GetType().Name})。"); }
        finally { exiting = false; }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnSystemPreferencesChanged;
        hotkey?.Dispose();
        tray?.Dispose();
        instance.Dispose();
        base.OnExit(e);
    }
}
