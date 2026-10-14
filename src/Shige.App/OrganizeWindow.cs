using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Shige.App.Platform;
using Shige.Core;

namespace Shige.App;

internal sealed class OrganizeWindow : Window
{
    private readonly AppController _app;
    private readonly Dictionary<FileCategory, ComboBox> _targets = [];
    private readonly StackPanel _preview = UI.Stack(10);
    private readonly Button _run, _scan, _cancel;
    private readonly TextBlock _status = UI.Text("尚未扫描。选择目标后生成预览，不会立即移动文件。", 13, .7);
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Value = 0 };
    private List<PlanItem> _plan = [];
    private CancellationTokenSource? _cancellation;
    private bool _running, _finished;

    public OrganizeWindow(AppController app)
    {
        _app = app; Title = "拾格 · 手动整理桌面"; UI.Backdrop(this);
        NativeWindowService.Place(this, 260, 120, 920, 780, false);
        var content = UI.Stack(16); content.Margin = new Thickness(24);
        content.Children.Add(UI.Text("整理桌面", 26));
        content.Children.Add(UI.Text("先预览，再确认移动", 16));
        content.Children.Add(UI.Text("来源：" + _app.DesktopPath + "\n只整理普通文件，不处理公共桌面、文件夹、隐藏/系统项、临时下载项。", 12, .7));
        var targetGrid = new Grid { ColumnSpacing = 12, RowSpacing = 8 };
        targetGrid.ColumnDefinitions.Add(new ColumnDefinition()); targetGrid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < 3; i++) targetGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        int index = 0;
        foreach (var category in Enum.GetValues<FileCategory>())
        {
            var options = new List<TargetChoice> { new("新建收纳格子 · " + Classifier.DisplayName(category), "") };
            options.AddRange(_app.Settings.Boxes.Select(b => new TargetChoice(b.Title + " · " + b.FolderPath, b.FolderPath)));
            _app.Settings.CategoryTargets.TryGetValue(category, out var path);
            var combo = new ComboBox { Header = Classifier.DisplayName(category), DisplayMemberPath = "Title", ItemsSource = options, HorizontalAlignment = HorizontalAlignment.Stretch };
            combo.SelectedItem = options.FirstOrDefault(o => string.Equals(o.Path, path, StringComparison.OrdinalIgnoreCase)) ?? options[0];
            combo.SelectionChanged += (_, _) => ClearPreview(); _targets[category] = combo;
            Grid.SetColumn(combo, index % 2); Grid.SetRow(combo, index / 2); targetGrid.Children.Add(combo); index++;
        }
        content.Children.Add(targetGrid);
        var actions = UI.Row();
        _scan = UI.Button("生成预览", () => _ = PreviewAsync());
        _run = UI.Button("确认移动", () => _ = ExecuteAsync(), true); _run.IsEnabled = false;
        _cancel = UI.Button("取消剩余操作", () => _cancellation?.Cancel()); _cancel.IsEnabled = false;
        actions.Children.Add(_scan); actions.Children.Add(_run); actions.Children.Add(_cancel); content.Children.Add(actions);
        content.Children.Add(_progress); content.Children.Add(_status); content.Children.Add(_preview);
        content.Children.Add(UI.Button("撤销最近整理", () => _ = _app.UndoAsync(this)));
        var scroll = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled };
        scroll.SizeChanged += (_, e) => content.Width = Math.Max(0, e.NewSize.Width - 48);
        Content = scroll;
        AppWindow.Closing += (_, e) => { if (_running) { e.Cancel = true; _cancellation?.Cancel(); _status.Text = "正在取消，请等当前文件安全处理完成后关闭。"; } };
    }
    private void ClearPreview()
    {
        if (_run is null) return;
        _plan.Clear(); _preview.Children.Clear(); _run.IsEnabled = false; _status.Text = "目标已更改，请重新生成预览。";
    }
    private async Task PreviewAsync()
    {
        if (_app.Busy) { await UI.Notice(this, "请稍候", "已有整理或撤销正在执行。"); return; }
        try
        {
            _scan.IsEnabled = false; _run.IsEnabled = false; _finished = false;
            var destinations = new Dictionary<FileCategory, string>();
            foreach (var pair in _targets)
            {
                var target = ((TargetChoice)pair.Value.SelectedItem).Path;
                if (string.IsNullOrEmpty(target))
                {
                    PathPolicy.ValidateStorageRoot(_app.Settings.StorageRoot, _app.DesktopPath, _app.Store.DataDirectory);
                    target = Path.Combine(_app.Settings.StorageRoot, Classifier.DisplayName(pair.Key));
                }
                destinations[pair.Key] = target;
            }
            _plan = await Task.Run(() => _app.Organizer.Preview(_app.DesktopPath, destinations));
            _preview.Children.Clear();
            _preview.Children.Add(UI.Text("逐项确认 · 取消勾选即保留在桌面", 16));
            var all = new CheckBox { Content = "全部勾选", IsChecked = true };
            var list = new ListView { ItemsSource = _plan, SelectionMode = ListViewSelectionMode.None, MaxHeight = 360 };
            list.ItemTemplate = (DataTemplate)XamlReader.Load("""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><CheckBox IsChecked="{Binding Included,Mode=TwoWay}" HorizontalContentAlignment="Stretch"><StackPanel Spacing="2" Padding="0,4"><TextBlock Text="{Binding Source}" FontSize="13" TextWrapping="Wrap"/><TextBlock Text="{Binding Destination}" FontSize="12" Opacity="0.65" TextWrapping="Wrap"/><TextBlock Text="{Binding Note}" FontSize="12" Opacity="0.65" TextWrapping="Wrap"/></StackPanel></CheckBox></DataTemplate>
                """);
            all.Click += (_, _) => { foreach (var item in _plan) item.Included = all.IsChecked == true; list.ItemsSource = null; list.ItemsSource = _plan; };
            _preview.Children.Add(all); _preview.Children.Add(list);
            _status.Text = $"预览共 {_plan.Count} 项。目标同名时使用新名称，不覆盖已有文件。";
            _run.IsEnabled = _plan.Count > 0;
        }
        catch (Exception e) { await UI.Notice(this, "无法生成预览", e.Message); }
        finally { _scan.IsEnabled = true; }
    }
    private async Task ExecuteAsync()
    {
        if (_app.Busy || _running || _finished) return;
        var selected = _plan.Where(p => p.Included).ToList();
        if (selected.Count == 0) { await UI.Notice(this, "未选择文件", "请至少勾选一个文件。"); return; }
        if (!await UI.Confirm(this, "确认移动文件？", $"将移动预览中勾选的 {selected.Count} 个文件。\n请确认目标路径。取消后已完成的文件仍会保留在目标，可使用撤销。", "确认移动")) return;
        if (_app.Busy) return;
        _app.Busy = true; _running = true; _scan.IsEnabled = false; _run.IsEnabled = false; _cancel.IsEnabled = true;
        foreach (var combo in _targets.Values) combo.IsEnabled = false;
        _preview.IsHitTestVisible = false; _cancellation = new CancellationTokenSource();
        try
        {
            var progress = new Progress<OrganizationProgress>(p => { _progress.Maximum = Math.Max(1, p.Total); _progress.Value = p.Completed; _status.Text = $"{p.Completed}/{p.Total} · {p.Current}"; });
            var result = await _app.Organizer.ExecuteAsync(selected, progress, _cancellation.Token);
            foreach (var group in selected.Where(p => File.Exists(p.Destination)).GroupBy(p => Path.GetDirectoryName(p.Destination)!, StringComparer.OrdinalIgnoreCase))
            {
                if (!_app.Settings.Boxes.Any(b => string.Equals(b.FolderPath, group.Key, StringComparison.OrdinalIgnoreCase)))
                    _app.AddBox(Classifier.DisplayName(group.First().Category), group.Key, true);
            }
            foreach (var pair in _targets) { var selectedTarget = ((TargetChoice)pair.Value.SelectedItem).Path; if (string.IsNullOrEmpty(selectedTarget)) _app.Settings.CategoryTargets.Remove(pair.Key); else _app.Settings.CategoryTargets[pair.Key] = selectedTarget; }
            _app.Save(); _status.Text = AppController.ResultText(result); _finished = true;
            await UI.Notice(this, "整理结果", AppController.ResultText(result));
        }
        catch (Exception e) { _status.Text = "整理未完成：" + e.Message; await UI.Notice(this, "整理未完成", e.Message + "\n再次运行前将核对事务状态，保留的文件不会被覆盖。"); }
        finally
        {
            _app.Busy = false; _running = false; _scan.IsEnabled = true; _cancel.IsEnabled = false; _preview.IsHitTestVisible = true;
            foreach (var combo in _targets.Values) combo.IsEnabled = true;
            _cancellation.Dispose(); _cancellation = null; _app.Refresh();
        }
    }
}
