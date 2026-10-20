using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Interop;
using Microsoft.Win32;
namespace SmartNote.Desktop;

public sealed class SingleInstance : IDisposable
{
    private readonly string name = "SmartNoteAI-" + WindowsIdentity.GetCurrent().User!.Value;
    private Mutex? mutex;
    private readonly CancellationTokenSource cancellation = new();
    public bool Acquire()
    {
        mutex = new Mutex(true, @"Local\" + name, out var created);
        return created;
    }
    public async Task SignalAsync()
    {
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        await pipe.WriteAsync(new byte[] { 1 });
    }
    public async Task ListenAsync(Action activate)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation.Token);
                var bytes = new byte[1];
                if (await pipe.ReadAsync(bytes, cancellation.Token) == 1 && bytes[0] == 1)
                    activate();
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) {/* Retry a disconnected activation client. */}
        }
    }
    public void Dispose()
    {
        cancellation.Cancel();
        mutex?.Dispose();
        cancellation.Dispose();
    }
}
public sealed class GlobalHotkey : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
    private readonly HwndSource source;
    private readonly Action show;
    public bool Registered
    {
        get;
    }
    public GlobalHotkey(Window window, Action show)
    {
        this.show = show;
        var handle = new WindowInteropHelper(window).EnsureHandle();
        source = HwndSource.FromHwnd(handle)!;
        source.AddHook(Hook);
        Registered = RegisterHotKey(handle, 0x534e, 0x4000 | 0x0001 | 0x0002, 0x4d);
    }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam.ToInt32() == 0x534e)
        {
            show();
            handled = true;
        }
        return IntPtr.Zero;
    }
    public void Dispose()
    {
        if (Registered)
            UnregisterHotKey(source.Handle, 0x534e);
        source.RemoveHook(Hook);
    }
}
public static class StartupRegistration
{
    public static Task SetAsync(bool enabled) => Task.Run(() =>
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled)
        {
            var path = Environment.ProcessPath ?? throw new InvalidOperationException("実行パスが不明です。");
            if (!path.EndsWith("SmartNote.Desktop.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("自動起動は配布先のexeから設定してください。");
            key.SetValue("SmartNoteAI", $"\"{path}\" --background");
        }
        else
            key.DeleteValue("SmartNoteAI", false);
    });
}
