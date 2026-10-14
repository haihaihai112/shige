namespace Shige.Core;

public enum FileCategory
{
    Shortcuts, Documents, Images, Media, Archives, Other
}
public enum SortMode
{
    Manual, Name, Type, Modified
}
public class BoxSettings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "新格子";
    public string FolderPath { get; set; } = "";
    public bool IsManaged
    {
        get; set;
    }
    public int X { get; set; } = 100;
    public int Y { get; set; } = 100;
    public int Width { get; set; } = 380;
    public int Height { get; set; } = 420;
    public bool Locked
    {
        get; set;
    }
    public SortMode Sort
    {
        get; set;
    }
    public List<string> ManualOrder { get; set; } = [];
}
public class AppSettings
{
    public List<BoxSettings> Boxes { get; set; } = [];
    public string StorageRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ShigeFiles");
    public bool EnableF7 { get; set; } = true;
    public bool Snap { get; set; } = true;
    public int IconSize { get; set; } = 64;
    public int DefaultWidth { get; set; } = 380;
    public int DefaultHeight { get; set; } = 420;
    public bool DefaultLocked
    {
        get; set;
    }
    public Dictionary<FileCategory, string> CategoryTargets { get; set; } = [];
}
public class PlanItem
{
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public FileCategory Category
    {
        get; set;
    }
    public bool Included { get; set; } = true;
    public string Note { get; set; } = "";
}
public class OrganizationProgress
{
    public int Completed
    {
        get; set;
    }
    public int Total
    {
        get; set;
    }
    public string Current { get; set; } = "";
}
public class OrganizationResult
{
    public int Completed
    {
        get; set;
    }
    public int Failed
    {
        get; set;
    }
    public bool Cancelled
    {
        get; set;
    }
    public List<string> Messages { get; set; } = [];
}
