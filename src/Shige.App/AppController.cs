using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shige.App.Platform;
using Shige.Core;

namespace Shige.App;

internal sealed class AppController
{
    public static AppController? Current { get; private set; }
    public SettingsStore Store { get; }
    public AppSettings Settings { get; private set; }
    public Organizer Organizer { get; }
    public string DesktopPath { get; } = Environment.GetEnvironmentVariable("SHIGE_DESKTOP_DIR") ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public MainWindow Main { get; private set; } = null!;
    private DesktopHost? _host;
    private readonly Dictionary<string, BoxWindow> _boxes = [];
    private bool _visible = true, _quitting;
    public bool Busy { get; set; }
    public int FileOperations { get; set; }

    public AppController()
    {
        Current = this;
        var dataRoot = Environment.GetEnvironmentVariable("SHIGE_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Shige");
        Store = new SettingsStore(dataRoot);
        Settings = Store.Load();
        if (Environment.GetEnvironmentVariable("SHIGE_STORAGE_ROOT") is { } root) Settings.StorageRoot = root;
        Organizer = new Organizer(dataRoot);
        Application.Current.UnhandledException += (_, e) =>
        {
            Store.Log(e.Exception.ToString());
            // Startup errors must not leave an invisible instance holding the singleton lock.
            if (Main is null) { e.Handled = false; return; }
            e.Handled = true;
            _ = ReportAsync("操作未完成", e.Exception.Message);
        };
    }

    public async Task StartAsync()
    {
        Main = new MainWindow(this); Main.Activate();
        _host = new DesktopHost(NativeWindowService.Handle(Main), Toggle, Show,
            command => Main.DispatcherQueue.TryEnqueue(() => Command(command)),
            () => Main.DispatcherQueue.TryEnqueue(() => { foreach (var box in _boxes.Values) NativeWindowService.ClampToScreen(box); }));
        _host.SetHotkey(Settings.EnableF7);
        Busy = true;
        var recovery = await Organizer.RecoverAsync();
        Busy = false;
        foreach (var box in Settings.Boxes.ToArray()) OpenBox(box);
        Refresh();
        if (recovery.Messages.Count > 0) await ReportAsync("整理恢复记录", string.Join("\n", recovery.Messages));
        if (_host.HotkeyError is { } error) await ReportAsync("F7 暂不可用", error + "\n可通过托盘显示或隐藏格子。");
    }
    public void Save() { try { Store.Save(Settings); } catch (Exception e) { Store.Log(e.ToString()); _ = ReportAsync("配置保存失败", e.Message); } }
    public void Refresh()
    {
        Main.RefreshBoxes(); _host?.SetCanUndo(Organizer.CanUndo);
    }
    public void ApplySettings()
    {
        Save(); _host?.SetHotkey(Settings.EnableF7);
        foreach (var box in _boxes.Values) box.ApplySettings();
    }
    public async void Command(string value)
    {
        try
        {
            switch (value)
            {
                case "new": await CreateBoxAsync(Main, false); break;
                case "map": await CreateBoxAsync(Main, true); break;
                case "organize": Show(); new OrganizeWindow(this).Activate(); break;
                case "undo": await UndoAsync(Main); break;
                case "toggle": Toggle(); break;
                case "root": Directory.CreateDirectory(Settings.StorageRoot); ShellFiles.Open(Settings.StorageRoot); break;
                case "settings": Show(); Main.ShowSettings(); break;
                case "exit": await ExitAsync(); break;
            }
        }
        catch (Exception e) { await ReportAsync("操作未完成", e.Message); }
    }
    public async Task CreateBoxAsync(Window owner, bool mapped)
    {
        try
        {
            string? folder;
            string title;
            if (mapped)
            {
                folder = ShellFiles.PickFolder(NativeWindowService.Handle(owner), "选择要显示的文件夹");
                if (folder is null) return;
                title = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
                if (string.IsNullOrEmpty(title)) title = folder;
            }
            else
            {
                var name = await UI.Input(owner, "新建收纳格子", "格子名称（创建真实文件夹）");
                if (string.IsNullOrWhiteSpace(name)) return;
                if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ')) throw new IOException("请使用有效的文件夹名称。");
                PathPolicy.ValidateStorageRoot(Settings.StorageRoot, DesktopPath, Store.DataDirectory);
                title = name;
                folder = PathPolicy.UniqueDestination(Settings.StorageRoot, name);
                Directory.CreateDirectory(folder);
            }
            AddBox(title, folder, !mapped);
        }
        catch (Exception e) { await UI.Notice(owner, "创建失败", e.Message); }
    }
    public BoxSettings AddBox(string title, string folder, bool managed)
    {
        var existing = Settings.Boxes.FirstOrDefault(b => string.Equals(Path.GetFullPath(b.FolderPath), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase));
        if (existing is not null) { OpenBox(existing); return existing; }
        var offset = Settings.Boxes.Count % 6 * 28;
        var box = new BoxSettings
        {
            Title = title,
            FolderPath = folder,
            IsManaged = managed,
            Width = Settings.DefaultWidth,
            Height = Settings.DefaultHeight,
            Locked = Settings.DefaultLocked,
            X = 120 + offset,
            Y = 120 + offset
        };
        Settings.Boxes.Add(box); Save(); OpenBox(box); Refresh(); return box;
    }
    public void OpenBox(BoxSettings settings)
    {
        if (_boxes.TryGetValue(settings.Id, out var existing)) { NativeWindowService.Show(existing); return; }
        var window = new BoxWindow(this, settings); _boxes.Add(settings.Id, window);
        window.Closed += (_, _) => _boxes.Remove(settings.Id);
        window.Activate();
    }
    public async Task RemoveAsync(Window owner, BoxSettings box)
    {
        if (!await UI.Confirm(owner, "移除格子？", $"仅移除格子显示与配置，文件夹及文件将保留：\n{box.FolderPath}", "移除格子")) return;
        Settings.Boxes.Remove(box); if (_boxes.Remove(box.Id, out var window)) window.CloseForRemoval(); Save(); Refresh();
    }
    public void Show()
    {
        NativeWindowService.Show(Main);
        foreach (var settings in Settings.Boxes) OpenBox(settings);
        _visible = true;
    }
    public void Toggle()
    {
        if (_visible) { foreach (var box in _boxes.Values) NativeWindowService.Hide(box); NativeWindowService.Hide(Main); _visible = false; }
        else Show();
    }
    public async Task UndoAsync(Window owner)
    {
        if (Busy) { await UI.Notice(owner, "请稍候", "已有文件整理或撤销正在执行。"); return; }
        if (!Organizer.CanUndo) { await UI.Notice(owner, "暂无可撤销整理", "手动整理完成后可在这里撤销。"); return; }
        if (!await UI.Confirm(owner, "撤销最近整理？", "仅还原内容未变化且原位置为空的文件。发生冲突的文件会保留并报告。", "撤销整理")) return;
        Busy = true;
        try { var result = await Organizer.UndoAsync(); await UI.Notice(owner, "撤销结果", ResultText(result)); }
        catch (Exception e) { await UI.Notice(owner, "撤销未完成", e.Message); }
        finally { Busy = false; Refresh(); }
    }
    public async Task RecoverAsync(Window owner)
    {
        if (Busy || FileOperations > 0) { await UI.Notice(owner, "请稍候", "请等待当前文件操作完成。"); return; }
        Busy = true;
        try { var result = await Organizer.RecoverAsync(); await UI.Notice(owner, "事务恢复结果", ResultText(result)); }
        finally { Busy = false; Refresh(); }
    }
    public static string ResultText(OrganizationResult result) => $"完成 {result.Completed} 项，失败 {result.Failed} 项{(result.Cancelled ? "，已取消剩余操作" : "")}。\n" + string.Join("\n", result.Messages.Take(100));
    public async Task RestoreAsync(string file)
    {
        Settings = Store.Import(file);
        foreach (var window in _boxes.Values.ToArray()) window.CloseForRemoval();
        _boxes.Clear(); foreach (var box in Settings.Boxes) OpenBox(box); ApplySettings(); Refresh();
        await ReportAsync("配置已恢复", "只恢复格子、布局与偏好，未移动文件或更改整理事务。");
    }
    public Task ReportAsync(string title, string message) { Store.Log(title + ": " + message); return UI.Notice(Main, title, message); }
    public async Task ExitAsync()
    {
        if (_quitting) return;
        if (Busy || FileOperations > 0) { await ReportAsync("正在处理文件", "请先取消或等待文件操作完成。"); return; }
        _quitting = true; Save(); _host?.Dispose();
        foreach (var box in _boxes.Values.ToArray()) box.CloseForRemoval();
        Application.Current.Exit();
    }
}
