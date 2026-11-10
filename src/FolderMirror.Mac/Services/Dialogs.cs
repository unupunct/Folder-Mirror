using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace FolderMirror.Mac.Services;

/// <summary>Simple modal dialogs and file/folder pickers.</summary>
public static class Dialogs
{
    public static Window? Owner =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public static Task Info(string title, string message, bool error = false) => Show(title, message, "OK", null, error);

    public static Task<bool> Confirm(string title, string message, string ok = "OK", bool danger = false) =>
        Show(title, message, ok, "Cancel", danger);

    static async Task<bool> Show(string title, string message, string ok, string? cancel, bool danger)
    {
        var win = new Window
        {
            Title = title, Width = 480, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#171C21"))
        };
        var okBtn = new Button { Content = ok, MinWidth = 96, HorizontalContentAlignment = HorizontalAlignment.Center };
        okBtn.Classes.Add(danger ? "danger" : "accent");
        okBtn.Click += (_, _) => win.Close(true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 18, 0, 0) };
        if (cancel != null)
        {
            var c = new Button { Content = cancel, MinWidth = 96, HorizontalContentAlignment = HorizontalAlignment.Center };
            c.Click += (_, _) => win.Close(false);
            buttons.Children.Add(c);
        }
        buttons.Children.Add(okBtn);
        win.Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 6,
            Children =
            {
                new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = danger ? Brushes.IndianRed : null },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Opacity = 0.85, Margin = new Thickness(0, 6, 0, 0) },
                buttons
            }
        };
        var owner = Owner;
        if (owner == null) { win.Show(); return false; }
        return await win.ShowDialog<bool>(owner);
    }

    public static async Task<string?> PickFolder(string title)
    {
        var top = Owner == null ? null : TopLevel.GetTopLevel(Owner);
        if (top == null) return null;
        var r = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return r.Count == 0 ? null : r[0].TryGetLocalPath();
    }

    public static async Task<string?> PickOpenFile(string title, string patternName, string pattern)
    {
        var top = Owner == null ? null : TopLevel.GetTopLevel(Owner);
        if (top == null) return null;
        var r = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title, AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType(patternName) { Patterns = new[] { pattern } } }
        });
        return r.Count == 0 ? null : r[0].TryGetLocalPath();
    }

    public static async Task<string?> PickSaveFile(string title, string suggested, string patternName, string pattern)
    {
        var top = Owner == null ? null : TopLevel.GetTopLevel(Owner);
        if (top == null) return null;
        var r = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title, SuggestedFileName = suggested, DefaultExtension = pattern.TrimStart('*'),
            FileTypeChoices = new[] { new FilePickerFileType(patternName) { Patterns = new[] { pattern } } }
        });
        return r?.TryGetLocalPath();
    }
}
