using System.IO;
using System.Text.Json;

namespace VolumeMixer.Services;

public sealed class SavedVolume
{
    public double Volume { get; set; } = 100;
    public bool Muted { get; set; }
}

public sealed class VolumeStoreData
{
    public Dictionary<string, SavedVolume> Apps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class VolumeStoreService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly object _lock = new();
    private readonly object _fileLock = new();
    private VolumeStoreData _data;
    private readonly System.Threading.Timer _saveTimer;
    private bool _disposed;

    public VolumeStoreService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VolumeMixer");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "volumes.json");
        _data = Load();
        _saveTimer = new System.Threading.Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool TryGetAppVolume(string processName, out SavedVolume? saved)
    {
        lock (_lock)
        {
            return _data.Apps.TryGetValue(processName, out saved);
        }
    }

    public void SetAppVolume(string processName, double volume, bool muted)
    {
        if (string.IsNullOrWhiteSpace(processName)) return;

        lock (_lock)
        {
            _data.Apps[processName] = new SavedVolume
            {
                Volume = Math.Clamp(Math.Round(volume), 0, 100),
                Muted = muted
            };
        }

        ScheduleSave();
    }

    private VolumeStoreData Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var loaded = JsonSerializer.Deserialize<VolumeStoreData>(json, JsonOptions);
                if (loaded != null)
                {
                    loaded.Apps = new Dictionary<string, SavedVolume>(loaded.Apps, StringComparer.OrdinalIgnoreCase);
                    return loaded;
                }
            }
        }
        catch
        {
            // Ignore corrupted file
        }

        return new VolumeStoreData();
    }

    private void ScheduleSave()
    {
        if (_disposed) return;
        try
        {
            _saveTimer.Change(400, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Flush()
    {
        try
        {
            string json;
            lock (_lock)
            {
                json = JsonSerializer.Serialize(_data, JsonOptions);
            }
            lock (_fileLock)
            {
                File.WriteAllText(_filePath, json);
            }
        }
        catch
        {
            // Ignore write errors
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _saveTimer.Dispose();
        Flush();
    }
}
