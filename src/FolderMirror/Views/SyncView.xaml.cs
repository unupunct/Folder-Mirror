using System.Windows.Controls;
using FolderMirror.ViewModels;

namespace FolderMirror.Views;

public partial class SyncView : UserControl
{
    public SyncView() { InitializeComponent(); }

    void Resolution_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.RemovedItems.Count > 0 && DataContext is SyncViewModel vm)
            vm.ResolutionChangedCommand.Execute(null);
    }
}
