using Avalonia.Controls;

namespace UpdateForge.Desktop;

public partial class MainWindow : Window
{
    public bool AllowClose { get; set; }
    public Func<bool>? ShouldHideOnClose { get; set; }

    /// <summary>Raised when the user closes the window and "close to tray" is off: the app should exit (after any confirmation).</summary>
    public Action? ExitRequested { get; set; }

    public MainWindow() => InitializeComponent();

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (AllowClose) { base.OnClosing(e); return; }

        // The window is never really closed while the app lives: a closed Avalonia window cannot be shown again.
        e.Cancel = true;
        if (ShouldHideOnClose?.Invoke() == true) Hide();
        else ExitRequested?.Invoke();
    }
}
