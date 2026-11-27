using System.Windows.Interop;
using VolumeMixer.Models;

namespace VolumeMixer.Services;

public sealed class HotkeyService : IDisposable
{
    private const int HotkeyToggleId = 1;
    private const int HotkeyMasterMuteId = 2;
    private const int HotkeyActiveWindowMuteId = 3;

    private readonly HwndSource _source;

    public event Action? ToggleMixerPressed;
    public event Action? MasterMutePressed;
    public event Action? ActiveWindowMutePressed;

    public HotkeyService()
    {
        var parameters = new HwndSourceParameters("VolumeMixerHotkeySink")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public bool ApplySettings(AppSettings settings)
    {
        UnregisterAll();

        bool allOk = true;
        if (!settings.ToggleMixerHotkey.IsEmpty)
        {
            allOk &= Win32Interop.RegisterHotKey(
                _source.Handle,
                HotkeyToggleId,
                settings.ToggleMixerHotkey.Modifiers,
                settings.ToggleMixerHotkey.VirtualKey);
        }

        if (!settings.MasterMuteHotkey.IsEmpty)
        {
            allOk &= Win32Interop.RegisterHotKey(
                _source.Handle,
                HotkeyMasterMuteId,
                settings.MasterMuteHotkey.Modifiers,
                settings.MasterMuteHotkey.VirtualKey);
        }

        if (!settings.ActiveWindowMuteHotkey.IsEmpty)
        {
            allOk &= Win32Interop.RegisterHotKey(
                _source.Handle,
                HotkeyActiveWindowMuteId,
                settings.ActiveWindowMuteHotkey.Modifiers,
                settings.ActiveWindowMuteHotkey.VirtualKey);
        }

        return allOk;
    }

    public void UnregisterAll()
    {
        if (_source.Handle == IntPtr.Zero) return;
        Win32Interop.UnregisterHotKey(_source.Handle, HotkeyToggleId);
        Win32Interop.UnregisterHotKey(_source.Handle, HotkeyMasterMuteId);
        Win32Interop.UnregisterHotKey(_source.Handle, HotkeyActiveWindowMuteId);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32Interop.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            switch (id)
            {
                case HotkeyToggleId:
                    ToggleMixerPressed?.Invoke();
                    handled = true;
                    break;
                case HotkeyMasterMuteId:
                    MasterMutePressed?.Invoke();
                    handled = true;
                    break;
                case HotkeyActiveWindowMuteId:
                    ActiveWindowMutePressed?.Invoke();
                    handled = true;
                    break;
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
