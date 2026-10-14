using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace Shige.Core;

internal static class NativeSafety
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StreamData
    {
        public long Size; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string Name;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint FindFirstStreamW(string path, int level, out StreamData data, int flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool FindNextStreamW(nint handle, out StreamData data);
    [DllImport("kernel32.dll")] static extern bool FindClose(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFileW(string name, uint access, uint sharing, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref int information, uint size);
    internal static List<string> AlternateStreams(string path)
    {
        var names = new List<string>();
        if (!OperatingSystem.IsWindows())
            return names;
        var h = FindFirstStreamW(path, 0, out var data, 0);
        if (h == new nint(-1))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 38 or 1)
                return names;
            throw new IOException("无法确认文件数据流", new Win32Exception(error));
        }
        try
        {
            do
            {
                if (data.Name != "::$DATA")
                    names.Add(data.Name);
            } while (FindNextStreamW(h, out data));
            var error = Marshal.GetLastWin32Error();
            if (error != 38)
                throw new Win32Exception(error);
        }
        finally { FindClose(h); }
        return names;
    }
    internal static void RejectAlternateStreams(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new IOException("跨文件系统移动暂不支持当前操作系统；源文件保留。");
        if (AlternateStreams(path).Count > 0)
            throw new IOException("文件含备用数据流，跨盘移动被拒绝以保留完整内容。");
    }
    internal static FileStream OpenForVerifiedDeletion(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new IOException("当前平台不支持安全跨盘源删除");
        var h = CreateFileW(path, 0x80000000 | 0x00010000, 1, 0, 3, 0x00200000, 0);
        if (h.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new FileStream(h, FileAccess.Read);
    }
    internal static void DeleteOpened(FileStream source)
    {
        int delete = 1;
        if (!SetFileInformationByHandle(source.SafeFileHandle, 4, ref delete, 4))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    internal static bool SameVolume(string source, string destination)
    {
        if (OperatingSystem.IsWindows())
            return string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase);
        string Volume(string p) => DriveInfo.GetDrives().Where(d => PathPolicy.Within(p, d.RootDirectory.FullName)).OrderByDescending(d => d.RootDirectory.FullName.Length).FirstOrDefault()?.Name ?? "/";
        return Volume(source) == Volume(destination);
    }
}


