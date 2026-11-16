using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Klang_WinUI.Views;
using Microsoft.Win32;

namespace Klang_WinUI.Services;

internal sealed record TaskbarEnvironment(TaskbarNative.Taskbar Bar, IReadOnlyList<ScreenRect> Blanks, bool Dark);

/// <summary>
/// UIA 仅在专用 MTA 后台线程查询。UI 不等待跨进程调用；快照按实际查询耗时设置有限有效期，
/// 即使 Explorer 的可访问性提供程序卡住，也不会不断创建查询线程或阻塞播放。
/// </summary>
internal sealed class TaskbarEnvironmentService : IDisposable
{
    private readonly ManualResetEvent _stop = new(false);
    private readonly object _gate = new();
    private IReadOnlyList<TaskbarEnvironment> _latest = Array.Empty<TaskbarEnvironment>();
    private long _updated;
    private long _validFor = 2000;
    private volatile bool _disposed;
    private readonly int _processId = Environment.ProcessId;

    public TaskbarEnvironmentService()
    {
        var thread = new Thread(Poll) { IsBackground = true, Name = "Klang taskbar geometry" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    public IReadOnlyList<TaskbarEnvironment> Snapshot()
    {
        lock (_gate) return Environment.TickCount64 - _updated <= _validFor ? _latest : Array.Empty<TaskbarEnvironment>();
    }

    private void Poll()
    {
        TaskbarAutomation? automation = null;
        try
        {
            do
            {
                try
                {
                    bool dark = IsTaskbarDark();
                    long started = Environment.TickCount64;
                    automation ??= new TaskbarAutomation();
                    var list = TaskbarNative.FindAll().Select(bar => new TaskbarEnvironment(bar, QueryBlanks(bar, automation), dark)).ToArray();
                    long elapsed = Environment.TickCount64 - started;
                    lock (_gate)
                    {
                        _latest = list;
                        _updated = Environment.TickCount64;
                        // 某些 Explorer/UIA 提供程序一次查询就需数秒。固定 2 秒会让正常查询期间反复闪隐。
                        // 仍有 15 秒硬上限；任务栏是否可见、全屏与 popup 遮挡另由 UI 线程即时核对。
                        _validFor = Math.Clamp(elapsed * 2 + 1000, 2000, 15000);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Taskbar discovery: {ex.Message}");
                    lock (_gate) _latest = Array.Empty<TaskbarEnvironment>();
                }
            } while (!_stop.WaitOne(500) && !_disposed);
        }
        finally { automation?.Dispose(); _stop.Dispose(); }
    }

    private IReadOnlyList<ScreenRect> QueryBlanks(TaskbarNative.Taskbar bar, TaskbarAutomation automation)
    {
        if (!TaskbarLayout.CanDock(bar.Bounds, bar.Monitor, bar.Scale)) return Array.Empty<ScreenRect>();
        // 整次 UIA 查询失败或没有识别到控件时，不把未知区域当成安全空白。
        try
        {
            var discovered = automation.OccupiedBounds(bar.Handle, bar.Bounds, _processId);
            if (discovered == null) return Array.Empty<ScreenRect>();
            var occupied = TaskbarNative.NativeOccupants(bar.Handle).Concat(discovered).ToArray();
            return TaskbarLayout.FindBlanks(bar.Bounds, occupied, bar.Scale);
        }
        catch { return Array.Empty<ScreenRect>(); }
    }

    private static bool IsTaskbarDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is not int value || value == 0;
        }
        catch { return true; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Set();
        }
    }
}
