using System.Windows;
using FolderMirror.Services;
using FolderMirror.ViewModels;

namespace FolderMirror.Views;

public partial class ScheduleWindow : Window
{
    public ScheduleWindow(ScheduleViewModel vm)
    {
        InitializeComponent();
        ThemeManager.Hook(this);
        DataContext = vm;
        Title = "Schedule – Folder Mirror";
        vm.CloseRequested = ok => { DialogResult = ok; };
    }
}
