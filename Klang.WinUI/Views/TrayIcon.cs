using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Klang_WinUI.Views;

/// <summary>
/// 系统托盘图标：左键单击显示主窗口，右键弹出菜单（显示 / 播放暂停 / 上一首 / 下一首 / 退出）。
/// WinUI 3 没有托盘控件，这里用一个隐藏的消息窗口接收 Shell_NotifyIcon 的回调。
/// 必须在界面线程上创建，事件也都在界面线程上触发。资源管理器重启后会自动重新添加图标。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    public event Action? ShowRequested;
    public event Action? PlayPauseRequested;
    public event Action? PrevRequested;
    public event Action? NextRequested;
    public event Action? ExitRequested;

    /// <summary>弹出菜单前询问当前是否在播放（决定显示“播放”还是“暂停”）。</summary>
    public Func<bool>? IsPlaying { get; set; }

    /// <summary>弹出菜单前询问应用当前是否为深色主题（菜单跟着变色）。</summary>
    public Func<bool>? IsDark { get; set; }

    private const string ClassName = "Klang.TrayIcon";
    private const uint CallbackMessage = 0x8001;   // WM_APP + 1
    private const uint IconId = 1;
    private static readonly WndProc Callback = WindowProc;
    private static bool _registered;
    private static TrayIcon? _current;   // 窗口过程是静态的，转给当前实例
    private static uint _taskbarCreated;

    private IntPtr _hwnd, _icon;
    private bool _added;
    private string _tip = "Klang";

    public TrayIcon()
    {
        _current = this;
        var instance = GetModuleHandle(null);
        if (!_registered)
        {
            var cls = new WindowClass { Instance = instance, Name = ClassName, Procedure = Marshal.GetFunctionPointerForDelegate(Callback) };
            if (RegisterClass(ref cls) == 0 && Marshal.GetLastWin32Error() != 1410)   // 1410 = 类已注册
                throw new System.ComponentModel.Win32Exception();
            _registered = true;
            _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        }
        // 普通的隐藏顶层窗口（不是 HWND_MESSAGE）：只有顶层窗口才收得到 TaskbarCreated 广播
        _hwnd = CreateWindowEx(0x80 /* TOOLWINDOW */, ClassName, "Klang", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        _icon = LoadAppIcon();
        Add();
    }

    /// <summary>鼠标悬停时的提示（最多 127 个字符）。</summary>
    public void SetToolTip(string tip)
    {
        tip = string.IsNullOrWhiteSpace(tip) ? "Klang" : tip;
        if (tip.Length > 127) tip = tip[..126] + "…";
        if (tip == _tip) return;
        _tip = tip;
        if (_added) Notify(1 /* NIM_MODIFY */);
    }

    private static IntPtr LoadAppIcon()
    {
        int size = GetSystemMetrics(49);   // SM_CXSMICON
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        var icon = File.Exists(path) ? LoadImage(IntPtr.Zero, path, 1 /* IMAGE_ICON */, size, size, 0x10 /* LR_LOADFROMFILE */) : IntPtr.Zero;
        return icon != IntPtr.Zero ? icon : LoadIcon(IntPtr.Zero, new IntPtr(32512));   // IDI_APPLICATION
    }

    private void Add()
    {
        _added = Notify(0 /* NIM_ADD */);
        if (!_added) return;
        var data = NewData();
        data.Version = 4;   // NOTIFYICON_VERSION_4：回调里带上事件类型和坐标
        Shell_NotifyIcon(4 /* NIM_SETVERSION */, ref data);
    }

    private bool Notify(uint message)
    {
        var data = NewData();
        data.Flags = 0x1 | 0x2 | 0x4 | 0x80;   // MESSAGE | ICON | TIP | SHOWTIP
        data.CallbackMessage = CallbackMessage;
        data.Icon = _icon;
        data.Tip = _tip;
        return Shell_NotifyIcon(message, ref data);
    }

    private NotifyIconData NewData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = _hwnd,
        Id = IconId,
        Tip = "",
        Info = "",
        InfoTitle = ""
    };

    private static IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr w, IntPtr l)
    {
        var self = _current;
        if (self != null && hwnd == self._hwnd)
        {
            if (message == CallbackMessage)
            {
                int evt = (int)(l.ToInt64() & 0xFFFF);
                switch (evt)
                {
                    case 0x0400:   // NIN_SELECT（左键单击）
                    case 0x0401:   // NIN_KEYSELECT
                    case 0x0203:   // WM_LBUTTONDBLCLK
                        self.ShowRequested?.Invoke();
                        break;
                    case 0x007B:   // WM_CONTEXTMENU（右键）
                        int x = (short)(w.ToInt64() & 0xFFFF), y = (short)((w.ToInt64() >> 16) & 0xFFFF);
                        self.ShowMenu(x, y);
                        break;
                }
                return IntPtr.Zero;
            }
            if (message == _taskbarCreated && _taskbarCreated != 0)
            {
                self.Add();   // 资源管理器重启后托盘图标会丢失，重新添加
                return IntPtr.Zero;
            }
        }
        return DefWindowProc(hwnd, message, w, l);
    }

    private void ShowMenu(int x, int y)
    {
        if (IsDark != null) SetMenuTheme(IsDark());
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            bool playing = IsPlaying?.Invoke() ?? false;
            AppendMenu(menu, 0, 1, L.T("显示 Klang"));
            AppendMenu(menu, 0x800 /* SEPARATOR */, 0, null);
            AppendMenu(menu, 0, 2, playing ? L.T("暂停") : L.T("播放"));
            AppendMenu(menu, 0, 3, L.T("上一首"));
            AppendMenu(menu, 0, 4, L.T("下一首"));
            AppendMenu(menu, 0x800, 0, null);
            AppendMenu(menu, 0, 5, L.T("退出"));
            SetMenuDefaultItem(menu, 1, 0);

            // 弹出前先把隐藏窗口设为前台，否则点菜单外面菜单不会消失
            SetForegroundWindow(_hwnd);
            int cmd = TrackPopupMenuEx(menu, 0x0100 /* RETURNCMD */ | 0x0002 /* RIGHTBUTTON */, x, y, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, 0, IntPtr.Zero, IntPtr.Zero);   // WM_NULL
            switch (cmd)
            {
                case 1: ShowRequested?.Invoke(); break;
                case 2: PlayPauseRequested?.Invoke(); break;
                case 3: PrevRequested?.Invoke(); break;
                case 4: NextRequested?.Invoke(); break;
                case 5: ExitRequested?.Invoke(); break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    /// <summary>让托盘菜单跟随应用的深色 / 浅色主题（uxtheme 未公开的接口，失败时保持系统默认）。</summary>
    private static void SetMenuTheme(bool dark)
    {
        try
        {
            SetPreferredAppMode(dark ? 2 /* ForceDark */ : 3 /* ForceLight */);
            FlushMenuThemes();
        }
        catch { }
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = NewData();
            Shell_NotifyIcon(2 /* NIM_DELETE */, ref data);
            _added = false;
        }
        if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
        if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
        if (_current == this) _current = null;
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style; public IntPtr Procedure; public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? Menu; public string Name;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Item;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClass(ref WindowClass cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint extended, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")] private static extern bool AppendMenu(IntPtr menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr param);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("uxtheme.dll", EntryPoint = "#135")] private static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136")] private static extern void FlushMenuThemes();
}
