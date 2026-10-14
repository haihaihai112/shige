using Microsoft.UI.Xaml;

namespace Shige.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Trace("进程启动");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Trace(e.ExceptionObject.ToString() ?? "未处理异常");
        if (!Shige.App.Platform.DesktopHost.TryAcquire(out var lease))
        {
            Shige.App.Platform.DesktopHost.NotifyExisting();
            return;
        }
        using var instanceLease = lease;
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Trace("ComWrappers 已初始化");
        Application.Start(callback =>
        {
            Trace("Application.Start 回调");
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(dispatcher));
            _ = new ShigeApplication();
        });
    }
    internal static void Trace(string message)
    {
        try { var directory = Environment.GetEnvironmentVariable("SHIGE_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Shige"); Directory.CreateDirectory(directory); File.AppendAllText(Path.Combine(directory, "startup.log"), DateTimeOffset.Now + " " + message + Environment.NewLine); } catch { }
    }
}

public sealed partial class ShigeApplication : Application
{
    private AppController? _controller;
    public ShigeApplication()
    {
        Program.Trace("Application 构造");
        UnhandledException += (_, e) => Program.Trace(e.Exception.ToString());
        InitializeComponent();
        Program.Trace("控件资源已加载");
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Program.Trace("OnLaunched");
        _controller = new AppController();
        await _controller.StartAsync();
    }
}
