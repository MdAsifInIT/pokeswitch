using PokeSwitch.Models;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace PokeSwitch.Services;

public sealed record TrayActionHandlers(
    Func<Task> ToggleWslAsync,
    Func<Task> ToggleDockerAsync,
    Func<Task> ToggleGpuAsync,
    Func<Task> NuclearShutdownAsync,
    Action Restore,
    Action Exit);

public interface ITrayService : IDisposable
{
    bool IsEnabled { get; }
    void Configure(AppConfig config, TrayActionHandlers handlers);
    void Notify(string title, string message);
}

public sealed class TrayService : ITrayService
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private AppConfig? _config;
    private TrayActionHandlers? _handlers;

    public TrayService()
    {
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "PokeSwitch",
            Icon = ResolveTrayIcon(),
            Visible = false
        };
        _notifyIcon.DoubleClick += (_, _) => _handlers?.Restore();
    }

    public bool IsEnabled => _config?.Tray?.Enabled == true;

    public void Configure(AppConfig config, TrayActionHandlers handlers)
    {
        _config = config;
        _handlers = handlers;
        _notifyIcon.Visible = config.Tray?.Enabled == true;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.ContextMenuStrip = CreateMenu(handlers);
    }

    public void Notify(string title, string message)
    {
        if (!IsEnabled)
        {
            return;
        }

        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.ShowBalloonTip(3000);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    private static Forms.ContextMenuStrip CreateMenu(TrayActionHandlers handlers)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open PokeSwitch", null, (_, _) => handlers.Restore());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Toggle WSL Keep-Alive", null, async (_, _) => await handlers.ToggleWslAsync());
        menu.Items.Add("Toggle Docker Engine", null, async (_, _) => await handlers.ToggleDockerAsync());
        menu.Items.Add("Toggle NVIDIA GPU", null, async (_, _) => await handlers.ToggleGpuAsync());
        menu.Items.Add("Nuclear Shutdown", null, async (_, _) => await handlers.NuclearShutdownAsync());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => handlers.Exit());
        return menu;
    }

    private static Drawing.Icon ResolveTrayIcon()
    {
        try
        {
            string? processPath = Environment.ProcessPath;
            return processPath == null
                ? Drawing.SystemIcons.Application
                : Drawing.Icon.ExtractAssociatedIcon(processPath) ?? Drawing.SystemIcons.Application;
        }
        catch
        {
            return Drawing.SystemIcons.Application;
        }
    }
}
