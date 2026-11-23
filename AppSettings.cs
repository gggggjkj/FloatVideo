using System.Text.Json;

namespace FloatVideo;

internal sealed class HotkeyBinding
{
    public Keys Key { get; set; }
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }

    public HotkeyBinding() { }
    public HotkeyBinding(Keys key, bool ctrl, bool alt, bool shift = false)
        => (Key, Ctrl, Alt, Shift) = (key, ctrl, alt, shift);

    public bool IsValid => Key != Keys.None && Key is not
        (Keys.ControlKey or Keys.LControlKey or Keys.RControlKey or
         Keys.Menu or Keys.LMenu or Keys.RMenu or
         Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey or Keys.LWin or Keys.RWin);
    public override string ToString()
        => Key == Keys.None ? "未設定" :
            $"{(Ctrl ? "Ctrl + " : "")}{(Alt ? "Alt + " : "")}{(Shift ? "Shift + " : "")}{Key}";
}

internal sealed class AppSettings
{
    public string LastUrl { get; set; } = "";
    public int X { get; set; } = -1;
    public int Y { get; set; } = -1;
    public int Width { get; set; } = 680;
    public int Height { get; set; } = 430;
    public int Volume { get; set; } = 80;
    public bool TopMost { get; set; } = true;
    public Dictionary<string, HotkeyBinding> Hotkeys { get; set; } = Defaults();

    public static Dictionary<string, HotkeyBinding> Defaults() => new()
    {
        ["toggle"] = new(Keys.Space, true, true),
        ["back"] = new(Keys.Left, true, true),
        ["forward"] = new(Keys.Right, true, true),
        ["volumeDown"] = new(Keys.Down, true, true),
        ["volumeUp"] = new(Keys.Up, true, true)
    };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FloatVideo", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var data = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (data != null)
                {
                    foreach (var (name, binding) in Defaults())
                        data.Hotkeys.TryAdd(name, binding);
                    return data;
                }
            }
        }
        catch { /* Start with defaults if settings are corrupt or unavailable. */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* Playback remains usable if preferences cannot be saved. */ }
    }
}
