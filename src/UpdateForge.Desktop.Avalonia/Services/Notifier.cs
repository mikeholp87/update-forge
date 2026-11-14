using System.Diagnostics;

namespace UpdateForge.Desktop.Services;

/// <summary>Desktop notifications through <c>notify-send</c> (libnotify), which every major desktop's notification daemon understands. Missing tool = silently no toast.</summary>
public static class Notifier
{
    private static readonly string[] Tools = ["/usr/bin/notify-send", "/usr/local/bin/notify-send", "/bin/notify-send"];
    private static readonly string[] Options = ["--app-name=UpdateForge", "--icon=system-software-update", "--"];

    public static void Send(string title, string text)
    {
        var tool = Tools.FirstOrDefault(File.Exists);
        if (tool is null) return;
        try
        {
            var psi = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in Options) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add(title);
            psi.ArgumentList.Add(text);
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { /* no notification daemon: nothing to do */ }
    }
}
