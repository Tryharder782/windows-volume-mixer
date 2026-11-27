using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using VolumeMixer.Models;

namespace VolumeMixer.Services;

public sealed class AudioService : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly MediaControlService _mediaControl = new();
    private readonly VolumeStoreService _volumeStore;
    private readonly HashSet<string> _activeGroupKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _nameCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<AudioSessionControl>> _sessionGroups = new(StringComparer.OrdinalIgnoreCase);

    public AudioService(VolumeStoreService volumeStore)
    {
        _volumeStore = volumeStore;
        _mediaControl.EnsureInitialized();
    }

    public event Action? MediaMetadataUpdated
    {
        add => _mediaControl.MetadataUpdated += value;
        remove => _mediaControl.MetadataUpdated -= value;
    }

    public string DeviceName { get; private set; } = "Динамики";
    public double MasterVolume { get; private set; } = 100;
    public bool MasterMuted { get; private set; }

    public List<AudioSessionItem> GetSessionsSnapshot()
    {
        var result = new List<AudioSessionItem>();
        ClearSessionGroups();

        try
        {
            using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            DeviceName = device.FriendlyName;
            MasterVolume = Math.Round(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
            MasterMuted = device.AudioEndpointVolume.Mute;

            var sessions = device.AudioSessionManager.Sessions;
            var groupedMeta = new Dictionary<string, (int Pid, string ProcessName, string DisplayName, string? ExePath, double Vol, bool Muted)>(StringComparer.OrdinalIgnoreCase);
            var currentGroupKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (session.State == AudioSessionState.AudioSessionStateExpired)
                {
                    session.Dispose();
                    continue;
                }

                int pid = (int)session.GetProcessID;
                if (pid == Environment.ProcessId)
                {
                    session.Dispose();
                    continue;
                }

                string processName = "System";
                string displayName = session.DisplayName;
                string? exePath = null;

                if (pid > 0)
                {
                    try
                    {
                        using var proc = Process.GetProcessById(pid);
                        processName = proc.ProcessName;
                        try
                        {
                            exePath = proc.MainModule?.FileName;
                        }
                        catch
                        {
                            // Access denied for some protected processes
                        }

                        if (string.IsNullOrWhiteSpace(displayName) || displayName.StartsWith("@%"))
                        {
                            displayName = GetCachedFriendlyName(exePath) ?? FormatAppName(processName);
                        }
                    }
                    catch
                    {
                        processName = $"PID {pid}";
                    }
                }
                else
                {
                    displayName = "Системные звуки";
                }

                if (string.IsNullOrWhiteSpace(displayName))
                {
                    displayName = FormatAppName(processName);
                }

                string groupKey = pid == 0 ? "__system__" : processName;

                if (!_sessionGroups.TryGetValue(groupKey, out var list))
                {
                    list = new List<AudioSessionControl>();
                    _sessionGroups[groupKey] = list;

                    double vol = Math.Round(session.SimpleAudioVolume.Volume * 100);
                    bool muted = session.SimpleAudioVolume.Mute;
                    groupedMeta[groupKey] = (pid, processName, displayName, exePath, vol, muted);
                }

                list.Add(session);
            }

            foreach (var kvp in groupedMeta)
            {
                var key = kvp.Key;
                var meta = kvp.Value;
                currentGroupKeys.Add(key);

                var mediaMatch = _mediaControl.FindSessionForProcess(meta.ProcessName);

                var item = new AudioSessionItem
                {
                    Id = key,
                    ProcessId = meta.Pid,
                    ProcessName = meta.ProcessName,
                    DisplayName = meta.DisplayName,
                    Icon = GetCachedIcon(meta.ExePath),
                    CanControlPlayback = mediaMatch?.CanControl ?? false,
                    IsPlaying = mediaMatch?.IsPlaying ?? false,
                    CanPrev = mediaMatch?.CanPrev ?? false,
                    CanNext = mediaMatch?.CanNext ?? false,
                    MediaTitle = !string.IsNullOrWhiteSpace(mediaMatch?.Title) ? mediaMatch.Title : meta.DisplayName,
                    MediaArtist = !string.IsNullOrWhiteSpace(mediaMatch?.Artist) ? mediaMatch.Artist : meta.DisplayName,
                    MediaThumbnail = mediaMatch?.Thumbnail
                };

                if (mediaMatch != null)
                {
                    item.UpdateTimelineFromSource(
                        mediaMatch.PositionSeconds,
                        mediaMatch.DurationSeconds,
                        isLive: false,
                        canSeek: mediaMatch.CanSeek);
                }

                item.UpdateFromSystem(meta.Vol, meta.Muted);

                // Restore saved volume whenever this app session newly appears
                if (!_activeGroupKeys.Contains(key) && _volumeStore.TryGetAppVolume(key, out var saved) && saved != null)
                {
                    ApplyGroupVolume(key, saved.Volume, saved.Muted, saveToStore: false);
                    item.UpdateFromSystem(saved.Volume, saved.Muted);
                }

                item.OnChanged = (vol, muted) => ApplyGroupVolume(key, vol, muted, saveToStore: true);
                if (mediaMatch != null)
                {
                    var smtcSession = mediaMatch.Session;
                    item.OnTogglePlayPause = () =>
                    {
                        item.IsPlaying = !item.IsPlaying;
                        _ = MediaControlService.TogglePlayPauseAsync(smtcSession);
                    };
                    item.OnPrevious = () => _ = MediaControlService.SkipPreviousAsync(smtcSession);
                    item.OnNext = () => _ = MediaControlService.SkipNextAsync(smtcSession);
                    item.OnSeek = sec => _ = MediaControlService.SeekAsync(smtcSession, sec);
                }
                result.Add(item);
            }

            _activeGroupKeys.Clear();
            foreach (var key in currentGroupKeys)
            {
                _activeGroupKeys.Add(key);
            }

            // Sort: System Sounds last, others alphabetically
            result.Sort((a, b) =>
            {
                if (a.ProcessId == 0 && b.ProcessId != 0) return 1;
                if (b.ProcessId == 0 && a.ProcessId != 0) return -1;
                return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            });
        }
        catch
        {
            // No active audio device or WASAPI unavailable
        }

        return result;
    }

    public void SetMasterVolume(double volume, bool muted)
    {
        try
        {
            using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            device.AudioEndpointVolume.MasterVolumeLevelScalar = (float)Math.Clamp(volume / 100.0, 0.0, 1.0);
            device.AudioEndpointVolume.Mute = muted;
            MasterVolume = Math.Round(volume);
            MasterMuted = muted;
        }
        catch
        {
            // Ignore audio device errors
        }
    }

    public bool ToggleMasterMute()
    {
        try
        {
            using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            bool next = !device.AudioEndpointVolume.Mute;
            device.AudioEndpointVolume.Mute = next;
            MasterMuted = next;
            return next;
        }
        catch
        {
            return MasterMuted;
        }
    }

    public void ToggleProcessMute(int pid)
    {
        if (pid <= 0) return;

        try
        {
            using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;
            bool? targetMute = null;

            for (int i = 0; i < sessions.Count; i++)
            {
                using var session = sessions[i];
                if ((int)session.GetProcessID == pid)
                {
                    targetMute ??= !session.SimpleAudioVolume.Mute;
                    session.SimpleAudioVolume.Mute = targetMute.Value;
                }
            }
        }
        catch
        {
            // Ignore
        }
    }

    private void ApplyGroupVolume(string groupKey, double volume, bool muted, bool saveToStore)
    {
        if (saveToStore)
        {
            _volumeStore.SetAppVolume(groupKey, volume, muted);
        }

        if (!_sessionGroups.TryGetValue(groupKey, out var controls)) return;

        float scalar = (float)Math.Clamp(volume / 100.0, 0.0, 1.0);
        foreach (var ctrl in controls)
        {
            try
            {
                ctrl.SimpleAudioVolume.Volume = scalar;
                ctrl.SimpleAudioVolume.Mute = muted;
            }
            catch
            {
                // Session may have expired while flyout was open
            }
        }
    }

    private ImageSource? GetCachedIcon(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return null;

        if (_iconCache.TryGetValue(exePath, out var cached))
            return cached;

        ImageSource? source = null;
        try
        {
            using Icon? icon = Icon.ExtractAssociatedIcon(exePath);
            if (icon != null)
            {
                var bmp = Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(24, 24));
                bmp.Freeze();
                source = bmp;
            }
        }
        catch
        {
            source = null;
        }

        _iconCache[exePath] = source;
        return source;
    }

    private string? GetCachedFriendlyName(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return null;

        if (_nameCache.TryGetValue(exePath, out var cached))
            return cached;

        string? desc = null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            if (!string.IsNullOrWhiteSpace(info.FileDescription))
            {
                desc = info.FileDescription.Trim();
            }
        }
        catch
        {
            desc = null;
        }

        _nameCache[exePath] = desc;
        return desc;
    }

    private static string FormatAppName(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return "App";
        return char.ToUpper(processName[0]) + processName[1..];
    }

    private void ClearSessionGroups()
    {
        foreach (var list in _sessionGroups.Values)
        {
            foreach (var ctrl in list)
            {
                try { ctrl.Dispose(); } catch { }
            }
        }
        _sessionGroups.Clear();
    }

    public void Dispose()
    {
        ClearSessionGroups();
        _enumerator.Dispose();
    }
}
