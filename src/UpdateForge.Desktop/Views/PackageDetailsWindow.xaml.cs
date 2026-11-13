using System.Windows;
using UpdateForge.Desktop.ViewModels;

namespace UpdateForge.Desktop.Views;

public partial class PackageDetailsWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly CancellationTokenSource _closing = new();

    public PackageDetailsWindow(PackageDetailsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        // Loading stops when the window closes, so a slow manager never keeps running for a dismissed dialog.
        Loaded += async (_, _) => await viewModel.LoadAsync(_closing.Token);
        Closed += (_, _) => { _closing.Cancel(); _closing.Dispose(); };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
