using System.ComponentModel;
using Wpf.Ui.Controls;

namespace UpdateForge.Desktop;

public partial class MainWindow : FluentWindow
{
    public bool AllowClose { get; set; }
    public Func<bool>? ShouldHideOnClose { get; set; }

    /// <summary>Raised when the user closes the window and "close to tray" is off: the app should exit (after any confirmation).</summary>
    public Action? ExitRequested { get; set; }

    public MainWindow() => InitializeComponent();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (AllowClose)
        {
            base.OnClosing(e);
            return;
        }

        // The window is never really closed while the app lives. Destroying it left a windowless background process
        // that could not be reopened, because Show() on a closed window throws.
        e.Cancel = true;
        if (ShouldHideOnClose?.Invoke() == true) Hide();
        else Dispatcher.BeginInvoke(() => ExitRequested?.Invoke());
    }
}
