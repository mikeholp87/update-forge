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
            psi.ArgumentList.Add(Escape(title));
            psi.ArgumentList.Add(Escape(text));
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { /* no notification daemon: nothing to do */ }
    }

    /// <summary>
    /// Notification daemons render a subset of HTML in the body, and package names come from remote catalogs (Flatpak search), so a
    /// name like <c>&lt;a href=…&gt;</c> would become a link. Escapes markup and drops control characters other than newlines.
    /// </summary>
    internal static string Escape(string s) =>
        new string([.. s.Where(c => c == '\n' || !char.IsControl(c))])
            .Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
}
