using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Shige.App.Platform;
using Shige.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.System;
using Path = System.IO.Path;
using FileAttributes = System.IO.FileAttributes;
using DragDropModifiers = Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers;

namespace Shige.App;

internal sealed class BoxWindow : Window
{
    private readonly AppController _app;
    private readonly BoxSettings _box;
    private readonly GridView _files;
    private readonly TextBlock _status = UI.Text("", 12, .65);
    private readonly TextBlock _heading;
    private readonly ObservableCollection<FileEntry> _entries = [];
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _refreshTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _layoutTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _watchRetryTimer;
    private FileSystemWatcher? _watcher;
    private FileSystemWatcher? _parentWatcher;
    private bool _removed, _refreshing, _pendingRefresh, _layoutApplying;
    private readonly Grid _surface = new();
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Rectangle _selection = new() { Fill = new SolidColorBrush(Colors.DodgerBlue) { Opacity = .12 }, Stroke = new SolidColorBrush(Colors.DodgerBlue), StrokeThickness = 1, Visibility = Visibility.Collapsed };
    private Point? _anchor;
    private List<object> _baseSelection = [];

    public BoxWindow(AppController app, BoxSettings settings)
    {
        _app = app; _box = settings; Title = _box.Title + " · 拾格"; UI.Backdrop(this);
        NativeWindowService.Place(this, _box.X, _box.Y, _box.Width, _box.Height, _box.Locked);
        var root = new Grid { Padding = new Thickness(12), RowSpacing = 8 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Grid(); header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _heading = UI.Text(_box.Title, 18); _heading.VerticalAlignment = VerticalAlignment.Center; header.Children.Add(_heading);
        var more = new Button { Content = "···" };
        Grid.SetColumn(more, 1); more.Flyout = BuildBoxMenu(); header.Children.Add(more); root.Children.Add(header);
        _files = new GridView
        {
            ItemsSource = _entries,
            SelectionMode = ListViewSelectionMode.Extended,
            IsItemClickEnabled = false,
            CanDragItems = true,
            CanReorderItems = _box.Sort == SortMode.Manual,
            AllowDrop = true,
            IsMultiSelectCheckBoxEnabled = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _surface.Children.Add(_files); _overlay.Children.Add(_selection); _surface.Children.Add(_overlay); Grid.SetRow(_surface, 1); root.Children.Add(_surface);
        Grid.SetRow(_status, 2); root.Children.Add(_status); Content = root;
        _refreshTimer = DispatcherQueue.CreateTimer(); _refreshTimer.Interval = TimeSpan.FromMilliseconds(350); _refreshTimer.IsRepeating = false;
        _refreshTimer.Tick += (_, _) => _ = RefreshAsync();
        _layoutTimer = DispatcherQueue.CreateTimer(); _layoutTimer.Interval = TimeSpan.FromMilliseconds(400); _layoutTimer.IsRepeating = false;
        _layoutTimer.Tick += (_, _) => SaveLayout();
        _watchRetryTimer = DispatcherQueue.CreateTimer(); _watchRetryTimer.Interval = TimeSpan.FromSeconds(2); _watchRetryTimer.IsRepeating = true;
        _watchRetryTimer.Tick += (_, _) => { Watch(); _ = RefreshAsync(); };
        AppWindow.Changed += (_, e) => { if (!_layoutApplying && (e.DidPositionChange || e.DidSizeChange)) { _layoutTimer.Stop(); _layoutTimer.Start(); } };
        AppWindow.Closing += (_, e) => { if (!_removed) { e.Cancel = true; NativeWindowService.Hide(this); } };
        Closed += (_, _) => { _watcher?.Dispose(); _parentWatcher?.Dispose(); _refreshTimer.Stop(); _layoutTimer.Stop(); _watchRetryTimer.Stop(); };
        _files.DoubleTapped += (_, _) => { if (_files.SelectedItem is FileEntry item) Guard(() => ShellFiles.Open(item.Path)); };
        _files.RightTapped += OnRightTapped;
        root.KeyDown += OnKey;
        _files.DragItemsStarting += OnDragStarting;
        _files.DragItemsCompleted += (_, _) => { if (_box.Sort == SortMode.Manual) { _box.ManualOrder = _entries.Select(e => e.Name).ToList(); _app.Save(); } _ = RefreshAsync(); };
        _files.DragOver += OnDragOver;
        _files.Drop += OnDrop;
        _files.ContainerContentChanging += (sender, e) => { if (!e.InRecycleQueue && e.Item is FileEntry entry && entry.Thumbnail is null) _ = LoadIconAsync(entry); };
        _files.SelectionChanged += (_, _) => UpdateStatus();
        _surface.PointerPressed += OnBlankPressed; _surface.PointerMoved += OnBoxSelect;
        _surface.PointerReleased += (_, e) => { if (_anchor.HasValue) { _anchor = null; _selection.Visibility = Visibility.Collapsed; _surface.ReleasePointerCapture(e.Pointer); e.Handled = true; } };
        _surface.PointerCaptureLost += (_, _) => { _anchor = null; _selection.Visibility = Visibility.Collapsed; };
        ApplySettings(); Watch(); _ = RefreshAsync();
    }
    public void ApplySettings()
    {
        _heading.Text = _box.Title + (_box.Locked ? "  · 已锁定" : ""); Title = _box.Title + " · 拾格";
        NativeWindowService.SetLocked(this, _box.Locked);
        int icon = _app.Settings.IconSize, width = icon + 42, height = icon + 58;
        _files.ItemTemplate = (DataTemplate)XamlReader.Load($$"""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><StackPanel Width="{{width}}" Height="{{height}}" Padding="4" Spacing="5" ToolTipService.ToolTip="{Binding Path}">
              <Grid Height="{{icon}}" Width="{{icon}}"><TextBlock Text="{Binding Glyph}" FontFamily="Segoe Fluent Icons" FontSize="{{icon * .60}}" HorizontalAlignment="Center" VerticalAlignment="Center" Opacity="0.6"/><Image Source="{Binding Thumbnail}" Stretch="Uniform"/></Grid>
              <TextBlock Text="{Binding Name}" FontSize="12" TextAlignment="Center" TextWrapping="Wrap" MaxLines="2" TextTrimming="CharacterEllipsis"/>
            </StackPanel></DataTemplate>
            """);
        _files.CanReorderItems = _box.Sort == SortMode.Manual;
    }
    private MenuFlyout BuildBoxMenu()
    {
        var menu = new MenuFlyout();
        Add(menu, "添加文件…", async () =>
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker(); picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, NativeWindowService.Handle(this));
            var files = await picker.PickMultipleFilesAsync(); if (files.Count > 0) await Transfer(files.Select(f => f.Path), false);
        });
        Add(menu, "打开文件夹", () => { ShellFiles.Open(_box.FolderPath); return Task.CompletedTask; });
        Add(menu, "新建文件夹", NewFolder);
        Add(menu, "粘贴", Paste); Add(menu, "刷新", RefreshAsync);
        var sort = new MenuFlyoutSubItem { Text = "排序" };
        foreach (var (mode, label) in new[] { (SortMode.Manual, "手动排序"), (SortMode.Name, "名称"), (SortMode.Type, "类型"), (SortMode.Modified, "修改时间") })
        { var item = new MenuFlyoutItem { Text = label }; item.Click += (_, _) => { _box.Sort = mode; _app.Save(); ApplySettings(); _ = RefreshAsync(); }; sort.Items.Add(item); }
        menu.Items.Add(sort); menu.Items.Add(new MenuFlyoutSeparator());
        Add(menu, "重命名格子", async () => { var name = await UI.Input(this, "重命名格子", "仅更改显示名称，真实目录保持原名", _box.Title); if (!string.IsNullOrWhiteSpace(name)) { _box.Title = name; ApplySettings(); _app.Save(); _app.Refresh(); } });
        Add(menu, "锁定 / 解锁位置", () => { _box.Locked = !_box.Locked; ApplySettings(); _app.Save(); return Task.CompletedTask; });
        Add(menu, "收纳中心", () => { _app.Show(); return Task.CompletedTask; });
        Add(menu, "移除格子", () => _app.RemoveAsync(this, _box)); return menu;
    }
    private void Add(MenuFlyout menu, string name, Func<Task> action)
    { var item = new MenuFlyoutItem { Text = name }; item.Click += async (_, _) => { try { await action(); } catch (Exception e) { await UI.Notice(this, "操作未完成", e.Message); } }; menu.Items.Add(item); }
    private string[] Selected() => _files.SelectedItems.Cast<FileEntry>().Select(e => e.Path).ToArray();
    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null && source is not GridViewItem) source = VisualTreeHelper.GetParent(source);
        if (source is GridViewItem container && container.Content is FileEntry entry && !_files.SelectedItems.Contains(entry))
        { _files.SelectedItems.Clear(); _files.SelectedItems.Add(entry); }
        var selected = Selected(); var menu = new MenuFlyout();
        if (selected.Length > 0 && source is GridViewItem)
        {
            Add(menu, "打开", () => { foreach (var path in selected) ShellFiles.Open(path); return Task.CompletedTask; });
            Add(menu, "复制", () => { ShellFiles.SetClipboard(selected, false); return Task.CompletedTask; });
            Add(menu, "剪切", () => { ShellFiles.SetClipboard(selected, true); return Task.CompletedTask; });
            if (selected.Length == 1) Add(menu, "重命名", Rename);
            Add(menu, "删除到回收站", Delete);
            Add(menu, "复制路径", () => { var package = new DataPackage(); package.SetText(string.Join(Environment.NewLine, selected)); Clipboard.SetContent(package); return Task.CompletedTask; });
            Add(menu, "在资源管理器中显示", () => { ShellFiles.Reveal(selected[0]); return Task.CompletedTask; });
            Add(menu, "属性", () => { ShellFiles.Properties(selected[0]); return Task.CompletedTask; });
        }
        else
        { Add(menu, "粘贴", Paste); Add(menu, "新建文件夹", NewFolder); Add(menu, "刷新", RefreshAsync); Add(menu, "打开文件夹", () => { ShellFiles.Open(_box.FolderPath); return Task.CompletedTask; }); }
        menu.ShowAt(_files, e.GetPosition(_files)); e.Handled = true;
    }
    private async void OnKey(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = IsDown(VirtualKey.Control);
        try
        {
            if (ctrl && e.Key is VirtualKey.C or VirtualKey.X) { ShellFiles.SetClipboard(Selected(), e.Key == VirtualKey.X); e.Handled = true; }
            else if (ctrl && e.Key == VirtualKey.V) { e.Handled = true; await Paste(); }
            else if (ctrl && e.Key == VirtualKey.A) { _files.SelectAll(); e.Handled = true; }
            else if (e.Key == VirtualKey.Delete) { e.Handled = true; await Delete(); }
            else if (e.Key == VirtualKey.F2) { e.Handled = true; await Rename(); }
            else if (e.Key == VirtualKey.F5) { e.Handled = true; await RefreshAsync(); }
            else if (e.Key == VirtualKey.Enter && _files.SelectedItem is FileEntry entry) { ShellFiles.Open(entry.Path); e.Handled = true; }
        }
        catch (Exception ex) { await UI.Notice(this, "文件操作未完成", ex.Message); }
    }
    private static bool IsDown(VirtualKey key) => (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
    private async Task Rename()
    {
        var paths = Selected(); if (paths.Length != 1) return;
        var name = await UI.Input(this, "重命名文件", "完整文件名（包含扩展名）", Path.GetFileName(paths[0]));
        if (string.IsNullOrWhiteSpace(name)) return;
        if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' ')) throw new IOException("文件名无效。");
        var destination = Path.Combine(_box.FolderPath, name);
        if (string.Equals(destination, paths[0], StringComparison.Ordinal)) return;
        if (Directory.Exists(paths[0])) Directory.Move(paths[0], destination); else File.Move(paths[0], destination, false);
        if (_box.Sort == SortMode.Manual) { var index = _box.ManualOrder.IndexOf(Path.GetFileName(paths[0])); if (index >= 0) _box.ManualOrder[index] = name; _app.Save(); }
        await RefreshAsync();
    }
    private async Task Delete()
    {
        var paths = Selected(); if (paths.Length == 0) return;
        if (!await UI.Confirm(this, "移到回收站？", $"选中了 {paths.Length} 项。不支持回收站的位置会拒绝删除。", "移到回收站")) return;
        _app.FileOperations++;
        try { var result = await ShellFiles.RecycleAsync(paths); if (result.Errors.Count > 0) await UI.Notice(this, "删除结果", string.Join("\n", result.Errors)); await RefreshAsync(); }
        finally { _app.FileOperations--; }
    }
    private async Task NewFolder()
    {
        var name = await UI.Input(this, "新建文件夹", "文件夹名称", "新建文件夹");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' ')) throw new IOException("文件夹名称无效。");
        Directory.CreateDirectory(PathPolicy.UniqueDestination(_box.FolderPath, name)); await RefreshAsync();
    }
    private async Task Paste()
    { _app.FileOperations++; try { var result = await ShellFiles.PasteAsync(_box.FolderPath); if (result.Errors.Count > 0) await UI.Notice(this, "粘贴结果", string.Join("\n", result.Errors)); await RefreshAsync(); } finally { _app.FileOperations--; } }
    private async Task Transfer(IEnumerable<string> paths, bool move)
    { _app.FileOperations++; try { var result = await ShellFiles.TransferAsync(paths, _box.FolderPath, move); if (result.Errors.Count > 0) await UI.Notice(this, "文件传输结果", string.Join("\n", result.Errors)); await RefreshAsync(); } finally { _app.FileOperations--; } }
    private void OnDragStarting(object sender, DragItemsStartingEventArgs e)
    {
        var entries = e.Items.Cast<FileEntry>().ToArray();
        e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
        e.Data.Properties["Shige.BoxId"] = _box.Id;
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try
            {
                var items = new List<IStorageItem>();
                foreach (var entry in entries) items.Add(entry.IsDirectory ? await StorageFolder.GetFolderFromPathAsync(entry.Path) : await StorageFile.GetFileFromPathAsync(entry.Path));
                request.SetData(items);
            }
            catch (Exception ex) { _app.Store.Log(ex.ToString()); }
            finally { deferral.Complete(); }
        });
    }
    private async void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        if (e.DataView.Properties.TryGetValue("Shige.BoxId", out var source) && source as string == _box.Id)
        { e.AcceptedOperation = _box.Sort == SortMode.Manual ? DataPackageOperation.Move : DataPackageOperation.None; return; }
        bool ctrl = (e.Modifiers & DragDropModifiers.Control) != 0, shift = (e.Modifiers & DragDropModifiers.Shift) != 0;
        var deferral = e.GetDeferral();
        try
        {
            var requested = shift && !ctrl ? DataPackageOperation.Move : DataPackageOperation.Copy;
            if (!ctrl && !shift)
            {
                var items = await e.DataView.GetStorageItemsAsync();
                if (items.Count > 0 && items.All(item => string.Equals(Path.GetPathRoot(item.Path), Path.GetPathRoot(_box.FolderPath), StringComparison.OrdinalIgnoreCase))) requested = DataPackageOperation.Move;
            }
            e.AcceptedOperation = (e.AllowedOperations & requested) != 0 ? requested : DataPackageOperation.None;
            e.DragUIOverride.Caption = e.AcceptedOperation == DataPackageOperation.None ? "此来源不支持该操作" : requested == DataPackageOperation.Move ? "移动到 " + _box.Title : "复制到 " + _box.Title;
            e.DragUIOverride.IsCaptionVisible = true; e.Handled = true;
        }
        catch { e.AcceptedOperation = DataPackageOperation.None; }
        finally { deferral.Complete(); }
    }
    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        if (e.DataView.Properties.TryGetValue("Shige.BoxId", out var source) && source as string == _box.Id) return;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            if (e.AcceptedOperation == DataPackageOperation.None) return;
            await Transfer(items.Select(i => i.Path), e.AcceptedOperation == DataPackageOperation.Move);
        }
        catch (Exception ex) { await UI.Notice(this, "拖放未完成", ex.Message); }
        finally { deferral.Complete(); }
        e.Handled = true;
    }
    private void Watch()
    {
        if (_removed) return;
        _watcher?.Dispose(); _watcher = null;
        try
        {
            // The directory's own watcher follows the old handle after a rename.
            // Observe its name in the parent as well, so replacing the directory rebinds it.
            var watchedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_box.FolderPath));
            var parent = Path.GetDirectoryName(watchedPath);
            if (_parentWatcher is null && parent is not null && Directory.Exists(parent))
            {
                _parentWatcher = new FileSystemWatcher(parent) { NotifyFilter = NotifyFilters.DirectoryName };
                FileSystemEventHandler folderChanged = (_, e) =>
                {
                    if (string.Equals(e.FullPath, watchedPath, StringComparison.OrdinalIgnoreCase))
                        DispatcherQueue.TryEnqueue(() => { if (!_removed) { Watch(); _ = RefreshAsync(); } });
                };
                _parentWatcher.Created += folderChanged; _parentWatcher.Deleted += folderChanged;
                _parentWatcher.Renamed += (sender, e) =>
                {
                    folderChanged(sender, e);
                    if (string.Equals(e.OldFullPath, watchedPath, StringComparison.OrdinalIgnoreCase))
                        DispatcherQueue.TryEnqueue(() => { if (!_removed) { Watch(); _ = RefreshAsync(); } });
                };
                _parentWatcher.Error += (_, _) => DispatcherQueue.TryEnqueue(() =>
                { if (!_removed) { _parentWatcher?.Dispose(); _parentWatcher = null; Watch(); _ = RefreshAsync(); } });
                _parentWatcher.EnableRaisingEvents = true;
            }
            if (!Directory.Exists(_box.FolderPath)) { _watchRetryTimer.Start(); return; }
            _watcher = new FileSystemWatcher(_box.FolderPath) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size, EnableRaisingEvents = true };
            FileSystemEventHandler changed = (_, _) => DispatcherQueue.TryEnqueue(() => { if (!_removed) { _refreshTimer.Stop(); _refreshTimer.Start(); } });
            _watcher.Changed += changed; _watcher.Created += changed; _watcher.Deleted += changed; _watcher.Renamed += (sender, e) => changed(sender, e);
            _watcher.Error += (_, _) => DispatcherQueue.TryEnqueue(() => { if (!_removed) { Watch(); _ = RefreshAsync(); } });
            _watchRetryTimer.Stop();
        }
        catch (Exception e) { _watcher?.Dispose(); _watcher = null; _watchRetryTimer.Start(); _app.Store.Log(e.ToString()); }
    }
    private async Task RefreshAsync()
    {
        if (_removed) return;
        if (_refreshing) { _pendingRefresh = true; return; }
        _refreshing = true;
        try
        {
            var selected = Selected().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var entries = await Task.Run(() => Directory.EnumerateFileSystemEntries(_box.FolderPath).Select(path =>
            { var attr = File.GetAttributes(path); return new FileEntry(path, attr.HasFlag(FileAttributes.Directory), File.GetLastWriteTimeUtc(path)); }).Where(e => !Path.GetFileName(e.Path).StartsWith(".shige-", StringComparison.OrdinalIgnoreCase)).ToList());
            entries = _box.Sort switch
            {
                SortMode.Name => entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
                SortMode.Type => entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => Path.GetExtension(e.Name)).ThenBy(e => e.Name).ToList(),
                SortMode.Modified => entries.OrderByDescending(e => e.Modified).ToList(),
                _ => entries.OrderBy(e => { int i = _box.ManualOrder.IndexOf(e.Name); return i < 0 ? int.MaxValue : i; }).ThenBy(e => e.Name).ToList()
            };
            _entries.Clear(); foreach (var item in entries) _entries.Add(item);
            foreach (var item in _entries.Where(e => selected.Contains(e.Path))) _files.SelectedItems.Add(item);
            UpdateStatus(); if (_watcher is null) Watch();
        }
        catch (Exception e) { _entries.Clear(); _status.Text = "位置不可用：" + e.Message; }
        finally { _refreshing = false; if (_pendingRefresh) { _pendingRefresh = false; _refreshTimer.Start(); } }
    }
    private async Task LoadIconAsync(FileEntry entry)
    {
        if (entry.Loading) return; entry.Loading = true;
        try
        {
            using var thumbnail = entry.IsDirectory ? await (await StorageFolder.GetFolderFromPathAsync(entry.Path)).GetThumbnailAsync(ThumbnailMode.SingleItem, 96, ThumbnailOptions.UseCurrentScale)
                : await (await StorageFile.GetFileFromPathAsync(entry.Path)).GetThumbnailAsync(ThumbnailMode.SingleItem, 96, ThumbnailOptions.UseCurrentScale);
            if (thumbnail is null) return;
            var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(thumbnail); entry.Thumbnail = bitmap;
        }
        catch { /* The built-in glyph remains usable for inaccessible files. */ }
    }
    private void UpdateStatus() => _status.Text = _files.SelectedItems.Count > 0 ? $"{_entries.Count} 项 · 已选 {_files.SelectedItems.Count} 项" : $"{_entries.Count} 项 · {_box.FolderPath}";
    private void SaveLayout()
    {
        if (_removed) return; _layoutApplying = true;
        try
        {
            if (_app.Settings.Snap && !_box.Locked) NativeWindowService.Snap(this);
            var b = NativeWindowService.Bounds(this); _box.X = b.X; _box.Y = b.Y; _box.Width = b.Width; _box.Height = b.Height; _app.Save();
        }
        finally { _layoutApplying = false; }
    }
    public void CloseForRemoval() { SaveLayout(); _removed = true; Close(); }
    private async void Guard(Action action) { try { action(); } catch (Exception e) { await UI.Notice(this, "操作未完成", e.Message); } }
    private void OnBlankPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(_surface).Properties.IsLeftButtonPressed) return;
        DependencyObject? node = e.OriginalSource as DependencyObject;
        while (node is not null && node != _surface) { if (node is GridViewItem or ScrollBar) return; node = VisualTreeHelper.GetParent(node); }
        _anchor = e.GetCurrentPoint(_surface).Position; _baseSelection = IsDown(VirtualKey.Control) ? _files.SelectedItems.ToList() : [];
        _surface.CapturePointer(e.Pointer); e.Handled = true;
    }
    private void OnBoxSelect(object sender, PointerRoutedEventArgs e)
    {
        if (_anchor is not { } anchor) return;
        var point = e.GetCurrentPoint(_surface).Position; var rect = new Rect(Math.Min(point.X, anchor.X), Math.Min(point.Y, anchor.Y), Math.Abs(point.X - anchor.X), Math.Abs(point.Y - anchor.Y));
        Canvas.SetLeft(_selection, rect.Left); Canvas.SetTop(_selection, rect.Top); _selection.Width = rect.Width; _selection.Height = rect.Height; _selection.Visibility = Visibility.Visible;
        _files.SelectedItems.Clear(); foreach (var selected in _baseSelection) _files.SelectedItems.Add(selected);
        foreach (var item in _entries)
        {
            if (_files.ContainerFromItem(item) is not FrameworkElement container) continue;
            var itemRect = container.TransformToVisual(_surface).TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            if (rect.Left < itemRect.Right && rect.Right > itemRect.Left && rect.Top < itemRect.Bottom && rect.Bottom > itemRect.Top && !_files.SelectedItems.Contains(item)) _files.SelectedItems.Add(item);
        }
        e.Handled = true;
    }
}

internal sealed class FileEntry(string path, bool directory, DateTime modified) : INotifyPropertyChanged
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public bool IsDirectory { get; } = directory;
    public DateTime Modified { get; } = modified;
    public string Glyph => IsDirectory ? "\uE8B7" : "\uE8A5";
    public bool Loading { get; set; }
    private BitmapImage? _thumbnail;
    public BitmapImage? Thumbnail { get => _thumbnail; set { _thumbnail = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public override string ToString() => Name;
}
