using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.Graphics;
namespace Shige.App.Platform;

public static class NativeWindowService
{
    public static IntPtr Handle(Window window) => WinRT.Interop.WindowNative.GetWindowHandle(window);
    public static void Place(Window window, int x, int y, int w, int h, bool locked)
    {
        window.AppWindow.MoveAndResize(new RectInt32(x, y, Math.Max(220, w), Math.Max(160, h)));
        SetLocked(window, locked);
        ClampToScreen(window);
    }
    public static (int X, int Y, int Width, int Height) Bounds(Window window)
    {
        var p = window.AppWindow.Position;
        var s = window.AppWindow.Size;
        return (p.X, p.Y, s.Width, s.Height);
    }
    public static void SetLocked(Window window, bool locked)
    {
        if (window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)
        {
            p.IsResizable = !locked;
            p.IsMaximizable = !locked;
        }
        var h = Handle(window);
        SetProp(h, "Shige.Locked", locked ? (IntPtr)1 : IntPtr.Zero);
        if (!LockedWindows.ContainsKey(h))
        {
            SubclassProc proc = LockProc;
            LockedWindows[h] = proc;
            SetWindowSubclass(h, proc, 44, 0);
        }
    }
    private static readonly Dictionary<IntPtr, SubclassProc> LockedWindows = new();
    private static IntPtr LockProc(IntPtr h, uint m, IntPtr w, IntPtr l, nuint id, nuint data)
    {
        if (m == 0x112 && GetProp(h, "Shige.Locked") != IntPtr.Zero && ((w.ToInt64() & 0xfff0) == 0xf010 || (w.ToInt64() & 0xfff0) == 0xf000))
            return IntPtr.Zero;
        if (m == 0x82)
        {
            RemoveWindowSubclass(h, LockedWindows[h], 44);
            LockedWindows.Remove(h);
        }
        return DefSubclassProc(h, m, w, l);
    }
    public static void ClampToScreen(Window window)
    {
        var h = Handle(window);
        var mi = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(h, 2), ref mi))
            return;
        var b = Bounds(window);
        var width = Math.Min(b.Width, mi.Work.Right - mi.Work.Left);
        var height = Math.Min(b.Height, mi.Work.Bottom - mi.Work.Top);
        window.AppWindow.MoveAndResize(new RectInt32(Math.Clamp(b.X, mi.Work.Left, mi.Work.Right - width), Math.Clamp(b.Y, mi.Work.Top, mi.Work.Bottom - height), width, height));
    }
    public static void Snap(Window window)
    {
        var b = Bounds(window);
        window.AppWindow.Move(new PointInt32((int)Math.Round(b.X / 16d) * 16, (int)Math.Round(b.Y / 16d) * 16));
        ClampToScreen(window);
    }
    public static void Show(Window window)
    {
        ShowWindow(Handle(window), 9);
        window.Activate();
        SetForegroundWindow(Handle(window));
    }
    public static void Hide(Window window) => ShowWindow(Handle(window), 0);
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size; public Rect Monitor, Work; public uint Flags;
    }
    internal delegate IntPtr SubclassProc(IntPtr h, uint m, IntPtr w, IntPtr l, nuint id, nuint data);
    [DllImport("comctl32.dll")] internal static extern bool SetWindowSubclass(IntPtr h, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] internal static extern bool RemoveWindowSubclass(IntPtr h, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] internal static extern IntPtr DefSubclassProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr h, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetProp(IntPtr h, string name, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetProp(IntPtr h, string name);
}
