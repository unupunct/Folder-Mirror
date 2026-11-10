using System.Windows;
using System.Windows.Media;
using FolderMirror.Services;

namespace FolderMirror.Views;

public partial class DialogWindow : Window
{
    public DialogWindow(string title, string message, string okText, string? cancelText, bool danger, string? checkText, string glyph)
    {
        InitializeComponent();
        ThemeManager.Hook(this);
        Title = AppInfo.Name;
        TitleText.Text = title; MessageText.Text = message; OkButton.Content = okText;
        IconText.Text = glyph;
        IconText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, danger ? "DeleteBrush" : "AccentBrush");
        if (cancelText == null) CancelButton.Visibility = Visibility.Collapsed; else CancelButton.Content = cancelText;
        if (danger) { OkButton.Style = (Style)FindResource("DangerButton"); OkButton.FontWeight = FontWeights.SemiBold; }
        if (checkText != null)
        {
            CheckPanel.Visibility = Visibility.Visible; CheckText.Text = checkText;
            OkButton.IsEnabled = false;
            Check.Checked += (_, _) => OkButton.IsEnabled = true;
            Check.Unchecked += (_, _) => OkButton.IsEnabled = false;
        }
    }

    void Ok_Click(object sender, RoutedEventArgs e) { DialogResult = true; }
    void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; }
}

public static class Dialogs
{
    public static Window? Owner => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
        ?? Application.Current?.MainWindow;

    public static bool Confirm(string title, string message, string okText = "OK", bool danger = false, string? mustCheck = null, string cancelText = "Cancel")
    {
        var w = new DialogWindow(title, message, okText, cancelText, danger, mustCheck, danger ? "" : "");
        var o = Owner; if (o != null && o.IsVisible) w.Owner = o; else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return w.ShowDialog() == true;
    }

    public static void Info(string title, string message, bool error = false)
    {
        var w = new DialogWindow(title, message, "OK", null, error, null, error ? "" : "");
        var o = Owner; if (o != null && o.IsVisible) w.Owner = o; else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.ShowDialog();
    }
}
