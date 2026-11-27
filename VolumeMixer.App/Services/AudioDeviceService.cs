using NAudio.CoreAudioApi;
using VolumeMixer.Models;

namespace VolumeMixer.Services;

public sealed class AudioEndpointOption
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool IsDefault { get; init; }
}

public sealed class AudioDeviceService : IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly MMDeviceEnumerator _enumerator = new();

    public AudioDeviceService(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    private AudioProfilesSettings Profiles => _settingsService.Current.AudioProfiles;

    public bool HasHeadsetProfile =>
        !string.IsNullOrWhiteSpace(Profiles.HeadsetOutputId)
        || !string.IsNullOrWhiteSpace(Profiles.HeadsetMicId);

    public bool HasSpeakersProfile =>
        !string.IsNullOrWhiteSpace(Profiles.SpeakerOutputId)
        || !string.IsNullOrWhiteSpace(Profiles.DesktopMicId);

    public bool HasAnyProfileConfigured => HasHeadsetProfile || HasSpeakersProfile;

    public string ActiveProfile =>
        string.Equals(Profiles.ActiveProfile, "headset", StringComparison.OrdinalIgnoreCase)
            ? "headset"
            : "speakers";

    public void RestoreProfileOnStartup()
    {
        string? saved = Profiles.ActiveProfile;
        if (string.IsNullOrWhiteSpace(saved)) return;

        if (string.Equals(saved, "headset", StringComparison.OrdinalIgnoreCase))
        {
            ApplyProfile(Profiles.HeadsetOutputId, Profiles.HeadsetMicId);
        }
        else if (string.Equals(saved, "speakers", StringComparison.OrdinalIgnoreCase))
        {
            ApplyProfile(Profiles.SpeakerOutputId, Profiles.DesktopMicId);
        }
    }

    public List<AudioEndpointOption> GetAllOutputs() => EnumerateActiveEndpoints(DataFlow.Render);
    public List<AudioEndpointOption> GetAllInputs() => EnumerateActiveEndpoints(DataFlow.Capture);

    public List<AudioEndpointOption> GetProfileOutputs()
    {
        var list = new List<AudioEndpointOption>(2);
        string? defaultId = GetDefaultEndpointId(DataFlow.Render);

        if (!string.IsNullOrWhiteSpace(Profiles.HeadsetOutputId))
        {
            list.Add(new AudioEndpointOption
            {
                Id = Profiles.HeadsetOutputId,
                Name = "🎧 Наушники",
                IsDefault = string.Equals(Profiles.HeadsetOutputId, defaultId, StringComparison.OrdinalIgnoreCase)
            });
        }

        if (!string.IsNullOrWhiteSpace(Profiles.SpeakerOutputId))
        {
            list.Add(new AudioEndpointOption
            {
                Id = Profiles.SpeakerOutputId,
                Name = "🔊 Спикеры",
                IsDefault = string.Equals(Profiles.SpeakerOutputId, defaultId, StringComparison.OrdinalIgnoreCase)
            });
        }

        return list;
    }

    public List<AudioEndpointOption> GetProfileInputs()
    {
        var list = new List<AudioEndpointOption>(2);
        string? defaultId = GetDefaultEndpointId(DataFlow.Capture);

        if (!string.IsNullOrWhiteSpace(Profiles.HeadsetMicId))
        {
            list.Add(new AudioEndpointOption
            {
                Id = Profiles.HeadsetMicId,
                Name = "🎧 Микрофон наушников",
                IsDefault = string.Equals(Profiles.HeadsetMicId, defaultId, StringComparison.OrdinalIgnoreCase)
            });
        }

        if (!string.IsNullOrWhiteSpace(Profiles.DesktopMicId))
        {
            list.Add(new AudioEndpointOption
            {
                Id = Profiles.DesktopMicId,
                Name = "🎙 Настольный микрофон",
                IsDefault = string.Equals(Profiles.DesktopMicId, defaultId, StringComparison.OrdinalIgnoreCase)
            });
        }

        return list;
    }

    public bool SwitchToHeadset()
    {
        bool changed = ApplyProfile(Profiles.HeadsetOutputId, Profiles.HeadsetMicId);
        Profiles.ActiveProfile = "headset";
        _settingsService.Save();
        return changed;
    }

    public bool SwitchToSpeakers()
    {
        bool changed = ApplyProfile(Profiles.SpeakerOutputId, Profiles.DesktopMicId);
        Profiles.ActiveProfile = "speakers";
        _settingsService.Save();
        return changed;
    }

    public bool SetDefaultDevice(string deviceId) => TrySetDefault(deviceId);

    private bool ApplyProfile(string? outputId, string? micId)
    {
        bool changed = false;
        if (TrySetDefault(outputId)) changed = true;
        TrySetDefault(micId);
        return changed;
    }

    private List<AudioEndpointOption> EnumerateActiveEndpoints(DataFlow flow)
    {
        var list = new List<AudioEndpointOption>();
        string? defaultId = GetDefaultEndpointId(flow);

        try
        {
            var devices = _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
            foreach (var device in devices)
            {
                using (device)
                {
                    list.Add(new AudioEndpointOption
                    {
                        Id = device.ID,
                        Name = string.IsNullOrWhiteSpace(device.FriendlyName) ? device.ID : device.FriendlyName,
                        IsDefault = string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            // Ignore enumeration errors
        }

        return list;
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

    public void Dispose()
    {
        _enumerator.Dispose();
    }
}
