using Avalonia.Controls;
using Avalonia.Interactivity;
using FolderMirror.Mac.ViewModels;

namespace FolderMirror.Mac.Views;

public partial class ProfileEditorWindow : Window
{
    public ProfileEditorWindow() => InitializeComponent();

    void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ProfileEditorViewModel vm && vm.TrySave()) Close(true);
    }
}
