using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using UpdateForge.Desktop.ViewModels;

namespace UpdateForge.Desktop.Views;

public partial class DiscoverView : UserControl
{
    public DiscoverView() => InitializeComponent();

    private void Packages_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only a double-click on a row opens details: not on a header, the scrollbar, or the Install button.
        for (var d = e.OriginalSource as DependencyObject; d is not null && d != sender; d = ParentOf(d))
        {
            if (d is ButtonBase or DataGridColumnHeader or ScrollBar) return;
            if (d is DataGridRow { Item: DiscoveredPackageRow row })
            {
                e.Handled = true;
                ShowDetails(row);
                return;
            }
        }
    }

    private void ShowDetails_Click(object sender, RoutedEventArgs e)
    {
        if (PackagesGrid.SelectedItem is DiscoveredPackageRow row) ShowDetails(row);
    }

    private void Packages_KeyDown(object sender, KeyEventArgs e)
    {
        // Enter would otherwise move to the next row; open details instead, like a double-click.
        if (e.Key == Key.Enter && PackagesGrid.SelectedItem is DiscoveredPackageRow row) { e.Handled = true; ShowDetails(row); }
    }

    private void ShowDetails(DiscoveredPackageRow row)
    {
        if (DataContext is not DiscoverViewModel vm) return;
        var details = vm.CreateDetails(row);
        var window = new PackageDetailsWindow(details) { Owner = Window.GetWindow(this) };
        details.InstallRequested = () =>
        {
            window.Close();
            if (vm.InstallCommand.CanExecute(row)) vm.InstallCommand.Execute(row);
        };
        window.ShowDialog();
    }

    private static DependencyObject? ParentOf(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
}
