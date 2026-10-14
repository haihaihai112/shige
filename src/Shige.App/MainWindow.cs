using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shige.App.Platform;
using Shige.Core;

namespace Shige.App;

internal sealed class MainWindow : Window
{
    private readonly AppController _app;
    private readonly StackPanel _body = UI.Stack(16);
    private readonly StackPanel _boxList = UI.Stack(8);
    public MainWindow(AppController app)
    {
        _app = app; Title = "拾格 · 收纳中心"; UI.Backdrop(this);
        NativeWindowService.Place(this, 240, 120, 820, 680, false);
        var outer = UI.Stack(20); outer.Margin = new Thickness(28);
        var title = UI.Row(12); title.Children.Add(UI.Text("▦", 36));
        var brand = UI.Stack(2); brand.Children.Add(UI.Text("拾格", 28)); brand.Children.Add(UI.Text("让文件各有其位", 13, .65)); title.Children.Add(brand); outer.Children.Add(title);
        var nav = UI.Row(); nav.Children.Add(UI.Button("我的格子", BuildHome));
        nav.Children.Add(UI.Button("整理桌面", () => _app.Command("organize")));
        nav.Children.Add(UI.Button("设置", ShowSettings)); outer.Children.Add(nav); outer.Children.Add(_body);
        var scroll = new ScrollViewer { Content = outer, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled };
        scroll.SizeChanged += (_, e) => outer.Width = Math.Max(0, e.NewSize.Width - 56);
        Content = scroll;
        AppWindow.Closing += (_, e) => { e.Cancel = true; NativeWindowService.Hide(this); };
        BuildHome();
    }
    private void BuildHome()
    {
        _body.Children.Clear();
        var actions = UI.Row();
        actions.Children.Add(UI.Button("＋ 新建收纳格子", () => _ = _app.CreateBoxAsync(this, false), true));
        actions.Children.Add(UI.Button("映射已有文件夹", () => _ = _app.CreateBoxAsync(this, true))); _body.Children.Add(actions);
        _body.Children.Add(UI.Text("格子展示真实文件夹。改标题不会重命名目录，移除格子不会删除文件。", 13, .68));
        _body.Children.Add(_boxList); RefreshBoxes();
        _body.Children.Add(UI.Text("托盘或 F7 显示 / 隐藏全部格子。关闭此窗口后，拾格仍在托盘运行。", 12, .6));
        _body.Children.Add(UI.Button("退出拾格", () => _ = _app.ExitAsync()));
    }
    public void RefreshBoxes()
    {
        _boxList.Children.Clear();
        if (_app.Settings.Boxes.Count == 0)
        {
            var empty = UI.Stack(); empty.Children.Add(UI.Text("创建你的第一个格子", 22));
            empty.Children.Add(UI.Text("新建一个收纳文件夹，或直接展示已有文件夹。桌面文件不会自动移动。", 14, .7));
            _boxList.Children.Add(UI.Card(empty));
        }
        foreach (var box in _app.Settings.Boxes)
        {
            var details = UI.Stack(6); details.Children.Add(UI.Text(box.Title, 18));
            details.Children.Add(UI.Text(box.FolderPath + (Directory.Exists(box.FolderPath) ? "" : "  · 位置不可用"), 12, .65));
            var row = UI.Row(); row.Children.Add(UI.Button("显示格子", () => _app.OpenBox(box)));
            row.Children.Add(UI.Button("打开文件夹", () => Guard(() => ShellFiles.Open(box.FolderPath))));
            row.Children.Add(UI.Button("移除格子", () => _ = _app.RemoveAsync(this, box))); details.Children.Add(row);
            _boxList.Children.Add(UI.Card(details));
        }
    }
    public void ShowSettings()
    {
        _body.Children.Clear();
        var selector = new ComboBox { Header = "设置", ItemsSource = new[] { "常规", "格子", "整理" }, SelectedIndex = 0, MinWidth = 180 };
        var page = UI.Stack(12); _body.Children.Add(selector); _body.Children.Add(page);
        selector.SelectionChanged += (_, _) => BuildSettingsPage(page, selector.SelectedIndex);
        BuildSettingsPage(page, 0);
    }
    private void BuildSettingsPage(StackPanel page, int index)
    {
        page.Children.Clear();
        if (index == 0)
        {
            page.Children.Add(UI.Text("常规", 22));
            var startup = new ToggleSwitch { Header = "开机启动", IsOn = ShellFiles.GetStartup() };
            startup.Toggled += (_, _) => Guard(() => ShellFiles.SetStartup(startup.IsOn)); page.Children.Add(startup);
            var f7 = new ToggleSwitch { Header = "启用 F7 显示 / 隐藏", IsOn = _app.Settings.EnableF7 };
            f7.Toggled += (_, _) => { _app.Settings.EnableF7 = f7.IsOn; _app.ApplySettings(); }; page.Children.Add(f7);
            page.Children.Add(UI.Text("收纳目录（与配置分开）", 14));
            page.Children.Add(UI.Text(_app.Settings.StorageRoot, 12, .7));
            page.Children.Add(UI.Button("选择收纳目录", () => Guard(() =>
            {
                var path = ShellFiles.PickFolder(NativeWindowService.Handle(this), "选择新格子的收纳根目录");
                if (path is null) return;
                PathPolicy.ValidateStorageRoot(path, _app.DesktopPath, _app.Store.DataDirectory);
                _app.Settings.StorageRoot = path; _app.Save(); BuildSettingsPage(page, 0);
            })));
            page.Children.Add(UI.Text("更改根目录仅影响之后创建的格子，已有文件不会搬迁。", 12, .65));
            var row = UI.Row();
            row.Children.Add(UI.Button("导出配置", async () =>
            { try { var file = await UI.SaveFile(this, "Shige-settings", ".json"); if (file is not null) { _app.Save(); _app.Store.Export(file); await UI.Notice(this, "已导出", "仅包含配置，不包含收纳文件与整理事务。"); } } catch (Exception e) { await UI.Notice(this, "导出失败", e.Message); } }));
            row.Children.Add(UI.Button("恢复配置", async () =>
            {
                if (_app.Busy) { await UI.Notice(this, "请稍候", "请先完成文件整理。"); return; }
                try { var file = await UI.OpenFile(this); if (file is not null && await UI.Confirm(this, "恢复配置？", "将替换当前格子显示与设置，不移动或删除文件。", "恢复")) { await _app.RestoreAsync(file); ShowSettings(); } } catch (Exception e) { await UI.Notice(this, "恢复失败", e.Message); }
            })); page.Children.Add(row);
            page.Children.Add(UI.Button("打开日志目录", () => Guard(() => ShellFiles.Open(_app.Store.DataDirectory))));
            page.Children.Add(UI.Text("拾格 0.1.0 · 本地收纳学习版", 12, .65));
        }
        else if (index == 1)
        {
            page.Children.Add(UI.Text("格子", 22));
            var icons = new ComboBox { Header = "图标大小", ItemsSource = new[] { "小", "中", "大" }, SelectedIndex = _app.Settings.IconSize <= 48 ? 0 : _app.Settings.IconSize >= 80 ? 2 : 1, MinWidth = 180 };
            icons.SelectionChanged += (_, _) => { _app.Settings.IconSize = new[] { 48, 64, 80 }[icons.SelectedIndex]; _app.ApplySettings(); }; page.Children.Add(icons);
            var snap = new ToggleSwitch { Header = "移动后吸附到网格", IsOn = _app.Settings.Snap };
            snap.Toggled += (_, _) => { _app.Settings.Snap = snap.IsOn; _app.Save(); }; page.Children.Add(snap);
            var locked = new ToggleSwitch { Header = "新格子默认锁定位置", IsOn = _app.Settings.DefaultLocked };
            locked.Toggled += (_, _) => { _app.Settings.DefaultLocked = locked.IsOn; _app.Save(); }; page.Children.Add(locked);
            var width = new NumberBox { Header = "新格子宽度（像素）", Minimum = 280, Maximum = 1600, Value = _app.Settings.DefaultWidth, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            var height = new NumberBox { Header = "新格子高度（像素）", Minimum = 220, Maximum = 1400, Value = _app.Settings.DefaultHeight, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            width.ValueChanged += (_, _) => { if (double.IsFinite(width.Value)) { _app.Settings.DefaultWidth = (int)width.Value; _app.Save(); } };
            height.ValueChanged += (_, _) => { if (double.IsFinite(height.Value)) { _app.Settings.DefaultHeight = (int)height.Value; _app.Save(); } };
            page.Children.Add(width); page.Children.Add(height);
            page.Children.Add(UI.Text("现有格子", 16));
            foreach (var box in _app.Settings.Boxes)
            { var row = UI.Row(); row.Children.Add(UI.Button(box.Title + " · 显示", () => _app.OpenBox(box))); row.Children.Add(UI.Button("移除", () => _ = _app.RemoveAsync(this, box))); page.Children.Add(row); }
        }
        else
        {
            page.Children.Add(UI.Text("整理", 22));
            page.Children.Add(UI.Text("只在确认预览后移动个人桌面的普通文件。文件夹、隐藏/系统文件和临时下载文件会保留。", 13, .7));
            foreach (var category in Enum.GetValues<FileCategory>())
            {
                var options = new List<TargetChoice> { new("新建收纳格子", "") };
                options.AddRange(_app.Settings.Boxes.Select(b => new TargetChoice(b.Title + " · " + b.FolderPath, b.FolderPath)));
                var combo = new ComboBox { Header = Classifier.DisplayName(category), ItemsSource = options, DisplayMemberPath = "Title", MinWidth = 300, MaxWidth = 650 };
                _app.Settings.CategoryTargets.TryGetValue(category, out var target);
                combo.SelectedItem = options.FirstOrDefault(o => string.Equals(o.Path, target, StringComparison.OrdinalIgnoreCase)) ?? options[0];
                combo.SelectionChanged += (_, _) => { var selected = ((TargetChoice)combo.SelectedItem).Path; if (string.IsNullOrEmpty(selected)) _app.Settings.CategoryTargets.Remove(category); else _app.Settings.CategoryTargets[category] = selected; _app.Save(); };
                page.Children.Add(combo);
            }
            var row = UI.Row(); row.Children.Add(UI.Button("整理桌面", () => _app.Command("organize"), true));
            row.Children.Add(UI.Button("撤销最近整理", () => _ = _app.UndoAsync(this))); page.Children.Add(row);
            page.Children.Add(UI.Button("查看整理记录目录", () => Guard(() => ShellFiles.Open(_app.Store.DataDirectory))));
            page.Children.Add(UI.Button("检查未完成的整理事务", () => _ = _app.RecoverAsync(this)));
        }
    }
    private async void Guard(Action action) { try { action(); } catch (Exception e) { await UI.Notice(this, "操作未完成", e.Message); } }
}
internal sealed record TargetChoice(string Title, string Path);
