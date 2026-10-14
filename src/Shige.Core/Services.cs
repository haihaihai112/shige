using System.Security.Cryptography;
using System.Text.Json;
namespace Shige.Core;

public static class Classifier
{
    public static FileCategory GetCategory(string name)
    {
        var e = Path.GetExtension(name).ToLowerInvariant();
        return e switch
        {
            ".lnk" or ".url" => FileCategory.Shortcuts,
            ".txt" or ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".md" or ".csv" or ".rtf" => FileCategory.Documents,
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".svg" or ".heic" => FileCategory.Images,
            ".mp3" or ".mp4" or ".wav" or ".flac" or ".avi" or ".mkv" or ".mov" or ".m4a" => FileCategory.Media,
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => FileCategory.Archives,
            _ => FileCategory.Other
        };
    }
    public static string DisplayName(FileCategory c) => c switch { FileCategory.Shortcuts => "快捷方式", FileCategory.Documents => "文档", FileCategory.Images => "图片", FileCategory.Media => "音视频", FileCategory.Archives => "压缩包", _ => "其他" };
}
public static class PathPolicy
{
    internal static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    internal static bool Within(string path, string parent) => string.Equals(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar), Comparison) || Path.GetFullPath(path).StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, Comparison);
    public static void ValidateStorageRoot(string root, string desktop, string appData)
    {
        var p = Path.GetFullPath(root);
        if (p == Path.GetPathRoot(p) || Within(p, desktop) || Within(desktop, p) || Within(p, appData) || Within(appData, p))
            throw new IOException("真实文件目录不得与桌面、配置目录或磁盘根目录重叠。");
        EnsureNoLinks(p);
    }
    internal static void EnsureNoLinks(string path)
    {
        for (string? p = Path.GetFullPath(path); p != null; p = Path.GetDirectoryName(p))
        {
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("拒绝符号链接或重解析路径: " + p);
        }
    }
    public static string UniqueDestination(string folder, string fileName)
    {
        if (fileName != Path.GetFileName(fileName) || string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("文件名无效");
        EnsureNoLinks(folder);
        var path = Path.Combine(folder, fileName);
        for (int n = 2; File.Exists(path) || Directory.Exists(path); n++)
            path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(fileName)} ({n}){Path.GetExtension(fileName)}");
        return path;
    }
}
internal static class DurableJson
{
    internal static JsonSerializerOptions Options = new() { WriteIndented = true };
    internal static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PathPolicy.EnsureNoLinks(path);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, Options);
                stream.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? throw new IOException("JSON 内容为空");
}
public class SettingsStore
{
    public string DataDirectory
    {
        get;
    }
    private string FilePath => Path.Combine(DataDirectory, "settings.json");
    public SettingsStore(string dataDirectory)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
        PathPolicy.EnsureNoLinks(DataDirectory);
        Directory.CreateDirectory(DataDirectory);
    }
    static AppSettings Validate(AppSettings s)
    {
        if (s.Boxes == null || s.CategoryTargets == null || string.IsNullOrWhiteSpace(s.StorageRoot) || !Path.IsPathFullyQualified(s.StorageRoot) || s.IconSize < 16 || s.IconSize > 256 || s.DefaultWidth < 100 || s.DefaultHeight < 100 || s.Boxes.Any(b => b == null || b.ManualOrder == null || string.IsNullOrWhiteSpace(b.Id) || string.IsNullOrWhiteSpace(b.FolderPath) || !Path.IsPathFullyQualified(b.FolderPath) || !Enum.IsDefined(b.Sort) || b.Width < 100 || b.Height < 100))
            throw new IOException("配置内容不合法");
        if (s.Boxes.Select(b => b.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != s.Boxes.Count || s.CategoryTargets.Any(t => !Enum.IsDefined(t.Key) || t.Value == null || (t.Value.Length > 0 && (string.IsNullOrWhiteSpace(t.Value) || !Path.IsPathFullyQualified(t.Value)))))
            throw new IOException("配置包含重复标识或无效分类目录");
        PathPolicy.EnsureNoLinks(s.StorageRoot);
        foreach (var box in s.Boxes)
            PathPolicy.EnsureNoLinks(box.FolderPath);
        return s;
    }
    public AppSettings Load()
    {
        foreach (var path in new[] { FilePath, FilePath + ".bak" })
        {
            if (!File.Exists(path))
                continue;
            try
            {
                return Validate(DurableJson.Read<AppSettings>(path));
            }
            catch (Exception ex) { Log("配置读取失败: " + ex.Message); }
        }
        return new();
    }
    public void Save(AppSettings value)
    {
        Validate(value);
        if (File.Exists(FilePath))
        {
            AppSettings? previous = null;
            try
            {
                previous = Validate(DurableJson.Read<AppSettings>(FilePath));
            }
            catch (JsonException) { Log("主配置损坏，保留已有备份。"); }
            catch (IOException) { Log("主配置无效，保留已有备份。"); }
            if (previous != null)
                DurableJson.Write(FilePath + ".bak", previous);
        }
        DurableJson.Write(FilePath, value);
    }
    public void Export(string path) => DurableJson.Write(Path.GetFullPath(path), Load());
    public AppSettings Import(string path)
    {
        var settings = Validate(DurableJson.Read<AppSettings>(path));
        Save(settings);
        return settings;
    }
    public void Log(string text)
    {
        try
        {
            File.AppendAllText(Path.Combine(DataDirectory, "shige.log"), $"{DateTimeOffset.Now:O} {text}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}




