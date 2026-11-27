using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VolumeMixer.Models;
using VolumeMixer.Services;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;

namespace VolumeMixer.Views;

public partial class FlyoutWindow : Window
{
    private readonly AudioService _audioService;
    private readonly AudioDeviceService _audioDevices;
    private readonly TabWebSocketServer _tabServer;
    private readonly SettingsService _settingsService;
    private readonly DispatcherTimer _refreshTimer;

    private readonly ObservableCollection<AudioSessionItem> _appSessions = new();
    private readonly ObservableCollection<BrowserTabItem> _browserTabs = new();
    private readonly HashSet<string> _expandedTrackKeys = new(StringComparer.OrdinalIgnoreCase);

    private DateTime _lastTickUtc = DateTime.UtcNow;
    private bool _suppressMasterEvents;
    private bool _suppressDeviceComboEvents;
    private bool _isClosingAnimated;
    private bool _isShowingSettings;
    private string? _recordingTarget;
    private IDisposable? _outsideClickHook;

    public Func<bool>? OnApplyHotkeys { get; set; }

    public FlyoutWindow(
        AudioService audioService,
        AudioDeviceService audioDevices,
        TabWebSocketServer tabServer,
        SettingsService settingsService)
    {
        InitializeComponent();

        _audioService = audioService;
        _audioDevices = audioDevices;
        _tabServer = tabServer;
        _settingsService = settingsService;

        AppSessionsList.ItemsSource = _appSessions;
        BrowserTabsList.ItemsSource = _browserTabs;

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _refreshTimer.Tick += (_, _) =>
        {
            var now = DateTime.UtcNow;
            double dt = Math.Clamp((now - _lastTickUtc).TotalSeconds, 0, 2.0);
            _lastTickUtc = now;

            RefreshAudioState(lightweight: true);
            AdvanceExpandedTabTimelines(dt);
        };

        _audioService.MediaMetadataUpdated += () =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (IsVisible)
                {
                    RefreshAudioState(lightweight: true);
                }
            });
        };

        _tabServer.TabsUpdated += () =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (IsVisible)
                {
                    RefreshTabs();
                    UpdateExtensionStatus();
                }
            });
        };
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Win32Interop.SetNoActivateStyle(this, true);
        Win32Interop.ApplyWindows11Backdrop(this, _settingsService.Current.BackdropMode);
    }

    public void ToggleAnimated()
    {
        if (IsVisible && !_isClosingAnimated)
        {
            HideAnimated();
        }
        else
        {
            ShowAnimated(openSettings: false);
        }
    }

    public void ShowAnimated(bool openSettings = false)
    {
        _isClosingAnimated = false;
        UpdateWindowGeometry();

        if (openSettings)
            SwitchToSettings(animate: false);
        else
            SwitchToMixer(animate: false);

        RefreshAudioState(lightweight: false);
        RefreshDevicesUi();
        RefreshTabs();
        LoadSettingsUi();

        RootGrid.BeginAnimation(OpacityProperty, null);
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
        WindowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        WindowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        RootGrid.Opacity = 0;
        SlideTransform.Y = 18;
        WindowScale.ScaleX = 0.96;
        WindowScale.ScaleY = 0.96;

        Show();
        Win32Interop.SetNoActivateStyle(this, true);
        Win32Interop.ApplyWindows11Backdrop(this, _settingsService.Current.BackdropMode);
        Win32Interop.ShowTopmostNoActivate(this);

        _outsideClickHook?.Dispose();
        _outsideClickHook = Win32Interop.InstallOutsideClickHook(this, () =>
        {
            if (_recordingTarget == null && !IsAnyDeviceDropdownOpen())
            {
                HideAnimated();
            }
        });

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(200);
        RootGrid.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, dur) { EasingFunction = ease });
        WindowScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1.0, dur) { EasingFunction = ease });
        WindowScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1.0, dur) { EasingFunction = ease });

        _lastTickUtc = DateTime.UtcNow;
        _refreshTimer.Start();
    }

    public void HideAnimated()
    {
        if (!IsVisible || _isClosingAnimated) return;
        _isClosingAnimated = true;
        _recordingTarget = null;
        _refreshTimer.Stop();
        _outsideClickHook?.Dispose();
        _outsideClickHook = null;

        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var dur = TimeSpan.FromMilliseconds(140);
        var fadeOut = new DoubleAnimation(RootGrid.Opacity, 0, dur) { EasingFunction = ease };

        fadeOut.Completed += (_, _) =>
        {
            if (_isClosingAnimated)
            {
                Hide();
                _isClosingAnimated = false;
            }
        };

        RootGrid.BeginAnimation(OpacityProperty, fadeOut);
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(SlideTransform.Y, 14, dur) { EasingFunction = ease });
        WindowScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(WindowScale.ScaleX, 0.97, dur) { EasingFunction = ease });
        WindowScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(WindowScale.ScaleY, 0.97, dur) { EasingFunction = ease });
    }

    private void UpdateWindowGeometry()
    {
        var wa = SystemParameters.WorkArea;
        double targetArea = wa.Width * wa.Height * 0.10;
        double width = Math.Clamp(Math.Sqrt(targetArea * 0.68), 340, 410);
        double height = Math.Clamp(targetArea / width, 450, 620);

        Width = Math.Round(width);
        Height = Math.Round(height);
        Left = wa.Right - Width - 12;
        Top = wa.Bottom - Height - 12;
    }

    private void RefreshAudioState(bool lightweight)
    {
        var snapshot = _audioService.GetSessionsSnapshot();

        _suppressMasterEvents = true;
        try
        {
            HeaderSubtitle.Text = _isShowingSettings ? "Горячие клавиши и система" : _audioService.DeviceName;
            if (!MasterSlider.IsMouseCaptureWithin)
            {
                MasterSlider.Value = _audioService.MasterVolume;
            }
            MasterMuteBtn.IsChecked = _audioService.MasterMuted;
            MasterMuteIcon.Text = _audioService.MasterMuted || _audioService.MasterVolume == 0 ? "🔇" : "🔊";
            MasterPercentText.Text = _audioService.MasterMuted ? "0%" : $"{(int)_audioService.MasterVolume}%";
        }
        finally
        {
            _suppressMasterEvents = false;
        }

        // Remove closed sessions
        var incomingIds = new HashSet<string>(snapshot.Select(x => x.Id), StringComparer.OrdinalIgnoreCase);
        for (int i = _appSessions.Count - 1; i >= 0; i--)
        {
            if (!incomingIds.Contains(_appSessions[i].Id))
            {
                _appSessions.RemoveAt(i);
            }
        }

        var now = DateTime.UtcNow;
        for (int i = 0; i < snapshot.Count; i++)
        {
            var incoming = snapshot[i];
            var existing = _appSessions.FirstOrDefault(x => string.Equals(x.Id, incoming.Id, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (!lightweight || (now - existing.LastUserEditUtc).TotalMilliseconds > 600)
                {
                    existing.UpdateFromSystem(incoming.Volume, incoming.IsMuted);
                }
                existing.DisplayName = incoming.DisplayName;
                if (existing.Icon == null && incoming.Icon != null)
                {
                    existing.Icon = incoming.Icon;
                }
                existing.CanControlPlayback = incoming.CanControlPlayback;
                existing.IsPlaying = incoming.IsPlaying;
                existing.CanPrev = incoming.CanPrev;
                existing.CanNext = incoming.CanNext;
                existing.MediaTitle = incoming.MediaTitle;
                existing.MediaArtist = incoming.MediaArtist;
                existing.MediaThumbnail = incoming.MediaThumbnail;
                existing.UpdateTimelineFromSource(
                    incoming.PositionSeconds,
                    incoming.DurationSeconds,
                    incoming.IsLive,
                    incoming.CanSeek);
                existing.IsExpanded = incoming.CanControlPlayback && _expandedTrackKeys.Contains(existing.TrackKey);
                existing.OnChanged = incoming.OnChanged;
                existing.OnTogglePlayPause = incoming.OnTogglePlayPause;
                existing.OnPrevious = incoming.OnPrevious;
                existing.OnNext = incoming.OnNext;
                existing.OnSeek = incoming.OnSeek;

                int currentIndex = _appSessions.IndexOf(existing);
                if (currentIndex != i && i < _appSessions.Count)
                {
                    _appSessions.Move(currentIndex, i);
                }
            }
            else
            {
                incoming.IsExpanded = incoming.CanControlPlayback && _expandedTrackKeys.Contains(incoming.TrackKey);
                _appSessions.Insert(Math.Min(i, _appSessions.Count), incoming);
            }
        }

        EmptyAppsText.Visibility = _appSessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshTabs()
    {
        var tabs = _tabServer.GetTabsSnapshot();

        // Remove closed tabs
        for (int i = _browserTabs.Count - 1; i >= 0; i--)
        {
            var cur = _browserTabs[i];
            if (!tabs.Any(t => t.TabId == cur.TabId && t.ClientId == cur.ClientId))
            {
                _browserTabs.RemoveAt(i);
            }
        }

        var now = DateTime.UtcNow;
        for (int i = 0; i < tabs.Count; i++)
        {
            var incoming = tabs[i];
            var existing = _browserTabs.FirstOrDefault(t => t.TabId == incoming.TabId && t.ClientId == incoming.ClientId);
            if (existing != null)
            {
                bool allowVolSync = (now - existing.LastUserEditUtc).TotalMilliseconds > 600;
                existing.UpdateFromExtension(
                    allowVolSync ? incoming.Volume : existing.Volume,
                    allowVolSync ? incoming.IsMuted : existing.IsMuted,
                    incoming.Title,
                    incoming.BrowserName,
                    incoming.IsAudible,
                    incoming.IsPlaying,
                    incoming.CanControlPlayback);
                existing.FavIconUrl = incoming.FavIconUrl;
                existing.ArtworkUrl = incoming.ArtworkUrl;
                if (incoming.Icon != null)
                {
                    existing.Icon = incoming.Icon;
                }
                if (incoming.MediaThumbnail != null)
                {
                    existing.MediaThumbnail = incoming.MediaThumbnail;
                }
                existing.MediaTitle = incoming.MediaTitle;
                existing.MediaArtist = incoming.MediaArtist;
                existing.CanPrev = incoming.CanPrev;
                existing.CanNext = incoming.CanNext;
                existing.UpdateTimelineFromSource(
                    incoming.PositionSeconds,
                    incoming.DurationSeconds,
                    incoming.IsLive,
                    incoming.CanSeek);
                existing.IsExpanded = incoming.CanControlPlayback && _expandedTrackKeys.Contains(existing.TrackKey);
                existing.OnChanged = incoming.OnChanged;
                existing.OnTogglePlayPause = incoming.OnTogglePlayPause;
                existing.OnPrevious = incoming.OnPrevious;
                existing.OnNext = incoming.OnNext;
                existing.OnSeek = incoming.OnSeek;

                int currentIndex = _browserTabs.IndexOf(existing);
                if (currentIndex != i && i < _browserTabs.Count)
                {
                    _browserTabs.Move(currentIndex, i);
                }
            }
            else
            {
                incoming.IsExpanded = incoming.CanControlPlayback && _expandedTrackKeys.Contains(incoming.TrackKey);
                _browserTabs.Insert(Math.Min(i, _browserTabs.Count), incoming);
            }
        }

        TabsSection.Visibility = _browserTabs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AdvanceExpandedTabTimelines(double dt)
    {
        if (dt <= 0) return;
        var now = DateTime.UtcNow;
        foreach (var tab in _browserTabs)
        {
            if (tab.IsExpanded && tab.IsPlaying && tab.ShowTimeline && (now - tab.LastSeekUtc).TotalMilliseconds > 700)
            {
                tab.SetPositionWithoutSeek(tab.PositionSeconds + dt);
            }
        }
    }

    private void MasterSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressMasterEvents) return;
        double vol = Math.Round(e.NewValue);
        bool muted = MasterMuteBtn.IsChecked == true;
        _audioService.SetMasterVolume(vol, muted);
        MasterPercentText.Text = muted ? "0%" : $"{(int)vol}%";
        MasterMuteIcon.Text = muted || vol == 0 ? "🔇" : "🔊";
    }

    private void MasterMuteBtn_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressMasterEvents) return;
        bool muted = MasterMuteBtn.IsChecked == true;
        double vol = Math.Round(MasterSlider.Value);
        _audioService.SetMasterVolume(vol, muted);
        MasterPercentText.Text = muted ? "0%" : $"{(int)vol}%";
        MasterMuteIcon.Text = muted || vol == 0 ? "🔇" : "🔊";
    }

    private void VolumeSlider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is Slider slider)
        {
            double step = e.Delta > 0 ? 3 : -3;
            slider.Value = Math.Clamp(slider.Value + step, slider.Minimum, slider.Maximum);
            e.Handled = true;
        }
    }

    private void PlayPauseTrackBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AudioTrackBase track })
        {
            track.OnTogglePlayPause?.Invoke();
        }
    }

    private void PrevTrackBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AudioTrackBase track })
        {
            track.OnPrevious?.Invoke();
        }
    }

    private void NextTrackBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AudioTrackBase track })
        {
            track.OnNext?.Invoke();
        }
    }

    private void ExpandMediaBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AudioTrackBase track })
        {
            bool next = !track.IsExpanded;
            track.IsExpanded = next;
            if (next)
                _expandedTrackKeys.Add(track.TrackKey);
            else
                _expandedTrackKeys.Remove(track.TrackKey);
        }
    }

    private bool IsAnyDeviceDropdownOpen() =>
        HeadsetOutputCombo.IsDropDownOpen
        || SpeakerOutputCombo.IsDropDownOpen
        || HeadsetMicCombo.IsDropDownOpen
        || DesktopMicCombo.IsDropDownOpen;

    private void RefreshDevicesUi()
    {
        bool hasProfiles = _audioDevices.HasAnyProfileConfigured;
        ProfileSwitchBar.Visibility = hasProfiles ? Visibility.Visible : Visibility.Collapsed;
        DevicesDrawerBtn.Visibility = hasProfiles ? Visibility.Visible : Visibility.Collapsed;
        HeadsetProfileBtn.Visibility = _audioDevices.HasHeadsetProfile ? Visibility.Visible : Visibility.Collapsed;
        SpeakersProfileBtn.Visibility = _audioDevices.HasSpeakersProfile ? Visibility.Visible : Visibility.Collapsed;

        bool isHeadset = string.Equals(_audioDevices.ActiveProfile, "headset", StringComparison.OrdinalIgnoreCase);
        HeadsetProfileBtn.Background = new SolidColorBrush(isHeadset ? Color.FromArgb(0x36, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        SpeakersProfileBtn.Background = new SolidColorBrush(!isHeadset ? Color.FromArgb(0x36, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));

        if (DevicesDrawer.Visibility == Visibility.Visible)
        {
            OutputsList.ItemsSource = _audioDevices.GetProfileOutputs();
            InputsList.ItemsSource = _audioDevices.GetProfileInputs();
        }
    }

    private void HeadsetProfileBtn_Click(object sender, RoutedEventArgs e)
    {
        _audioDevices.SwitchToHeadset();
        RefreshDevicesUi();
        RefreshAudioState(lightweight: false);
    }

    private void SpeakersProfileBtn_Click(object sender, RoutedEventArgs e)
    {
        _audioDevices.SwitchToSpeakers();
        RefreshDevicesUi();
        RefreshAudioState(lightweight: false);
    }

    private bool _devicesDrawerOpen;

    private void DevicesDrawerBtn_Click(object sender, RoutedEventArgs e)
    {
        _devicesDrawerOpen = !_devicesDrawerOpen;
        DevicesDrawerBtn.Content = _devicesDrawerOpen ? "Раздельно ▴" : "Раздельно ▾";

        if (_devicesDrawerOpen)
        {
            DevicesDrawer.Visibility = Visibility.Visible;
            RefreshDevicesUi();

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(190);
            DevicesDrawerScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(DevicesDrawerScale.ScaleY, 1, dur) { EasingFunction = ease });
            DevicesDrawer.BeginAnimation(OpacityProperty, new DoubleAnimation(DevicesDrawer.Opacity, 1, TimeSpan.FromMilliseconds(160)));
        }
        else
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var dur = TimeSpan.FromMilliseconds(140);
            var collapse = new DoubleAnimation(DevicesDrawerScale.ScaleY, 0, dur) { EasingFunction = ease };
            collapse.Completed += (_, _) =>
            {
                if (!_devicesDrawerOpen)
                {
                    DevicesDrawer.Visibility = Visibility.Collapsed;
                }
            };
            DevicesDrawerScale.BeginAnimation(ScaleTransform.ScaleYProperty, collapse);
            DevicesDrawer.BeginAnimation(OpacityProperty, new DoubleAnimation(DevicesDrawer.Opacity, 0, TimeSpan.FromMilliseconds(120)));
        }
    }

    private void SelectOutputDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            _audioDevices.SetDefaultDevice(id);
            RefreshDevicesUi();
            RefreshAudioState(lightweight: false);
        }
    }

    private void SelectInputDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            _audioDevices.SetDefaultDevice(id);
            RefreshDevicesUi();
        }
    }

    private void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isShowingSettings)
            SwitchToMixer(animate: true);
        else
            SwitchToSettings(animate: true);
    }

    private void SwitchToSettings(bool animate)
    {
        _isShowingSettings = true;
        _recordingTarget = null;
        HeaderTitle.Text = "Настройки";
        HeaderSubtitle.Text = "Горячие клавиши и система";
        SettingsBtnIcon.Text = "←";
        SettingsBtn.ToolTip = "Назад к микшеру";
        LoadSettingsUi();

        MixerPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Visible;

        if (animate)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(180);
            SettingsPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
            SettingsSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(18, 0, dur) { EasingFunction = ease });
        }
        else
        {
            SettingsPanel.BeginAnimation(OpacityProperty, null);
            SettingsSlide.BeginAnimation(TranslateTransform.XProperty, null);
            SettingsPanel.Opacity = 1;
            SettingsSlide.X = 0;
        }
    }

    private void SwitchToMixer(bool animate)
    {
        _isShowingSettings = false;
        _recordingTarget = null;
        HeaderTitle.Text = "Микшер громкости";
        HeaderSubtitle.Text = _audioService.DeviceName;
        SettingsBtnIcon.Text = "⚙";
        SettingsBtn.ToolTip = "Настройки и бинды";

        SettingsPanel.Visibility = Visibility.Collapsed;
        MixerPanel.Visibility = Visibility.Visible;

        if (animate)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(180);
            MixerPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
            MixerSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-18, 0, dur) { EasingFunction = ease });
        }
        else
        {
            MixerPanel.BeginAnimation(OpacityProperty, null);
            MixerSlide.BeginAnimation(TranslateTransform.XProperty, null);
            MixerPanel.Opacity = 1;
            MixerSlide.X = 0;
        }
    }

    private void LoadSettingsUi()
    {
        var s = _settingsService.Current;
        BindToggleBtn.Content = s.ToggleMixerHotkey.ToString();
        BindMasterMuteBtn.Content = s.MasterMuteHotkey.ToString();
        BindActiveMuteBtn.Content = s.ActiveWindowMuteHotkey.ToString();
        StartupSwitch.IsChecked = s.RunAtStartup;

        bool isAcrylic = !string.Equals(s.BackdropMode, "Mica", StringComparison.OrdinalIgnoreCase);
        AcrylicBtn.Background = new SolidColorBrush(isAcrylic ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
        MicaBtn.Background = new SolidColorBrush(!isAcrylic ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));

        CopyExtPathBtn.Content = "Копировать путь к расширению";
        UpdateExtensionStatus();
        if (_isShowingSettings)
        {
            LoadDeviceProfileCombos();
        }
    }

    private void LoadDeviceProfileCombos()
    {
        var outputs = _audioDevices.GetAllOutputs();
        var inputs = _audioDevices.GetAllInputs();
        var profiles = _settingsService.Current.AudioProfiles;

        _suppressDeviceComboEvents = true;
        try
        {
            PopulateDeviceCombo(HeadsetOutputCombo, outputs, profiles.HeadsetOutputId);
            PopulateDeviceCombo(SpeakerOutputCombo, outputs, profiles.SpeakerOutputId);
            PopulateDeviceCombo(HeadsetMicCombo, inputs, profiles.HeadsetMicId);
            PopulateDeviceCombo(DesktopMicCombo, inputs, profiles.DesktopMicId);
        }
        finally
        {
            _suppressDeviceComboEvents = false;
        }
    }

    private static void PopulateDeviceCombo(System.Windows.Controls.ComboBox combo, List<AudioEndpointOption> devices, string? savedId)
    {
        var items = new List<AudioEndpointOption>(devices.Count + 2)
        {
            new() { Id = string.Empty, Name = "— Не выбрано —" }
        };
        items.AddRange(devices);

        if (!string.IsNullOrWhiteSpace(savedId)
            && !devices.Any(d => string.Equals(d.Id, savedId, StringComparison.OrdinalIgnoreCase)))
        {
            items.Add(new AudioEndpointOption { Id = savedId, Name = "(Устройство не подключено)" });
        }

        combo.ItemsSource = items;
        combo.SelectedValue = string.IsNullOrWhiteSpace(savedId) ? string.Empty : savedId;
    }

    private void RefreshDeviceCombosBtn_Click(object sender, RoutedEventArgs e)
    {
        LoadDeviceProfileCombos();
    }

    private void DeviceProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDeviceComboEvents) return;

        var profiles = _settingsService.Current.AudioProfiles;
        if (sender == HeadsetOutputCombo)
            profiles.HeadsetOutputId = NullIfEmpty(HeadsetOutputCombo.SelectedValue as string);
        else if (sender == SpeakerOutputCombo)
            profiles.SpeakerOutputId = NullIfEmpty(SpeakerOutputCombo.SelectedValue as string);
        else if (sender == HeadsetMicCombo)
            profiles.HeadsetMicId = NullIfEmpty(HeadsetMicCombo.SelectedValue as string);
        else if (sender == DesktopMicCombo)
            profiles.DesktopMicId = NullIfEmpty(DesktopMicCombo.SelectedValue as string);

        _settingsService.Save();
        RefreshDevicesUi();
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private void UpdateExtensionStatus()
    {
        if (_tabServer.HasConnectedClients)
        {
            ExtStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
            ExtStatusText.Text = "Подключено";
        }
        else
        {
            ExtStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A));
            ExtStatusText.Text = "Ожидание браузера";
        }
    }

    private void BindBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string target) return;

        LoadSettingsUi();
        _recordingTarget = target;
        btn.Content = "Нажмите клавиши...";
        BindHintText.Text = "Нажмите комбинацию клавиш (Esc для отмены)";
        Win32Interop.SetNoActivateStyle(this, false);
        Activate();
    }

    private void ClearBindBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string target) return;

        var s = _settingsService.Current;
        if (target == "MasterMute") s.MasterMuteHotkey = new HotkeyBinding();
        else if (target == "ActiveMute") s.ActiveWindowMuteHotkey = new HotkeyBinding();

        _settingsService.Save();
        OnApplyHotkeys?.Invoke();
        LoadSettingsUi();
    }

    private void BackdropBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string mode) return;
        _settingsService.Current.BackdropMode = mode;
        _settingsService.Save();
        Win32Interop.ApplyWindows11Backdrop(this, mode);
        LoadSettingsUi();
    }

    private void StartupSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _settingsService.Current.RunAtStartup = StartupSwitch.IsChecked == true;
        _settingsService.Save();
    }

    private void CopyExtPathBtn_Click(object sender, RoutedEventArgs e)
    {
        string baseDir = AppContext.BaseDirectory;
        string? extDir = FindExtensionDirectory(baseDir);
        if (extDir != null)
        {
            Clipboard.SetText(extDir);
            CopyExtPathBtn.Content = "Путь скопирован!";
        }
        else
        {
            CopyExtPathBtn.Content = "Папка VolumeMixer.Extension не найдена";
        }
    }

    private static string? FindExtensionDirectory(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        for (int i = 0; i < 9 && dir != null; i++)
        {
            string candidate = Path.Combine(dir.FullName, "VolumeMixer.Extension");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => HideAnimated();

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (_recordingTarget == null)
        {
            HideAnimated();
        }
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_recordingTarget != null)
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key == Key.Escape)
            {
                _recordingTarget = null;
                Win32Interop.SetNoActivateStyle(this, true);
                BindHintText.Text = "Нажмите на кнопку и выберите комбинацию клавиш";
                LoadSettingsUi();
                return;
            }

            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            {
                return;
            }

            uint mods = 0;
            if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) mods |= 1;
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) mods |= 2;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) mods |= 4;
            if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) mods |= 8;

            bool isFunctionKey = key >= Key.F1 && key <= Key.F24;
            if (mods == 0 && !isFunctionKey)
            {
                BindHintText.Text = "Добавьте Ctrl, Alt или Shift (или выберите F1-F12)";
                return;
            }

            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            var binding = new HotkeyBinding { Modifiers = mods, VirtualKey = vk };

            var s = _settingsService.Current;
            switch (_recordingTarget)
            {
                case "Toggle": s.ToggleMixerHotkey = binding; break;
                case "MasterMute": s.MasterMuteHotkey = binding; break;
                case "ActiveMute": s.ActiveWindowMuteHotkey = binding; break;
            }

            _recordingTarget = null;
            Win32Interop.SetNoActivateStyle(this, true);
            _settingsService.Save();
            bool registered = OnApplyHotkeys?.Invoke() ?? true;
            BindHintText.Text = registered
                ? "Нажмите на кнопку и выберите комбинацию клавиш"
                : "Конфликт: комбинация занята другой программой";
            LoadSettingsUi();
            return;
        }

        if (e.Key == Key.Escape)
        {
            HideAnimated();
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _refreshTimer.Stop();
        _outsideClickHook?.Dispose();
        _outsideClickHook = null;
        base.OnClosed(e);
    }
}
