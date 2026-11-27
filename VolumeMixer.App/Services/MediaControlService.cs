using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace VolumeMixer.Services;

public sealed class MediaControlService
{
    public sealed class CachedMediaMeta
    {
        public string Title { get; init; } = string.Empty;
        public string Artist { get; init; } = string.Empty;
        public ImageSource? Thumbnail { get; init; }
        public DateTime FetchedUtc { get; init; }
    }

    public sealed record PlaybackMatch(
        bool CanControl,
        bool IsPlaying,
        bool CanPrev,
        bool CanNext,
        bool CanSeek,
        string Title,
        string Artist,
        ImageSource? Thumbnail,
        double PositionSeconds,
        double DurationSeconds,
        GlobalSystemMediaTransportControlsSession Session);

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private Task<GlobalSystemMediaTransportControlsSessionManager?>? _initTask;
    private readonly ConcurrentDictionary<string, CachedMediaMeta> _metaCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pendingMeta = new(StringComparer.OrdinalIgnoreCase);

    public event Action? MetadataUpdated;

    public void EnsureInitialized()
    {
        if (_manager != null || _initTask != null) return;

        _initTask = Task.Run(async () =>
        {
            try
            {
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                return _manager;
            }
            catch
            {
                return null;
            }
        });
    }

    public PlaybackMatch? FindSessionForProcess(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName) || processName == "System")
            return null;

        EnsureInitialized();
        var manager = _manager;
        if (manager == null) return null;

        try
        {
            var sessions = manager.GetSessions();
            foreach (var session in sessions)
            {
                string aumid = session.SourceAppUserModelId ?? string.Empty;
                if (string.IsNullOrWhiteSpace(aumid)) continue;

                string cleanAumid = aumid.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? aumid[..^4]
                    : aumid;

                bool matches = string.Equals(cleanAumid, processName, StringComparison.OrdinalIgnoreCase)
                    || (processName.Length >= 3 && aumid.Contains(processName, StringComparison.OrdinalIgnoreCase));

                if (!matches) continue;

                var info = session.GetPlaybackInfo();
                var status = info.PlaybackStatus;
                bool canControl = info.Controls.IsPlayEnabled
                    || info.Controls.IsPauseEnabled
                    || status is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                              or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;

                if (!canControl) continue;

                bool isPlaying = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                bool canPrev = info.Controls.IsPreviousEnabled;
                bool canNext = info.Controls.IsNextEnabled;
                bool canSeek = info.Controls.IsPlaybackPositionEnabled;

                double posSec = 0;
                double durSec = 0;
                try
                {
                    var tl = session.GetTimelineProperties();
                    var start = tl.StartTime;
                    var end = tl.EndTime;
                    if (end > start)
                    {
                        durSec = (end - start).TotalSeconds;
                        var pos = tl.Position;
                        if (isPlaying && tl.LastUpdatedTime != DateTimeOffset.MinValue)
                        {
                            var elapsed = DateTimeOffset.UtcNow - tl.LastUpdatedTime;
                            if (elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromHours(12))
                            {
                                pos += elapsed;
                            }
                        }
                        posSec = Math.Clamp((pos - start).TotalSeconds, 0, durSec);
                    }
                }
                catch
                {
                    // Timeline unavailable
                }

                string cacheKey = $"{aumid}|{processName}";
                _metaCache.TryGetValue(cacheKey, out var cached);

                // Refresh cached metadata if missing or older than 2 seconds
                if (cached == null || (DateTime.UtcNow - cached.FetchedUtc).TotalSeconds > 2)
                {
                    QueueMetaRefresh(cacheKey, session);
                }

                return new PlaybackMatch(
                    true,
                    isPlaying,
                    canPrev,
                    canNext,
                    canSeek,
                    cached?.Title ?? string.Empty,
                    cached?.Artist ?? string.Empty,
                    cached?.Thumbnail,
                    posSec,
                    durSec,
                    session);
            }
        }
        catch
        {
            // Ignore transient WinRT SMTC errors
        }

        return null;
    }

    private void QueueMetaRefresh(string cacheKey, GlobalSystemMediaTransportControlsSession session)
    {
        if (!_pendingMeta.TryAdd(cacheKey, 1)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var props = await session.TryGetMediaPropertiesAsync();
                if (props != null)
                {
                    string title = props.Title ?? string.Empty;
                    string artist = props.Artist ?? string.Empty;

                    _metaCache.TryGetValue(cacheKey, out var prev);
                    ImageSource? thumb = prev?.Thumbnail;

                    // Decode thumbnail only if artwork changed or not decoded yet
                    bool sameTrack = prev != null
                        && string.Equals(prev.Title, title, StringComparison.Ordinal)
                        && string.Equals(prev.Artist, artist, StringComparison.Ordinal)
                        && thumb != null;

                    if (!sameTrack && props.Thumbnail != null)
                    {
                        thumb = await DecodeThumbnailAsync(props.Thumbnail);
                    }

                    bool changed = prev == null
                        || !string.Equals(prev.Title, title, StringComparison.Ordinal)
                        || !string.Equals(prev.Artist, artist, StringComparison.Ordinal)
                        || !ReferenceEquals(prev.Thumbnail, thumb);

                    _metaCache[cacheKey] = new CachedMediaMeta
                    {
                        Title = title,
                        Artist = artist,
                        Thumbnail = thumb,
                        FetchedUtc = DateTime.UtcNow
                    };

                    if (changed)
                    {
                        MetadataUpdated?.Invoke();
                    }
                }
            }
            catch
            {
                // Ignore expired session metadata errors
            }
            finally
            {
                _pendingMeta.TryRemove(cacheKey, out _);
            }
        });
    }

    private static async Task<ImageSource?> DecodeThumbnailAsync(IRandomAccessStreamReference streamRef)
    {
        try
        {
            using var winrtStream = await streamRef.OpenReadAsync();
            using var netStream = winrtStream.AsStreamForRead();
            using var ms = new MemoryStream();
            await netStream.CopyToAsync(ms);
            ms.Position = 0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 96;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public static async Task TogglePlayPauseAsync(GlobalSystemMediaTransportControlsSession session)
    {
        try { await session.TryTogglePlayPauseAsync(); } catch { }
    }

    public static async Task SkipPreviousAsync(GlobalSystemMediaTransportControlsSession session)
    {
        try { await session.TrySkipPreviousAsync(); } catch { }
    }

    public static async Task SkipNextAsync(GlobalSystemMediaTransportControlsSession session)
    {
        try { await session.TrySkipNextAsync(); } catch { }
    }

    public static async Task SeekAsync(GlobalSystemMediaTransportControlsSession session, double seconds)
    {
        try
        {
            long ticks = TimeSpan.FromSeconds(Math.Max(0, seconds)).Ticks;
            await session.TryChangePlaybackPositionAsync(ticks);
        }
        catch { }
    }
}
