using System.Windows.Forms;

namespace UpdateForge.Desktop.Services;

/// <summary>System tray icon (native NotifyIcon). Balloon tips surface as native Windows notifications and respect Do Not Disturb.</summary>
public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _pause;

    public event Action? OpenRequested;
    public event Action? ScanRequested;
    public event Action? UpdateRequested;
    public event Action? HistoryRequested;
    public event Action? SettingsRequested;
    public event Action<bool>? PauseToggled;
    public event Action? ExitRequested;

    public TrayService()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open UpdateForge", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add("Scan for updates", null, (_, _) => ScanRequested?.Invoke());
        menu.Items.Add("Update approved applications", null, (_, _) => UpdateRequested?.Invoke());
        _pause = new ToolStripMenuItem("Pause automatic updates") { CheckOnClick = true };
        _pause.CheckedChanged += (_, _) => PauseToggled?.Invoke(_pause.Checked);
        menu.Items.Add(_pause);
        menu.Items.Add("View update history", null, (_, _) => HistoryRequested?.Invoke());
        menu.Items.Add("Settings", null, (_, _) => SettingsRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        _icon = new NotifyIcon
        {
            Text = "UpdateForge",
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    public void SetPaused(bool paused) => _pause.Checked = paused;

    public void SetStatus(int updates) => _icon.Text = updates > 0 ? $"UpdateForge - {updates} update(s) available" : "UpdateForge - up to date";

    public void Notify(string title, string text) => _icon.ShowBalloonTip(6000, title, text, ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
