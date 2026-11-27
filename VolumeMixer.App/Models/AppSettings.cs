using System.Text.Json.Serialization;

namespace VolumeMixer.Models;

public sealed class HotkeyBinding
{
    public uint Modifiers { get; set; } // MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4, MOD_WIN=8
    public uint VirtualKey { get; set; }

    [JsonIgnore]
    public bool IsEmpty => VirtualKey == 0;

    public override string ToString()
    {
        if (IsEmpty) return "Не назначено";

        var parts = new List<string>();
        if ((Modifiers & 2) != 0) parts.Add("Ctrl");
        if ((Modifiers & 1) != 0) parts.Add("Alt");
        if ((Modifiers & 4) != 0) parts.Add("Shift");
        if ((Modifiers & 8) != 0) parts.Add("Win");

        var key = System.Windows.Input.KeyInterop.KeyFromVirtualKey((int)VirtualKey);
        parts.Add(FormatKeyName(key));
        return string.Join(" + ", parts);
    }

    private static string FormatKeyName(System.Windows.Input.Key key) => key switch
    {
        >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9 => ((char)('0' + (key - System.Windows.Input.Key.D0))).ToString(),
        System.Windows.Input.Key.OemTilde => "~",
        System.Windows.Input.Key.OemMinus => "-",
        System.Windows.Input.Key.OemPlus => "+",
        System.Windows.Input.Key.OemOpenBrackets => "[",
        System.Windows.Input.Key.OemCloseBrackets => "]",
        System.Windows.Input.Key.Space => "Space",
        _ => key.ToString()
    };
}

public sealed class AppSettings
{
    public HotkeyBinding ToggleMixerHotkey { get; set; } = new()
    {
        Modifiers = 2 | 4, // Ctrl + Shift
        VirtualKey = 0x56  // V
    };

    public HotkeyBinding MasterMuteHotkey { get; set; } = new();
    public HotkeyBinding ActiveWindowMuteHotkey { get; set; } = new();

    public string BackdropMode { get; set; } = "Acrylic"; // "Acrylic" or "Mica"
    public bool RunAtStartup { get; set; } = false;
    public int WebSocketPort { get; set; } = 49152;
}
