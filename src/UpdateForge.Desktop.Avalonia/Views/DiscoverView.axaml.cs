using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using UpdateForge.Desktop.ViewModels;

namespace UpdateForge.Desktop.Views;

public partial class DiscoverView : UserControl
{
    public DiscoverView()
    {
        InitializeComponent();
        QueryBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && DataContext is DiscoverViewModel vm && vm.SearchCommand.CanExecute(null)) vm.SearchCommand.Execute(null);
        };
        // The Package managers shortcut lives in the shell, so it goes through the window's data context.
        ManagersButton.Click += (_, _) =>
        {
            if (this.GetVisualRoot() is Window { DataContext: ShellViewModel shell }) shell.NavigateCommand.Execute(PageId.PackageManagers);
        };
        PackagesGrid.DoubleTapped += OnDoubleTapped;
        PackagesGrid.KeyDown += (_, e) =>
        {
            // Enter would otherwise move to the next row; open details instead, like a double-click.
            if (e.Key == Key.Enter && PackagesGrid.SelectedItem is DiscoveredPackageRow row) { e.Handled = true; ShowDetails(row); }
        };
        ShowDetailsItem.Click += (_, _) => { if (PackagesGrid.SelectedItem is DiscoveredPackageRow row) ShowDetails(row); };
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Only a double-click on a row opens details: not on a header, the scrollbar, or the Install button.
        for (var v = e.Source as Avalonia.Visual; v is not null && v != PackagesGrid; v = v.GetVisualParent())
        {
            if (v is Button or DataGridColumnHeader or Avalonia.Controls.Primitives.ScrollBar) return;
            if (v is DataGridRow { DataContext: DiscoveredPackageRow row })
            {
                e.Handled = true;
                ShowDetails(row);
                return;
            }
        }
    }

    private async void ShowDetails(DiscoveredPackageRow row)
    {
        if (DataContext is not DiscoverViewModel vm || this.GetVisualRoot() is not Window owner) return;
        var details = vm.CreateDetails(row);
        var window = new PackageDetailsWindow(details);
        details.InstallRequested = () =>
        {
            window.Close();
            if (vm.InstallCommand.CanExecute(row)) vm.InstallCommand.Execute(row);
        };
        await window.ShowDialog(owner);
    }
}
