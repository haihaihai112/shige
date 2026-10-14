using System.ComponentModel;
using System.Runtime.InteropServices;
namespace Shige.App.Platform;

public sealed class DesktopHost : IDisposable
{
    readonly IntPtr owner; readonly Action toggle, show; readonly Action<string> command; readonly Action displayChanged; readonly NativeWindowService.SubclassProc proc; bool disposed, undo;
    static readonly uint ShowMessage = RegisterWindowMessage("Shige.Independent.Show.v1"); readonly uint taskbar = RegisterWindowMessage("TaskbarCreated");
    public string? HotkeyError
    {
        get; private set;
    }
    public DesktopHost(IntPtr owner, Action toggle, Action show, Action<string> command, Action displayChanged)
    {
        this.owner = owner;
        this.toggle = toggle;
        this.show = show;
        this.command = command;
        this.displayChanged = displayChanged;
        proc = WndProc;
        NativeWindowService.SetWindowSubclass(owner, proc, 45, 0);
        AddTray();
        SetHotkey(true);
    }
    public static bool TryAcquire(out IDisposable? lease)
    {
        var mutex = new Mutex(true, "Local\\Shige.Independent.Instance.v1", out var created);
        if (!created)
        {
            mutex.Dispose();
            lease = null;
            return false;
        }
        lease = mutex;
        return true;
    }
    public static void NotifyExisting() => PostMessage((IntPtr)0xffff, ShowMessage, IntPtr.Zero, IntPtr.Zero);
    public void SetHotkey(bool enabled)
    {
        UnregisterHotKey(owner, 707);
        HotkeyError = null;
        if (enabled && !RegisterHotKey(owner, 707, 0x4000, 0x76))
            HotkeyError = "F7 注册失败：" + new Win32Exception(Marshal.GetLastWin32Error()).Message;
    }
    public void SetCanUndo(bool value) => undo = value;
    IntPtr TrayIcon()
    {
        var icon = SendMessage(owner, 0x7f, (IntPtr)2, IntPtr.Zero);
        return icon != IntPtr.Zero ? icon : LoadIcon(IntPtr.Zero, (IntPtr)32512);
    }
    NotifyData Data() => new() { Size = (uint)Marshal.SizeOf<NotifyData>(), Window = owner, Id = 1, Flags = 7, Callback = 0x8001, Icon = TrayIcon(), Tip = "拾格 · 单击或 F7 隐藏/显示", Info = "", Title = "" };
    void AddTray()
    {
        var d = Data();
        Shell_NotifyIcon(0, ref d);
    }
    public void ShowBalloon(string title, string message)
    {
        var d = Data();
        d.Flags = 0x10;
        d.Title = title[..Math.Min(title.Length, 63)];
        d.Info = message[..Math.Min(message.Length, 255)];
        d.InfoFlags = 1;
        Shell_NotifyIcon(1, ref d);
    }
    IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l, nuint id, nuint data)
    {
        if (m == ShowMessage)
        {
            show();
            return IntPtr.Zero;
        }
        if (m == taskbar)
        {
            AddTray();
        }
        if (m == 0x312 && w == (IntPtr)707)
        {
            toggle();
            return IntPtr.Zero;
        }
        if (m == 0x7e || (m == 0x218 && w == (IntPtr)7))
            displayChanged();
        if (m == 0x8001)
        {
            if (l == (IntPtr)0x202)
                toggle();
            if (l == (IntPtr)0x205)
                Menu();
            return IntPtr.Zero;
        }
        return NativeWindowService.DefSubclassProc(h, m, w, l);
    }
    void Menu()
    {
        var menu = CreatePopupMenu();
        string[] keys = ["new", "map", "organize", "undo", "toggle", "root", "settings", "exit"];
        string[] labels = ["新建格子", "映射文件夹", "整理桌面…", "撤销整理…", "显示/隐藏", "打开文件根目录", "设置", "退出"];
        for (int i = 0; i < keys.Length; i++)
            AppendMenu(menu, (uint)(i == 3 && !undo ? 1 : 0), (nuint)(i + 1), labels[i]);
        GetCursorPos(out var p);
        SetForegroundWindow(owner);
        var selected = TrackPopupMenu(menu, 0x100 | 2, p.X, p.Y, 0, owner, IntPtr.Zero);
        DestroyMenu(menu);
        if (selected > 0 && selected <= keys.Length)
            command(keys[selected - 1]);
        PostMessage(owner, 0, IntPtr.Zero, IntPtr.Zero);
    }
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        UnregisterHotKey(owner, 707);
        var d = Data();
        Shell_NotifyIcon(2, ref d);
        NativeWindowService.RemoveWindowSubclass(owner, proc, 45);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NotifyData
    {
        public uint Size; public IntPtr Window; public uint Id, Flags, Callback; public IntPtr Icon; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip; public uint State, StateMask; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info; public uint Timeout; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Title; public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct Point
    {
        public int X, Y;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(uint m, ref NotifyData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr h, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr LoadIcon(IntPtr h, IntPtr id);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr h, uint flags, nuint id, string label);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point p);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr h, IntPtr rect);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr h);
}

