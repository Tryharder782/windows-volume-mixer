using System.IO;
using System.Text.Json;
using NAudio.CoreAudioApi;

namespace VolumeMixer.Services;

public sealed class AudioEndpointOption
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool IsDefault { get; init; }
}

public sealed class CoreAudioTrayConfig
{
    public string? HeadsetOutputId { get; set; }
    public string? SpeakerOutputId { get; set; }
    public string? HeadsetMicId { get; set; }
    public string? DesktopMicId { get; set; }
    public bool AutoSwitchEnabled { get; set; }
    public int AutoSwitchCooldownSeconds { get; set; } = 5;
    public bool StartMinimizedToTray { get; set; }
    public bool StartWithWindows { get; set; }
    public string LastProfile { get; set; } = "speakers";
}

public sealed class AudioDeviceBridgeService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly VolumeStoreService _volumeStore;
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly string _coreAudioConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CoreAudioTray",
        "config.json");

    public AudioDeviceBridgeService(VolumeStoreService volumeStore)
    {
        _volumeStore = volumeStore;
        ReloadConfig();
    }

    public CoreAudioTrayConfig? CoreAudioConfig { get; private set; }

    public bool HasCoreAudioProfiles =>
        CoreAudioConfig != null
        && !string.IsNullOrWhiteSpace(CoreAudioConfig.HeadsetOutputId)
        && !string.IsNullOrWhiteSpace(CoreAudioConfig.SpeakerOutputId);

    public string ActiveProfile { get; private set; } = "speakers";

    public void RestoreLockedProfileOnStartup()
    {
        ReloadConfig();
        if (CoreAudioConfig == null) return;

        string? locked = _volumeStore.GetLockedProfile();
        if (string.IsNullOrWhiteSpace(locked)) return;

        if (string.Equals(locked, "headset", StringComparison.OrdinalIgnoreCase))
        {
            ApplyProfile(CoreAudioConfig.HeadsetOutputId, CoreAudioConfig.HeadsetMicId);
            ActiveProfile = "headset";
        }
        else if (string.Equals(locked, "speakers", StringComparison.OrdinalIgnoreCase))
        {
            ApplyProfile(CoreAudioConfig.SpeakerOutputId, CoreAudioConfig.DesktopMicId);
            ActiveProfile = "speakers";
        }
    }

    public void ReloadConfig()
    {
        try
        {
            if (File.Exists(_coreAudioConfigPath))
            {
                var json = File.ReadAllText(_coreAudioConfigPath);
                CoreAudioConfig = JsonSerializer.Deserialize<CoreAudioTrayConfig>(json);
            }
            else
            {
                CoreAudioConfig = null;
            }
        }
        catch
        {
            CoreAudioConfig = null;
        }

        string? locked = _volumeStore.GetLockedProfile();
        if (!string.IsNullOrWhiteSpace(locked))
        {
            ActiveProfile = string.Equals(locked, "headset", StringComparison.OrdinalIgnoreCase)
                ? "headset"
                : "speakers";
            return;
        }

        ActiveProfile = CoreAudioConfig != null
            && string.Equals(CoreAudioConfig.LastProfile, "headset", StringComparison.OrdinalIgnoreCase)
            ? "headset"
            : "speakers";
    }

    public List<AudioEndpointOption> GetProfileOutputs()
    {
        var list = new List<AudioEndpointOption>(2);
        if (CoreAudioConfig == null) return list;

        string? defaultId = GetDefaultEndpointId(DataFlow.Render);

        if (!string.IsNullOrWhiteSpace(CoreAudioConfig.HeadsetOutputId))
        {
            list.Add(new AudioEndpointOption
            {
                Id = CoreAudioConfig.HeadsetOutputId,
                Name = "🎧 Наушники",
                IsDefault = string.Equals(CoreAudioConfig.HeadsetOutputId, defaultId, StringComparison.OrdinalIgnoreCase)
            });
        }

        if (!string.IsNullOrWhiteSpace(CoreAudioConfig.SpeakerOutputId))
        {
            list.Add(new AudioEndpointOption
            {
                Id = CoreAudioConfig.SpeakerOutputId,
                Name = "🔊 Спикеры",
                IsDefault = string.Equals(CoreAudioConfig.SpeakerOutputId, defaultId, StringComparison.OrdinalIgnoreCase)
            });
        }

        return list;
    }

    public List<AudioEndpointOption> GetProfileInputs()
    {
        var list = new List<AudioEndpointOption>(2);
        if (CoreAudioConfig == null) return list;

        string? defaultId = GetDefaultEndpointId(DataFlow.Capture);

        if (!string.IsNullOrWhiteSpace(CoreAudioConfig.HeadsetMicId))
        {
            list.Add(new AudioEndpointOption
            {
                Id = CoreAudioConfig.HeadsetMicId,
                Name = "🎧 Микрофон наушников",
                IsDefault = string.Equals(CoreAudioConfig.HeadsetMicId, defaultId, StringComparison.OrdinalIgnoreCase)
            });
        }

        if (!string.IsNullOrWhiteSpace(CoreAudioConfig.DesktopMicId))
        {
            list.Add(new AudioEndpointOption
            {
                Id = CoreAudioConfig.DesktopMicId,
                Name = "🎙 Настольный микрофон",
                IsDefault = string.Equals(CoreAudioConfig.DesktopMicId, defaultId, StringComparison.OrdinalIgnoreCase)
            });
        }

        return list;
    }

    public bool SwitchToHeadset()
    {
        ReloadConfig();
        if (CoreAudioConfig == null) return false;

        bool changed = ApplyProfile(CoreAudioConfig.HeadsetOutputId, CoreAudioConfig.HeadsetMicId);
        ActiveProfile = "headset";
        _volumeStore.SetLockedProfile("headset");
        SaveLastProfile("headset");
        return changed;
    }

    public bool SwitchToSpeakers()
    {
        ReloadConfig();
        if (CoreAudioConfig == null) return false;

        bool changed = ApplyProfile(CoreAudioConfig.SpeakerOutputId, CoreAudioConfig.DesktopMicId);
        ActiveProfile = "speakers";
        _volumeStore.SetLockedProfile("speakers");
        SaveLastProfile("speakers");
        return changed;
    }

    public bool SetDefaultDevice(string deviceId)
    {
        return TrySetDefault(deviceId);
    }

    private bool ApplyProfile(string? outputId, string? micId)
    {
        bool changed = false;
        if (TrySetDefault(outputId)) changed = true;
        TrySetDefault(micId);
        return changed;
    }

    private string? GetDefaultEndpointId(DataFlow flow)
    {
        try
        {
            using var device = _enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            return device.ID;
        }
        catch
        {
            return null;
        }
    }

    private bool TrySetDefault(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;

        try
        {
            using var device = _enumerator.GetDevice(deviceId);
            if (device.State != DeviceState.Active) return false;

            PolicyConfigClient.SetDefaultForAllRoles(deviceId);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SaveLastProfile(string profileName)
    {
        if (CoreAudioConfig == null) return;

        try
        {
            CoreAudioConfig.LastProfile = profileName;
            var json = JsonSerializer.Serialize(CoreAudioConfig, JsonOptions);
            File.WriteAllText(_coreAudioConfigPath, json);
        }
        catch
        {
            // Ignore write errors if CoreAudioTray is writing concurrently
        }
    }

    public void Dispose()
    {
        _enumerator.Dispose();
    }
}
