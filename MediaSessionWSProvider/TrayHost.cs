using System.Reflection;

namespace MediaSessionWSProvider;

public class TrayHost : IDisposable
{
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _menu;

    public TrayHost()
    {
        _menu = new ContextMenuStrip();
        _menu.Items.Add("Выход", null, OnExitClicked);

        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("MediaSessionWSProvider.icon.ico");
        var icon = new Icon(stream!);
        
        _trayIcon = new NotifyIcon
        {
            Icon = icon,
            Text = "Media Session Agent",
            Visible = true,
            ContextMenuStrip = _menu
        };
    }

    private void OnExitClicked(object? sender, EventArgs e)
    {
        Application.Exit();
    }

    public void Dispose()
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _menu.Dispose();
    }
}