using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InputFX;

public class ScreenFx
{
    public bool Enabled { get; set; } = true;
    public bool OnKeys { get; set; } = true;
    public bool OnMouseLeft { get; set; } = true;
    public bool OnMouseRight { get; set; } = true;
    public bool OnMouseMiddle { get; set; } = true;
    public bool OnMouseSide { get; set; } = true;
    public bool OnWheel { get; set; } = false;
    /// <summary>Explosion, Fireworks, Sparks, Confetti, Shockwave, Pixels</summary>
    public string Style { get; set; } = "Explosion";
    /// <summary>Fire, Fixed, Rainbow, Ice, Neon, Random</summary>
    public string ColorMode { get; set; } = "Fire";
    public string Color { get; set; } = "#FF8A1E";
    public int SizePercent { get; set; } = 100;
    public int Particles { get; set; } = 36;
    public int DurationMs { get; set; } = 700;
    public int GravityPercent { get; set; } = 40;
    public bool Shockwave { get; set; } = true;
    public bool Flash { get; set; } = true;
    public bool Sound { get; set; } = false;
    public int VolumePercent { get; set; } = 35;
    public bool SoundOnKeys { get; set; } = false;
    /// <summary>Cursor, Caret, WindowCenter, Random</summary>
    public string KeyPosition { get; set; } = "Cursor";
    /// <summary>All, Only, Except</summary>
    public string KeyFilterMode { get; set; } = "All";
    public string KeyFilterList { get; set; } = "";
    public bool IgnoreModifiers { get; set; } = true;
    public bool IgnoreAutoRepeat { get; set; } = true;
}

public class KeyboardHw
{
    public bool Enabled { get; set; } = false;
    /// <summary>Explosion, Pulse, Ripple, Key</summary>
    public string Style { get; set; } = "Explosion";
    /// <summary>Off, Static, Dim, Rainbow, Breathing</summary>
    public string Base { get; set; } = "Static";
    public string BaseColor { get; set; } = "#1B2A6B";
    public string BurstColor { get; set; } = "#FF6A00";
    public bool RainbowBurst { get; set; } = false;
    public int Brightness { get; set; } = 100;
    public int RadiusKeys { get; set; } = 5;
    public int DurationMs { get; set; } = 650;
    public bool OnMouse { get; set; } = true;
    /// <summary>true = use the keyboard's own stored mode (smooth); false = custom stream when Enabled.</summary>
    public bool UseHwMode { get; set; } = false;
    public int HwMode { get; set; } = 7;
    /// <summary>Speed (0-4) and brightness (0-4) written together with the built-in mode.</summary>
    public int HwSpeed { get; set; } = 4;
    public int HwBrightness { get; set; } = 4;
    /// <summary>How fast the custom burst expands, 25-400 (%).</summary>
    public int BurstSpeedPercent { get; set; } = 100;
    /// <summary>How fast an animated background plays, 25-400 (%).</summary>
    public int BgSpeedPercent { get; set; } = 100;
    /// <summary>Fixed, Random, Rainbow, Fire</summary>
    public string BurstColorMode { get; set; } = "Fixed";
    // ---- audio reactive (system sound) ----
    public bool AudioEnabled { get; set; } = false;
    /// <summary>Equalizer, Beat, Bass, Flow</summary>
    public string AudioStyle { get; set; } = "Equalizer";
    public int AudioSensitivity { get; set; } = 60;
}

public class MouseHw
{
    /// <summary>"" means leave alone. Otherwise a name from MouseLab.Modes.</summary>
    public string Mode { get; set; } = "";
    /// <summary>Experimental: pulse the mouse's own lights on every click (uses the Music-lighting live command).</summary>
    public bool ClickPulse { get; set; } = false;
    /// <summary>Experimental: the mouse lights follow the music (same live command).</summary>
    public bool AudioReactive { get; set; } = false;
    public int AudioSensitivity { get; set; } = 60;
    /// <summary>Pop, Flash, Glow, Double, Strobe, Heartbeat</summary>
    public string PulseStyle { get; set; } = "Flash";
    /// <summary>Peak brightness level of the pulse, 1-7.</summary>
    public int PulseStrength { get; set; } = 7;
    /// <summary>How fast the pulse plays, 25-400 (%).</summary>
    public int PulseSpeedPercent { get; set; } = 100;
    /// <summary>How long the pulse stays at its peak (ms).</summary>
    public int PulseHoldMs { get; set; } = 60;
    /// <summary>Zones = light every zone at once (symmetrical, from the Flicker music commands); Bar = the Wave level bar that sweeps left to right.</summary>
    public string PulseMode { get; set; } = "Zones";
    /// <summary>Both, A, B: which group of zones flashes (A = mask F0, B = mask 0F).</summary>
    public string PulseZones { get; set; } = "Both";
    /// <summary>Off, All, A, B: what stays lit between pulses while live mode is on.</summary>
    public string IdleLight { get; set; } = "Off";
}

public class RemapRule
{
    public bool Enabled { get; set; } = true;
    /// <summary>A key name (A, CapsLock, F13, LeftCtrl...) or MouseMiddle / MouseX1 / MouseX2</summary>
    public string Source { get; set; } = "";
    /// <summary>Key, Text, Run, Macro, Block, Profile</summary>
    public string Action { get; set; } = "Key";
    public string Target { get; set; } = "";
    public string Note { get; set; } = "";
}

public class MacroDef
{
    public string Name { get; set; } = "New macro";
    /// <summary>Once, WhileHeld, Toggle</summary>
    public string Mode { get; set; } = "Once";
    public int Repeat { get; set; } = 1;
    public string Script { get; set; } = "press Ctrl+C\ndelay 50\ntext hello";
}

public class Profile
{
    public string Name { get; set; } = "Default";
    public bool IsDefault { get; set; }
    /// <summary>Process names (no .exe) that activate this profile automatically.</summary>
    public List<string> Apps { get; set; } = new();
    public ScreenFx Screen { get; set; } = new();
    public KeyboardHw Keyboard { get; set; } = new();
    public MouseHw Mouse { get; set; } = new();
    public List<RemapRule> Remaps { get; set; } = new();
    public List<MacroDef> Macros { get; set; } = new();
    public List<AutoTool> AutoTools { get; set; } = new();

    public override string ToString() => Name;
}

public class AppConfig
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
    public bool AutoSwitch { get; set; } = true;
    public string ActiveProfile { get; set; } = "Default";
    /// <summary>Last keyboard hardware mode we wrote (0 = unknown / streaming was used since).</summary>
    public int LastKbMode { get; set; } = 0;
    /// <summary>Index into HwLab.MouseVariants that was last written to the mouse (0 = Button Response).</summary>
    public int LastMouseVariant { get; set; } = 0;
    /// <summary>The first-run device window has been completed.</summary>
    public bool WizardDone { get; set; } = false;
    /// <summary>Background (low power) mode: no overlay, no streamed lights, no audio, no games. Remaps and macros keep working.</summary>
    public bool EcoMode { get; set; } = false;
    /// <summary>Extra program names (no .exe) where automation must stay off. The built-in game, anti-cheat and full-screen rules always apply.</summary>
    public List<string> ExtraGameApps { get; set; } = new();
    public AlertsCfg Alerts { get; set; } = new();
    public int[] Garden { get; set; } = new int[14];
    /// <summary>Screen, mouse and keyboard act as one: waves travel between them.</summary>
    public bool CrossRipples { get; set; } = false;
    public int CrossSpeed { get; set; } = 1600;
    public Dictionary<string, int> HighScores { get; set; } = new();
    public List<Profile> Profiles { get; set; } = new();

    public static AppConfig CreateDefault()
    {
        var c = new AppConfig();
        c.Profiles.Add(new Profile { Name = "Default", IsDefault = true });
        c.Profiles.Add(new Profile
        {
            Name = "Gaming",
            Apps = new() { },
            Screen = new ScreenFx { Style = "Explosion", ColorMode = "Fire", Particles = 28, DurationMs = 550, OnKeys = true },
        });
        return c;
    }
}

public static class ConfigStore
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InputFX");
    public static readonly string FilePath = Path.Combine(Dir, "config.json");
    static readonly JsonSerializerOptions Opt = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var c = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), Opt);
                if (c != null && c.Profiles.Count > 0) { Normalize(c); return c; }
            }
        }
        catch (Exception e) { Log.Write("config load failed: " + e.Message); TryBackupBroken(); }
        var d = AppConfig.CreateDefault();
        Save(d);
        return d;
    }

    static void TryBackupBroken()
    {
        try { if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true); } catch { }
    }

    static void Normalize(AppConfig c)
    {
        if (!c.Profiles.Any(p => p.IsDefault)) c.Profiles[0].IsDefault = true;
        if (!c.Profiles.Any(p => p.Name == c.ActiveProfile)) c.ActiveProfile = c.Profiles.First(p => p.IsDefault).Name;
    }

    static readonly object saveLock = new();
    public static void Save(AppConfig c)
    {
        lock (saveLock)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(c, Opt));
                File.Move(tmp, FilePath, true);
            }
            catch (Exception e) { Log.Write("config save failed: " + e.Message); }
        }
    }
}

public static class Log
{
    static readonly object l = new();
    public static void Write(string s)
    {
        try
        {
            lock (l)
            {
                Directory.CreateDirectory(ConfigStore.Dir);
                var p = Path.Combine(ConfigStore.Dir, "inputfx.log");
                if (File.Exists(p) && new FileInfo(p).Length > 512_000) File.Delete(p);
                File.AppendAllText(p, $"{DateTime.Now:HH:mm:ss.fff} {s}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
