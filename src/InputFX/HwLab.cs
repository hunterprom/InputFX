using System.IO;
using System.Reflection;
using HidSharp;

namespace InputFX;

/// <summary>
/// Writes the keyboard/mouse's own stored lighting mode, by replaying frames captured from the vendor apps
/// with a single value changed. These are flash writes inside the device, so they are only done on request, never per key press.
/// </summary>
public static class HwLab
{
    static byte[] Res(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var s = asm.GetManifestResourceStream(name) ?? throw new FileNotFoundException(name);
        using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray();
    }

    // ---------------- keyboard ----------------
    public static readonly byte[] BrightLevels = { 1, 5, 10, 15, 20 };

    public static string ApplyKeyboardMode(int effectId, KeyboardLights lights, int speed = -1, int brightness = -1)
    {
        if (effectId is < 0 or > 21) return "Mode must be 0-21.";
        // stop our own streaming first so frames don't interleave with the config write
        lights.Stop();
        try
        {
            var dev = DeviceList.Local.GetHidDevices().FirstOrDefault(d => d.VendorID == 0x258A && d.ProductID is 0x019D or 0x0150 && d.GetMaxFeatureReportLength() == 520);
            if (dev == null) return "Keyboard not found.";
            var f84 = Res("kb_cfg84.bin"); var f04 = Res("kb_cfg04.bin");
            f04[18] = (byte)effectId;
            // per-effect slot: byte 64+2*id = brightness level (1/5/10/15/20), byte 65+2*id = speed (high nibble 0-4) | colour flags (low nibble)
            if (effectId > 0 && effectId <= 21)
            {
                if (brightness is >= 0 and <= 4) f04[64 + 2 * effectId] = BrightLevels[brightness];
                if (speed is >= 0 and <= 4) f04[65 + 2 * effectId] = (byte)((speed << 4) | (f04[65 + 2 * effectId] & 0x0F));
            }
            using var s = dev.Open();
            s.SetFeature(f84);
            s.SetFeature(f04);
            return $"Keyboard hardware mode {effectId} written.";
        }
        catch (Exception e) { Log.Write("kb mode: " + e.Message); return "Failed: " + e.Message; }
    }

    public static string RestoreKeyboard(KeyboardLights lights) => ApplyKeyboardMode(0x0B, lights);

    // ---------------- mouse ----------------
    public static HidDevice? FindMouse() =>
        DeviceList.Local.GetHidDevices().FirstOrDefault(d => d.VendorID == 0x30FA && d.ProductID == 0x1440 && d.GetMaxFeatureReportLength() == 8);

    /// <summary>Replays the full captured configuration with the given (address,value) patches applied.</summary>
    public static async Task<string> ApplyMouse(IReadOnlyDictionary<int, byte> patches, IProgress<int>? progress = null)
    {
        return await Task.Run(() =>
        {
            try
            {
                var dev = FindMouse();
                if (dev == null) return "Mouse not found.";
                var all = Res("mouse_replay.bin");
                using var s = dev.Open();
                int total = all.Length / 8;
                for (int i = 0; i < total; i++)
                {
                    var f = new byte[8];
                    Array.Copy(all, i * 8, f, 0, 8);
                    if (f[2] == 3)
                    {
                        int addr = (f[4] | f[5] << 8) + f[3];
                        if (patches.TryGetValue(addr, out var v)) f[6] = v;
                    }
                    s.SetFeature(f);
                    if (i % 40 == 0) progress?.Report(i * 100 / total);
                }
                progress?.Report(100);
                return "Mouse configuration written.";
            }
            catch (Exception e) { Log.Write("mouse write: " + e.Message); return "Failed: " + e.Message; }
        });
    }

    // Exact modes of the Gaming Mouse app's Backlight list. Captured by applying each one and diffing the mouse's memory:
    // only byte 0x1C (mode/effect) and bit 7 of byte 0x33 change.
    public static readonly (string Name, byte Reg1C, byte Reg33, string Note)[] MouseVariants =
    {
        ("Button Response", 0x9C, 0x3F, "reacts to clicks"),
        ("Ripple", 0xA4, 0xBF, ""),
        ("Flicker", 0x84, 0xBF, ""),
        ("Star Trek", 0x94, 0xBF, ""),
        ("Enraptured", 0xB4, 0xBF, ""),
        ("DPI Breathing Effect", 0x84, 0x3F, ""),
        ("Cycle Breathing Effect", 0x94, 0x3F, ""),
        ("Light On", 0xA4, 0x3F, "solid"),
        ("Flowing Water Effect", 0xB4, 0x3F, ""),
        ("Mono Water Effect", 0xC4, 0x3F, ""),
        ("Comet Streak", 0xD4, 0x3F, ""),
        ("Neon", 0xE4, 0x3F, ""),
        ("Ambilight", 0xF4, 0x3F, ""),
        ("LED Off", 0x97, 0x3F, "lights off"),
    };

    /// <summary>The 8 bytes of mouse memory at 0x18-0x1F (the lighting block) for a variant: from the captured image with that variant's mode byte.</summary>
    public static byte[] MouseBlock18(int variantIndex)
    {
        var all = Res("mouse_replay.bin");
        var blk = new byte[8];
        for (int i = 0; i + 8 <= all.Length; i += 8)
            if (all[i + 2] == 3)
            {
                int addr = (all[i + 4] | all[i + 5] << 8) + all[i + 3];
                if (addr >= 0x18 && addr <= 0x1F) blk[addr - 0x18] = all[i + 6];
            }
        if (variantIndex >= 0 && variantIndex < MouseVariants.Length) blk[4] = MouseVariants[variantIndex].Reg1C;
        return blk;
    }

    public static Task<string> ApplyMouseVariant(int index, IProgress<int>? progress = null)
    {
        var v = MouseVariants[index];
        return ApplyMouse(new Dictionary<int, byte> { [0x1C] = v.Reg1C, [0x33] = v.Reg33 }, progress);
    }
}
