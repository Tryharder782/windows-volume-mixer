using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using VolumeMixer.Models;

namespace VolumeMixer.Services;

public sealed class SettingsService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppRunName = "VolumeMixer";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public AppSettings Current { get; private set; }

    public SettingsService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VolumeMixer");
        Directory.CreateDirectory(dir);
        _settingsPath = Path.Combine(dir, "settings.json");
        Current = Load();
        ApplyStartupSetting(Current.RunAtStartup);
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded != null) return loaded;
            }
        }
        catch
        {
            // Fall back to defaults if config is corrupted
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Current, JsonOptions);
            File.WriteAllText(_settingsPath, json);
            ApplyStartupSetting(Current.RunAtStartup);
        }
        catch
        {
            // Ignore write errors
        }
    }

    private static void ApplyStartupSetting(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null) return;

            if (enable)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    key.SetValue(AppRunName, $"\"{exePath}\" --tray");
                }
            }
            else
            {
                if (key.GetValue(AppRunName) != null)
                {
                    key.DeleteValue(AppRunName, throwOnMissingValue: false);
                }
            }
        }
        catch
        {
            // Ignore registry permission errors
        }
    }
}
