using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
namespace SmartNote.Editor;

public sealed class EditorBridge(WebView2 view)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public event Action<string>? Failure;
    public Func<string, JsonElement, Task<object?>>? Dispatch
    {
        get; set;
    }
    public async Task InitializeAsync(string webDirectory, string mediaDirectory, string userDirectory)
    {
        var environment = await CoreWebView2Environment.CreateAsync(null, userDirectory);
        await view.EnsureCoreWebView2Async(environment);
        var core = view.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping("app.smartnote.local", webDirectory, CoreWebView2HostResourceAccessKind.DenyCors);
        core.SetVirtualHostNameToFolderMapping("media.smartnote.local", mediaDirectory, CoreWebView2HostResourceAccessKind.DenyCors);
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.NavigationStarting += (_, e) => { if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "app.smartnote.local" || uri.AbsolutePath != "/index.html") e.Cancel = true; };
        core.FrameNavigationStarting += (_, e) => e.Cancel = true;
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.ProcessFailed += (_, e) => Failure?.Invoke($"WebView2プロセス異常: {e.ProcessFailedKind}。自動再読み込みは行いません。保存済みデータ・履歴を保護しています。未保存内容が回復できない場合があります。");
        core.WebMessageReceived += Receive;
        view.Source = new Uri("https://app.smartnote.local/index.html");
    }
    private async void Receive(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? id = null;
        try
        {
            if (e.Source != "https://app.smartnote.local/index.html")
                throw new InvalidDataException("不正な送信元です。");
            var raw = e.WebMessageAsJson;
            if (raw.Length > 15_000_000)
                throw new InvalidDataException("メッセージが大きすぎます。");
            using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            id = root.GetProperty("id").GetString();
            if (!Guid.TryParse(id, out _))
                throw new InvalidDataException("要求IDが不正です。");
            var command = root.GetProperty("command").GetString() ?? throw new InvalidDataException();
            var data = root.GetProperty("data").Clone();
            var result = await (Dispatch?.Invoke(command, data) ?? throw new InvalidOperationException("通信を初期化できませんでした。"));
            Send(new
            {
                id,
                ok = true,
                data = result
            });
        }
        catch (Exception ex)
        {
            // Never log message bodies, JSON contents, clipboard data, or exception strings containing them.
            var message = ex is InvalidDataException or SmartNote.Core.RevisionConflictException ? ex.Message : $"処理に失敗しました ({ex.GetType().Name})。未保存内容を保持して再試行してください。";
            if (id is not null)
                Send(new
                {
                    id,
                    ok = false,
                    error = message
                });
            else
                Failure?.Invoke(message);
        }
    }
    public void Send(object value)
    {
        try
        {
            if (view.CoreWebView2 is not null)
                view.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(value, Json));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { Failure?.Invoke("WebView2との通信が切断されました。保存済みデータからの復旧が必要です。"); }
    }
}
