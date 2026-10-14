using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
namespace Shige.App.Platform;

public sealed class FileOperationResult
{
    public int Completed
    {
        get; set;
    }
    public List<string> Errors { get; } = [];
}
public static class ShellFiles
{
    public static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    public static void Reveal(string path) => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Path.GetFullPath(path)}\"") { UseShellExecute = true });
    public static void Properties(string path)
    {
        var info = new ShellExecuteInfo { Size = Marshal.SizeOf<ShellExecuteInfo>(), Mask = 12, Verb = "properties", File = Path.GetFullPath(path), Show = 1 };
        if (!ShellExecuteEx(ref info))
            throw new System.ComponentModel.Win32Exception();
    }
    public static Task<FileOperationResult> TransferAsync(
        IEnumerable<string> paths,
        string destination,
        bool move,
        CancellationToken token = default) => Task.Run(() =>
    {
        var result = new FileOperationResult();
        Directory.CreateDirectory(destination);
        foreach (var item in paths)
        {
            if (token.IsCancellationRequested)
                break;
            try
            {
                var source = Path.GetFullPath(item);
                var destinationFolder = Path.GetFullPath(destination);
                if (destinationFolder.Equals(source, StringComparison.OrdinalIgnoreCase) ||
                    destinationFolder.StartsWith(source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("目标不能是源目录或其子目录。");
                RejectReparse(source);
                var candidate = Path.Combine(destinationFolder, Path.GetFileName(source));
                // Moving an item back to its current folder must not rename it.
                if (move && candidate.Equals(source, StringComparison.OrdinalIgnoreCase))
                    continue;
                var target = Unique(candidate);
                if (Directory.Exists(source))
                {
                    if (move)
                    {
                        if (!SameVolume(source, target))
                            throw new IOException("暂不支持跨卷移动文件夹，请先复制并核验。");
                        Directory.Move(source, target);
                    }
                    else
                    {
                        var stage = Path.Combine(destinationFolder, ".shige-copy-" + Guid.NewGuid().ToString("N"));
                        CopyDirectory(source, stage, token);
                        Directory.Move(stage, target);
                    }
                }
                else if (move)
                {
                    if (SameVolume(source, target))
                        File.Move(source, target, false);
                    else
                        MoveAcrossVolumes(source, target, token);
                }
                else
                {
                    File.Copy(source, target, false);
                }
                result.Completed++;
            }
            catch (Exception error)
            {
                result.Errors.Add($"{item}：{error.Message}");
            }
        }
        return result;
    }, CancellationToken.None);

    static bool SameVolume(string source, string target) =>
        string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase);

    static void MoveAcrossVolumes(string source, string target, CancellationToken token)
    {
        var stage = target + ".shige-part-" + Guid.NewGuid().ToString("N");
        // Open with DELETE access and no write/delete sharing so neither contents nor path can change.
        using var handle = CreateFile(source, 0x80010000, 1, IntPtr.Zero, 3, 0x08000000, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        RejectAlternateStreams(source);
        var metadata = new FileInfo(source);
        var created = metadata.CreationTimeUtc;
        var written = metadata.LastWriteTimeUtc;
        var accessed = metadata.LastAccessTimeUtc;
        var attributes = metadata.Attributes;
        if ((attributes & (System.IO.FileAttributes.Encrypted | System.IO.FileAttributes.Compressed)) != 0)
            throw new IOException("跨卷移动暂不支持加密或压缩文件；源文件保留。");
        using var input = new FileStream(handle, FileAccess.Read);
        using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                token.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
            }
            output.Flush(true);
        }
        input.Position = 0;
        var original = System.Security.Cryptography.SHA256.HashData(input);
        using (var copied = File.OpenRead(stage))
            if (!original.SequenceEqual(System.Security.Cryptography.SHA256.HashData(copied)))
                throw new IOException("跨卷副本校验失败，源文件保留。");
        File.SetCreationTimeUtc(stage, created);
        File.SetLastWriteTimeUtc(stage, written);
        File.SetLastAccessTimeUtc(stage, accessed);
        File.SetAttributes(stage, attributes);
        token.ThrowIfCancellationRequested();
        File.Move(stage, target, false);
        using var published = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!original.SequenceEqual(System.Security.Cryptography.SHA256.HashData(published)))
            throw new IOException("目标在发布后发生变化，源文件保留。");
        byte disposition = 1;
        if (!SetFileInformationByHandle(handle, 4, ref disposition, 1))
            throw new IOException("副本已校验；源文件无法移除，已保留两份。", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int infoClass, ref byte info, uint size);
    static void RejectAlternateStreams(string source)
    {
        var find = FindFirstStreamW(source, 0, out var data, 0);
        if (find == (IntPtr)(-1))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 38)
                return;
            throw new IOException("无法验证文件数据流，跨卷移动已拒绝。", new System.ComponentModel.Win32Exception(error));
        }
        try
        {
            do
            {
                if (data.Name != "::$DATA")
                    throw new IOException("文件包含备用数据流，跨卷移动已拒绝；源文件保留。");
            } while (FindNextStreamW(find, out data));
            if (Marshal.GetLastWin32Error() != 38)
                throw new IOException("无法完整检查文件数据流，跨卷移动已拒绝。");
        }
        finally { FindClose(find); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StreamData
    {
        public long Size; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string Name;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr FindFirstStreamW(string path, int level, out StreamData data, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool FindNextStreamW(IntPtr find, out StreamData data);
    [DllImport("kernel32.dll")] static extern bool FindClose(IntPtr find);
    static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return path;
        var parent = Path.GetDirectoryName(path)!;
        var extension = Path.GetExtension(path);
        var stem = Path.GetFileNameWithoutExtension(path);
        for (int i = 2; ; i++)
        {
            var p = Path.Combine(parent, $"{stem} ({i}){extension}");
            if (!File.Exists(p) && !Directory.Exists(p))
                return p;
        }
    }
    static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & System.IO.FileAttributes.ReparsePoint) != 0)
            throw new IOException("为避免链接递归，暂不支持重解析点。");
        if (Directory.Exists(path))
            foreach (var p in Directory.EnumerateFileSystemEntries(path))
                RejectReparse(p);
    }
    static void CopyDirectory(string source, string target, CancellationToken token)
    {
        Directory.CreateDirectory(target);
        foreach (var p in Directory.EnumerateFileSystemEntries(source))
        {
            token.ThrowIfCancellationRequested();
            var dst = Path.Combine(target, Path.GetFileName(p));
            if (Directory.Exists(p))
                CopyDirectory(p, dst, token);
            else
                File.Copy(p, dst, false);
        }
    }
    public static void SetClipboard(IEnumerable<string> paths, bool cut)
    {
        var items = new List<IStorageItem>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
                items.Add(StorageFolder.GetFolderFromPathAsync(Path.GetFullPath(p)).AsTask().GetAwaiter().GetResult());
            else
                items.Add(StorageFile.GetFileFromPathAsync(Path.GetFullPath(p)).AsTask().GetAwaiter().GetResult());
        }
        var data = new DataPackage { RequestedOperation = cut ? DataPackageOperation.Move : DataPackageOperation.Copy };
        data.SetStorageItems(items);
        Clipboard.SetContent(data);
    }
    public static async Task<FileOperationResult> PasteAsync(string destination)
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.StorageItems))
            return new();
        var items = await content.GetStorageItemsAsync();
        var result = await TransferAsync(items.Select(i => i.Path), destination, content.RequestedOperation == DataPackageOperation.Move);
        if (result.Errors.Count == 0)
            content.ReportOperationCompleted(content.RequestedOperation);
        return result;
    }
    public static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled)
            key.SetValue("Shige", $"\"{Environment.ProcessPath}\" --startup");
        else
            key.DeleteValue("Shige", false);
    }
    public static bool GetStartup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue("Shige") is string;
    }
    public static string? PickFolder(IntPtr owner, string title)
    {
        var info = new BrowseInfo { Owner = owner, Title = title, Flags = 0x1 | 0x40 | 0x10 };
        var pidl = SHBrowseForFolder(ref info);
        if (pidl == IntPtr.Zero)
            return null;
        try
        {
            var buffer = new System.Text.StringBuilder(32768);
            return SHGetPathFromIDList(pidl, buffer) ? buffer.ToString() : null;
        }
        finally { Marshal.FreeCoTaskMem(pidl); }
    }
    public static Task<FileOperationResult> RecycleAsync(IEnumerable<string> paths)
    {
        var list = paths.ToArray();
        var completion = new TaskCompletionSource<FileOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var result = new FileOperationResult();
            foreach (var path in list)
            {
                try
                {
                    var full = Path.GetFullPath(path);
                    var drive = new DriveInfo(Path.GetPathRoot(full)!);
                    if (full.StartsWith(@"\\") || drive.DriveType != DriveType.Fixed || !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("仅支持本地 NTFS 固定磁盘上的回收站删除。");
                    RejectReparse(full);
                    var operation = (IFileOperation)new FileOperation();
                    try
                    {
                        // RECYCLEONDELETE is documented as recycle rather than permanent delete.
                        // WANTNUKEWARNING is retained; no NOCONFIRMATION or silent permanent fallback.
                        operation.SetOperationFlags(0x00080000 | 0x20000000 | 0x00004000);
                        SHCreateItemFromParsingName(full, IntPtr.Zero, typeof(IShellItem).GUID, out var item);
                        try
                        {
                            var sink = new RecycleSink();
                            operation.DeleteItem(item, sink);
                            operation.PerformOperations();
                            operation.GetAnyOperationsAborted(out var aborted);
                            if (aborted || !sink.Recycled)
                                throw new IOException(sink.Error ?? "回收操作未完成，已拒绝将其计为成功。");
                            result.Completed++;
                        }
                        finally { Marshal.ReleaseComObject(item); }
                    }
                    finally { Marshal.ReleaseComObject(operation); }
                }
                catch (Exception ex) { result.Errors.Add($"{path}：{ex.Message}"); }
            }
            completion.SetResult(result);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task;
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class RecycleSink : IRecycleSink
    {
        public bool Recycled
        {
            get; private set;
        }
        public string? Error
        {
            get; private set;
        }
        public int StartOperations() => 0;
        public int FinishOperations(int result)
        {
            if (result < 0)
                Error = Marshal.GetExceptionForHR(result)?.Message;
            return 0;
        }
        public int PreRenameItem(uint flags, IntPtr item, IntPtr name) => 0;
        public int PostRenameItem(uint flags, IntPtr item, IntPtr name, int result, IntPtr created) => 0;
        public int PreMoveItem(uint flags, IntPtr item, IntPtr destination, IntPtr name) => 0;
        public int PostMoveItem(uint flags, IntPtr item, IntPtr destination, IntPtr name, int result, IntPtr created) => 0;
        public int PreCopyItem(uint flags, IntPtr item, IntPtr destination, IntPtr name) => 0;
        public int PostCopyItem(uint flags, IntPtr item, IntPtr destination, IntPtr name, int result, IntPtr created) => 0;
        public int PreDeleteItem(uint flags, IntPtr item)
        {
            if ((flags & 0x80) != 0)
                return 0;
            Error = "系统未提供回收站删除标志，已拒绝删除。";
            return unchecked((int)0x80004004);
        }
        public int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr created)
        {
            if (result < 0)
            {
                Error = Marshal.GetExceptionForHR(result)?.Message;
                return 0;
            }
            if (created == IntPtr.Zero)
            {
                Error = "系统未返回回收站项目，无法确认回收结果。";
                return 0;
            }
            try
            {
                var recycled = (IShellItem)Marshal.GetObjectForIUnknown(created);
                try
                {
                    recycled.GetDisplayName(0x80058000, out var memory);
                    try
                    {
                        var path = Marshal.PtrToStringUni(memory);
                        Recycled = path?.Contains(@"\$Recycle.Bin\", StringComparison.OrdinalIgnoreCase) == true;
                        if (!Recycled)
                            Error = "系统返回的目标不是已识别的回收站位置。";
                    }
                    finally { Marshal.FreeCoTaskMem(memory); }
                }
                finally { Marshal.ReleaseComObject(recycled); }
            }
            catch (Exception ex) { Error = "无法确认回收站项目：" + ex.Message; }
            return 0;
        }
        public int PreNewItem(uint flags, IntPtr destination, IntPtr name) => 0;
        public int PostNewItem(uint flags, IntPtr destination, IntPtr name, IntPtr template, uint attributes, int result, IntPtr created) => 0;
        public int UpdateProgress(uint total, uint completed) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }
    [ComVisible(true), Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IRecycleSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, IntPtr item, IntPtr name);
        [PreserveSig] int PostRenameItem(uint flags, IntPtr item, IntPtr name, int result, IntPtr created);
        [PreserveSig] int PreMoveItem(uint flags, IntPtr item, IntPtr destination, IntPtr name);
        [PreserveSig] int PostMoveItem(uint flags, IntPtr item, IntPtr destination, IntPtr name, int result, IntPtr created);
        [PreserveSig] int PreCopyItem(uint flags, IntPtr item, IntPtr destination, IntPtr name);
        [PreserveSig] int PostCopyItem(uint flags, IntPtr item, IntPtr destination, IntPtr name, int result, IntPtr created);
        [PreserveSig] int PreDeleteItem(uint flags, IntPtr item);
        [PreserveSig] int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr created);
        [PreserveSig] int PreNewItem(uint flags, IntPtr destination, IntPtr name);
        [PreserveSig] int PostNewItem(uint flags, IntPtr destination, IntPtr name, IntPtr template, uint attributes, int result, IntPtr created);
        [PreserveSig] int UpdateProgress(uint total, uint completed);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct BrowseInfo
    {
        public IntPtr Owner, Root, DisplayName; public string Title; public uint Flags; public IntPtr Callback, Param; public int Image;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShellExecuteInfo
    {
        public int Size; public uint Mask; public IntPtr Window; public string Verb, File; public string? Parameters, Directory; public int Show; public IntPtr Instance, IdList; public string? Class; public IntPtr ClassKey; public uint HotKey; public IntPtr Icon, Process;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool ShellExecuteEx(ref ShellExecuteInfo info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SHBrowseForFolder(ref BrowseInfo info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool SHGetPathFromIDList(IntPtr pidl, System.Text.StringBuilder path);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)] static extern void SHCreateItemFromParsingName(string path, IntPtr context, [MarshalAs(UnmanagedType.LPStruct)] Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
    [ComImport, Guid("3ad05575-8857-4850-9277-11b85bdb8e09")]
    class FileOperation
    {
    }
    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr context, [MarshalAs(UnmanagedType.LPStruct)] Guid handler, [MarshalAs(UnmanagedType.LPStruct)] Guid iid, out IntPtr result); void GetParent(out IShellItem parent); void GetDisplayName(uint kind, out IntPtr name); void GetAttributes(uint mask, out uint attributes); void Compare(IShellItem other, uint hint, out int order);
    }
    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileOperation
    {
        void Advise(IntPtr sink, out uint cookie); void Unadvise(uint cookie); void SetOperationFlags(uint flags); void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message); void SetProgressDialog(IntPtr dialog); void SetProperties(IntPtr properties); void SetOwnerWindow(IntPtr owner); void ApplyPropertiesToItem(IShellItem item); void ApplyPropertiesToItems(IntPtr items); void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink); void RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name); void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IntPtr sink); void MoveItems(IntPtr items, IShellItem destination); void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IntPtr sink); void CopyItems(IntPtr items, IShellItem destination); void DeleteItem(IShellItem item, [MarshalAs(UnmanagedType.Interface)] IRecycleSink sink); void DeleteItems(IntPtr items); void NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? template, IntPtr sink); void PerformOperations(); void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}







