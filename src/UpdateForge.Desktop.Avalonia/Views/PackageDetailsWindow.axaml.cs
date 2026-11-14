using Avalonia.Controls;
using UpdateForge.Desktop.ViewModels;

namespace UpdateForge.Desktop.Views;

public partial class PackageDetailsWindow : Window
{
    private readonly CancellationTokenSource _cts = new();

    public PackageDetailsWindow() => InitializeComponent();   // required by the XAML loader

    public PackageDetailsWindow(PackageDetailsViewModel vm) : this()
    {
        DataContext = vm;
        Title = $"{vm.Title} – package details";
        CloseButton.Click += (_, _) => Close();
        Opened += async (_, _) => await vm.LoadAsync(_cts.Token);
        Closed += (_, _) => { _cts.Cancel(); _cts.Dispose(); };
    }
}
