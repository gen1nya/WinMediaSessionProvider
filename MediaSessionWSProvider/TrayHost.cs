using System.Reflection;

namespace MediaSessionWSProvider;

public class TrayHost : IDisposable
{
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _menu;
    private readonly FftService _fftService;
    private readonly ToolStripMenuItem _fftToggleItem;
    private readonly ToolStripMenuItem _deviceMenu;

    public TrayHost(FftService fftService)
    {
        _fftService = fftService;
        _menu = new ContextMenuStrip();
        _fftToggleItem = new ToolStripMenuItem("FFT") { CheckOnClick = true };
        _fftToggleItem.CheckedChanged += (s, e) => _fftService.Enable(_fftToggleItem.Checked);
        _fftToggleItem.Checked = _fftService.IsEnabled;

        _deviceMenu = new ToolStripMenuItem("Устройство");
        _deviceMenu.DropDownOpening += DeviceMenuOnDropDownOpening;

        _menu.Items.Add(_fftToggleItem);
        _menu.Items.Add(_deviceMenu);
        _menu.Items.Add(new ToolStripSeparator());
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

    private void DeviceMenuOnDropDownOpening(object? sender, EventArgs e)
    {
        _deviceMenu.DropDownItems.Clear();
        foreach (var dev in _fftService.GetDevices())
        {
            var item = new ToolStripMenuItem(dev.Name) { Tag = dev };
            if (_fftService.CurrentDevice?.Device.ID == dev.Device.ID)
                item.Checked = true;
            item.Click += DeviceMenuItemOnClick;
            _deviceMenu.DropDownItems.Add(item);
        }
    }

    private void DeviceMenuItemOnClick(object? sender, EventArgs e)
    {
        if (sender is ToolStripMenuItem item && item.Tag is FftService.AudioDeviceInfo info)
        {
            _fftService.SetDevice(info);
        }
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