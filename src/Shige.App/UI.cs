using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Markup;
using Shige.App.Platform;

namespace Shige.App;

internal static class UI
{
    public static TextBlock Text(string value, double size = 14, double opacity = 1) => new()
    { Text = value, FontSize = size, Opacity = opacity, TextWrapping = TextWrapping.Wrap };
    public static StackPanel Stack(double spacing = 12) => new() { Spacing = spacing };
    public static StackPanel Row(double spacing = 8) => new() { Orientation = Orientation.Horizontal, Spacing = spacing };
    public static Button Button(string text, Action action, bool accent = false)
    {
        var button = new Button { Content = text };
        if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.Click += (_, _) => action();
        return button;
    }
    public static void Backdrop(Window window)
    {
        try { window.SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop(); } catch { }
        try { window.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Shige.ico")); } catch { }
    }
    public static Border Card(UIElement child)
    {
        // Theme resources must be resolved in the visual tree, not read as local app resources.
        var border = (Border)XamlReader.Load("<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Padding=\"16\" CornerRadius=\"8\" BorderThickness=\"1\" Background=\"{ThemeResource CardBackgroundFillColorDefaultBrush}\" BorderBrush=\"{ThemeResource CardStrokeColorDefaultBrush}\"/>");
        border.Child = child;
        return border;
    }
    public static async Task Notice(Window owner, string title, string message)
    {
        if (owner.Content is not FrameworkElement element || element.XamlRoot is null) return;
        try { await new ContentDialog { XamlRoot = element.XamlRoot, Title = title, Content = new ScrollViewer { Content = Text(message), MaxHeight = 400 }, CloseButtonText = "知道了" }.ShowAsync(); }
        catch (Exception ex) { AppController.Current?.Store.Log(ex.ToString()); }
    }
    public static async Task<bool> Confirm(Window owner, string title, string message, string accept = "确认")
    {
        var dialog = new ContentDialog
        {
            XamlRoot = ((FrameworkElement)owner.Content).XamlRoot,
            Title = title,
            Content = Text(message),
            PrimaryButtonText = accept,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
    public static async Task<string?> Input(Window owner, string title, string label, string value = "")
    {
        var field = new TextBox { Text = value, Header = label, MinWidth = 300, MaxLength = 180 };
        var dialog = new ContentDialog
        {
            XamlRoot = ((FrameworkElement)owner.Content).XamlRoot,
            Title = title,
            Content = field,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? field.Text.Trim() : null;
    }
    public static async Task<string?> SaveFile(Window owner, string suggested, string extension)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = suggested };
        picker.FileTypeChoices.Add("JSON 配置", new List<string> { extension });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, NativeWindowService.Handle(owner));
        return (await picker.PickSaveFileAsync())?.Path;
    }
    public static async Task<string?> OpenFile(Window owner)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker(); picker.FileTypeFilter.Add(".json");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, NativeWindowService.Handle(owner));
        return (await picker.PickSingleFileAsync())?.Path;
    }
}
