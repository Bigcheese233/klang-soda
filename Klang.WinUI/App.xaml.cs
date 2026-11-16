using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Klang_WinUI.Services;
using Microsoft.UI.Xaml;

namespace Klang_WinUI;

public partial class App : Application
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    public static MainWindow? MainWindowInstance { get; private set; }

    /// <summary>错误日志位于独立资料目录 KlangSoda；自定义资料根目录由 KLANG_DATA_DIR 指定。</summary>
    public static readonly string ErrorLogPath = Path.Combine(
        Services.StateService.ResolveDataDirectory(), "error.log");

    public static void LogError(string kind, object? detail)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ErrorLogPath)!);
            var fi = new FileInfo(ErrorLogPath);
            if (fi.Exists && fi.Length > 1024 * 1024) fi.Delete();   // 最多保留 1 MB
            File.AppendAllText(ErrorLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] v{Services.CrashReporter.AppVersion} {kind}: {detail}\n\n");
        }
        catch { }
        Services.CrashReporter.Note("错误：" + kind);
    }

    public App()
    {
        Services.CrashReporter.Start();
        // 独立于上游 Klang；隔离资料目录启动的开发实例另用一个标识。
        SetCurrentProcessExplicitAppUserModelID(AppIdentity.ApplicationId +
            (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KLANG_DATA_DIR")) ? "" : ".Development"));
        InitializeComponent();

        // 界面线程上未处理的异常：记录下来并拦截，保持程序运行
        this.UnhandledException += (sender, e) =>
        {
            LogError("Unhandled", e.Exception);
            e.Handled = true;
        };
        // 后台线程上未处理的异常（进程会退出，至少留下记录）
        AppDomain.CurrentDomain.UnhandledException += (s, e) => LogError("Fatal", e.ExceptionObject);
        // 没人等待的 Task 里抛出的异常
        TaskScheduler.UnobservedTaskException += (s, e) => { LogError("UnobservedTask", e.Exception); e.SetObserved(); };
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        L.Init();   // 必须在创建任何界面之前：代码里的 L.T 靠它加载翻译表
        MainWindowInstance = new MainWindow();
        MainWindowInstance.Activate();
    }
}
