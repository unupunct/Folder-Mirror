using System.Windows;
using FolderMirror.Services;
using FolderMirror.ViewModels;

namespace FolderMirror.Views;

public partial class WizardWindow : Window
{
    public WizardWindow(WizardViewModel vm)
    {
        InitializeComponent();
        ThemeManager.Hook(this);
        DataContext = vm;
        Title = vm.WindowTitle + " – Folder Mirror";
        vm.CloseRequested = ok => { DialogResult = ok; };
    }
}
