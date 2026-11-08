using System.ComponentModel;
using Wpf.Ui.Controls;

namespace UpdateForge.Desktop;

public partial class MainWindow : FluentWindow
{
    public bool AllowClose { get; set; }
    public Func<bool>? ShouldHideOnClose { get; set; }

    public MainWindow() => InitializeComponent();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose && ShouldHideOnClose?.Invoke() == true)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }
}
