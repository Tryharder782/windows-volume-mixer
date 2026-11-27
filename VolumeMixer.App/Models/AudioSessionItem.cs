using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace VolumeMixer.Models;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public abstract class AudioTrackBase : ObservableObject
{
    private double _volume = 100;
    private bool _isMuted;
    private ImageSource? _icon;
    private bool _canControlPlayback;
    private bool _isPlaying;
    private bool _isExpanded;

    // Lock-screen media metadata & timeline
    private ImageSource? _mediaThumbnail;
    private string _mediaTitle = string.Empty;
    private string _mediaArtist = string.Empty;
    private double _positionSeconds;
    private double _durationSeconds;
    private bool _isLive;
    private bool _canSeek;
    private bool _canPrev;
    private bool _canNext;

    protected bool _suppressCallback;
    private bool _suppressSeekCallback;

    public bool IsScrubbing { get; set; }
    public DateTime LastUserEditUtc { get; protected set; }
    public DateTime LastSeekUtc { get; protected set; }

    public Action<double, bool>? OnChanged { get; set; }
    public Action? OnTogglePlayPause { get; set; }
    public Action? OnPrevious { get; set; }
    public Action? OnNext { get; set; }
    public Action<double>? OnSeek { get; set; }

    public abstract string TrackKey { get; }

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (SetField(ref _icon, value))
            {
                OnPropertyChanged(nameof(HasIcon));
                OnPropertyChanged(nameof(EffectiveThumbnail));
            }
        }
    }

    public bool HasIcon => Icon != null;

    public ImageSource? MediaThumbnail
    {
        get => _mediaThumbnail;
        set
        {
            if (SetField(ref _mediaThumbnail, value))
            {
                OnPropertyChanged(nameof(EffectiveThumbnail));
            }
        }
    }

    public ImageSource? EffectiveThumbnail => MediaThumbnail ?? Icon;

    public string MediaTitle
    {
        get => _mediaTitle;
        set => SetField(ref _mediaTitle, value);
    }

    public string MediaArtist
    {
        get => _mediaArtist;
        set => SetField(ref _mediaArtist, value);
    }

    public bool CanControlPlayback
    {
        get => _canControlPlayback;
        set => SetField(ref _canControlPlayback, value);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (SetField(ref _isPlaying, value))
            {
                OnPropertyChanged(nameof(PlayPauseGlyph));
            }
        }
    }

    public string PlayPauseGlyph => IsPlaying ? "⏸" : "▶";

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetField(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(ExpandGlyph));
            }
        }
    }

    public string ExpandGlyph => IsExpanded ? "▴" : "▾";

    public bool CanSeek
    {
        get => _canSeek;
        set => SetField(ref _canSeek, value);
    }

    public bool CanPrev
    {
        get => _canPrev;
        set => SetField(ref _canPrev, value);
    }

    public bool CanNext
    {
        get => _canNext;
        set => SetField(ref _canNext, value);
    }

    public bool IsLive
    {
        get => _isLive;
        set
        {
            if (SetField(ref _isLive, value))
            {
                OnPropertyChanged(nameof(DurationText));
                OnPropertyChanged(nameof(ShowTimeline));
            }
        }
    }

    public bool ShowTimeline => !IsLive && DurationSeconds > 0;

    public double DurationSeconds
    {
        get => _durationSeconds;
        set
        {
            var safe = Math.Max(0, value);
            if (SetField(ref _durationSeconds, safe))
            {
                OnPropertyChanged(nameof(DurationText));
                OnPropertyChanged(nameof(ShowTimeline));
                OnPropertyChanged(nameof(SeekMaximum));
            }
        }
    }

    public double SeekMaximum => DurationSeconds > 0 ? DurationSeconds : 1;

    public double PositionSeconds
    {
        get => _positionSeconds;
        set
        {
            var clamped = DurationSeconds > 0
                ? Math.Clamp(value, 0, DurationSeconds)
                : Math.Max(0, value);

            if (SetField(ref _positionSeconds, clamped))
            {
                OnPropertyChanged(nameof(PositionText));
                if (!_suppressSeekCallback && !IsScrubbing && DurationSeconds > 0)
                {
                    LastSeekUtc = DateTime.UtcNow;
                    OnSeek?.Invoke(clamped);
                }
            }
        }
    }

    public string PositionText => FormatTime(PositionSeconds);
    public string DurationText => IsLive ? "LIVE" : FormatTime(DurationSeconds);

    public double Volume
    {
        get => _volume;
        set
        {
            var clamped = Math.Clamp(Math.Round(value), 0, 100);
            if (SetField(ref _volume, clamped))
            {
                OnPropertyChanged(nameof(VolumeText));
                if (!_suppressCallback)
                {
                    LastUserEditUtc = DateTime.UtcNow;
                    OnChanged?.Invoke(_volume, _isMuted);
                }
            }
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (SetField(ref _isMuted, value))
            {
                OnPropertyChanged(nameof(VolumeText));
                if (!_suppressCallback)
                {
                    LastUserEditUtc = DateTime.UtcNow;
                    OnChanged?.Invoke(_volume, _isMuted);
                }
            }
        }
    }

    public string VolumeText => IsMuted ? "0%" : $"{(int)Volume}%";

    public void UpdateTimelineFromSource(double positionSeconds, double durationSeconds, bool isLive, bool canSeek)
    {
        if (IsScrubbing) return;

        _suppressSeekCallback = true;
        try
        {
            IsLive = isLive;
            CanSeek = canSeek && !isLive && durationSeconds > 0;
            DurationSeconds = durationSeconds;
            if ((DateTime.UtcNow - LastSeekUtc).TotalMilliseconds > 700)
            {
                PositionSeconds = positionSeconds;
            }
        }
        finally
        {
            _suppressSeekCallback = false;
        }
    }

    public void SetPositionWithoutSeek(double positionSeconds)
    {
        if (IsScrubbing) return;

        _suppressSeekCallback = true;
        try
        {
            PositionSeconds = positionSeconds;
        }
        finally
        {
            _suppressSeekCallback = false;
        }
    }

    public void CommitSeek(double positionSeconds)
    {
        IsScrubbing = false;
        if (DurationSeconds <= 0) return;

        var clamped = Math.Clamp(positionSeconds, 0, DurationSeconds);
        LastSeekUtc = DateTime.UtcNow;
        SetField(ref _positionSeconds, clamped, nameof(PositionSeconds));
        OnPropertyChanged(nameof(PositionText));
        OnSeek?.Invoke(clamped);
    }

    private static string FormatTime(double totalSeconds)
    {
        if (double.IsNaN(totalSeconds) || double.IsInfinity(totalSeconds) || totalSeconds < 0)
            return "00:00";

        var ts = TimeSpan.FromSeconds(totalSeconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}

public sealed class AudioSessionItem : AudioTrackBase
{
    private string _displayName = string.Empty;

    public string Id { get; init; } = string.Empty;
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;

    public override string TrackKey => $"app:{Id}";

    public string DisplayName
    {
        get => _displayName;
        set => SetField(ref _displayName, value);
    }

    public void UpdateFromSystem(double volume, bool isMuted)
    {
        _suppressCallback = true;
        try
        {
            Volume = volume;
            IsMuted = isMuted;
        }
        finally
        {
            _suppressCallback = false;
        }
    }
}

public sealed class BrowserTabItem : AudioTrackBase
{
    private string _title = string.Empty;
    private string _browserName = "Browser";
    private bool _isAudible;

    public int TabId { get; init; }
    public string ClientId { get; init; } = string.Empty;
    public string? FavIconUrl { get; set; }
    public string? ArtworkUrl { get; set; }

    public override string TrackKey => $"tab:{ClientId}:{TabId}";

    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }

    public string BrowserName
    {
        get => _browserName;
        set => SetField(ref _browserName, value);
    }

    public bool IsAudible
    {
        get => _isAudible;
        set => SetField(ref _isAudible, value);
    }

    public void UpdateFromExtension(double volume, bool isMuted, string title, string browserName, bool isAudible, bool isPlaying, bool canControlPlayback)
    {
        _suppressCallback = true;
        try
        {
            Volume = volume;
            IsMuted = isMuted;
            Title = title;
            BrowserName = browserName;
            IsAudible = isAudible;
            IsPlaying = isPlaying;
            CanControlPlayback = canControlPlayback;
        }
        finally
        {
            _suppressCallback = false;
        }
    }
}
