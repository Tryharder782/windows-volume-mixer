using System.Threading;
using System.Windows;
using VolumeMixer.Services;
using VolumeMixer.Views;

namespace VolumeMixer;

public partial class App : System.Windows.Application
{
    private readonly string _desktopName = Win32Interop.GetCurrentDesktopName();
    private string MutexName => $"VolumeMixer_SingleInstance_{_desktopName}";
    private string ShowEventName => $"VolumeMixer_ShowFlyout_{_desktopName}";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWaitHandle;
    private SettingsService? _settingsService;
    private VolumeStoreService? _volumeStore;
    private AudioService? _audioService;
    private AudioDeviceService? _audioDevices;
    private TabWebSocketServer? _tabServer;
    private HotkeyService? _hotkeyService;
    private TrayService? _trayService;
    private FlyoutWindow? _flyoutWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, MutexName, out bool isNew);
        if (!isNew)
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var existingEvent))
            {
                using (existingEvent)
                {
                    existingEvent.Set();
                }
            }
            Shutdown();
            return;
        }

        _settingsService = new SettingsService();
        _volumeStore = new VolumeStoreService();
        _audioService = new AudioService(_volumeStore);
        _audioDevices = new AudioDeviceService(_settingsService);
        _audioDevices.RestoreProfileOnStartup();
        _tabServer = new TabWebSocketServer(_settingsService.Current.WebSocketPort);
        _tabServer.Start();

        _flyoutWindow = new FlyoutWindow(_audioService, _audioDevices, _tabServer, _settingsService);

        _hotkeyService = new HotkeyService();
        _hotkeyService.ApplySettings(_settingsService.Current);
        _hotkeyService.ToggleMixerPressed += () => _flyoutWindow.ToggleAnimated();
        _hotkeyService.MasterMutePressed += () => _audioService.ToggleMasterMute();
        _hotkeyService.ActiveWindowMutePressed += () =>
        {
            int pid = Win32Interop.GetForegroundProcessId();
            _audioService.ToggleProcessMute(pid);
        };

        _flyoutWindow.OnApplyHotkeys = () => _hotkeyService.ApplySettings(_settingsService.Current);

        _trayService = new TrayService();
        _trayService.ToggleRequested += () => _flyoutWindow.ToggleAnimated();
        _trayService.SettingsRequested += () => _flyoutWindow.ShowAnimated(openSettings: true);
        _trayService.ExitRequested += () => Shutdown();

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWaitHandle = ThreadPool.RegisterWaitForSingleObject(
            _showEvent,
            (_, _) => Dispatcher.BeginInvoke(() => _flyoutWindow.ShowAnimated(openSettings: false)),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        bool startInTray = e.Args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));
        if (!startInTray)
        {
            _flyoutWindow.ShowAnimated(openSettings: false);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWaitHandle?.Unregister(_showEvent);
        _showEvent?.Dispose();
        _trayService?.Dispose();
        _hotkeyService?.Dispose();
        _tabServer?.Dispose();
        _audioDevices?.Dispose();
        _audioService?.Dispose();
        _volumeStore?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
