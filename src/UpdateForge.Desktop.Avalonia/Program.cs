using Avalonia;
using UpdateForge.Desktop.Services;

namespace UpdateForge.Desktop;

internal static class Program
{
    internal static SingleInstance Single { get; } = new();

    [STAThread]
    public static int Main(string[] args)
    {
        if (!Single.TryBecomeFirst())
        {
            SingleInstance.SignalFirst();   // ask the running instance to come to the foreground
            return 0;
        }
        try { return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown); }
        finally { Single.Dispose(); }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
