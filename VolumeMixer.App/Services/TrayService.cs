using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VolumeMixer.Services;

public sealed class TrayService : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _trayIcon;
    private readonly ContextMenuStrip _menu;

    public event Action? ToggleRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public TrayService()
    {
        _trayIcon = CreateSpeakerIcon();

        _menu = new ContextMenuStrip();
        _menu.Items.Add("Открыть микшер", null, (_, _) => ToggleRequested?.Invoke());
        _menu.Items.Add("Настройки биндов", null, (_, _) => SettingsRequested?.Invoke());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Выход", null, (_, _) => ExitRequested?.Invoke());

        _notifyIcon = new NotifyIcon
        {
            Icon = _trayIcon,
            Text = "Volume Mixer",
            Visible = true,
            ContextMenuStrip = _menu
        };

        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ToggleRequested?.Invoke();
            }
        };
    }

    private static Icon CreateSpeakerIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var brush = new SolidBrush(Color.White);
        using var pen = new Pen(Color.White, 2.4f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        // Speaker cone
        var cone = new[]
        {
            new PointF(5, 12),
            new PointF(10, 12),
            new PointF(16, 7),
            new PointF(16, 25),
            new PointF(10, 20),
            new PointF(5, 20)
        };
        g.FillPolygon(brush, cone);

        // Sound waves
        g.DrawArc(pen, 15, 10, 8, 12, -50, 100);
        g.DrawArc(pen, 17, 6, 12, 20, -50, 100);

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _trayIcon.Dispose();
    }
}
