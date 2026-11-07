using System.Diagnostics;
using HidSharp;

namespace InputFX;

/// <summary>
/// Live lighting for the Galahad mouse, using the sequence its own "Music-lighting" feature sends:
/// 1) switch the lighting block (memory 0x18-0x1F) to live mode by writing zeros WITHOUT the flash-commit frame (so nothing is saved),
/// 2) stream feature report 7 "07 13 ff e1 LEVEL 00 03 ff" (LEVEL 0x10-0x17) fast, no slow save needed,
/// 3) put the block back when finished.
/// </summary>
public sealed class MouseLive : IDisposable
{
    HidStream? stream;
    Thread? worker;
    volatile bool running;
    readonly AutoResetEvent wake = new(false);
    int pending;
    bool live;
    public Func<int> RestoreVariant = () => 0;
    public volatile bool AudioReactive;
    public volatile MouseHw Settings = new();
    public volatile int AudioSensitivity = 60;
    bool audioHeld;
    int pulses;
    int lastLevel = -1;
    int sinceSend;
    int audioSends;
    public string Status { get; private set; } = "off";

    static readonly byte[] Init = { 0x07, 0x13, 0xFF, 0x52, 0x0B, 0x00, 0x83, 0xEB };

    /// <summary>Turns the chosen pulse style and sliders into (level, wait ms after sending it) steps.</summary>
    public static List<(int level, int ms)> Shape(MouseHw m)
    {
        double sp = Math.Clamp(m.PulseSpeedPercent, 25, 400) / 100.0;
        int peak = Math.Clamp(m.PulseStrength, 1, 7);
        int hold = (int)(Math.Clamp(m.PulseHoldMs, 0, 600) / sp);
        int W(double ms) => Math.Max(5, (int)(ms / sp));
        var r = new List<(int, int)>();
        void Pop(int pk, double up, double down, int h) { for (int l = 0; l <= pk; l++) r.Add((l, W(up))); r.Add((pk, h)); for (int l = pk; l >= 0; l--) r.Add((l, W(down))); }
        switch (m.PulseStyle)
        {
            case "Flash": r.Add((peak, hold + W(40))); r.Add((0, W(30))); break;
            case "Glow": Pop(peak, 55, 85, hold + 80); break;
            case "Double": Pop(peak, 14, 28, hold); r.Add((0, W(90))); Pop(Math.Max(1, peak - 1), 14, 28, hold); break;
            case "Strobe": for (int i = 0; i < 3; i++) { r.Add((peak, W(45))); r.Add((0, W(55))); } break;
            case "Heartbeat": Pop(peak, 14, 26, hold); r.Add((0, W(110))); Pop(Math.Max(1, peak * 2 / 3), 14, 30, hold); break;
            default: Pop(peak, 15, 30, hold); break; // Pop
        }
        r.Add((0, 5));
        return r;
    }

    static readonly byte[] ZoneMode = { 0x07, 0x13, 0xFF, 0xA2, 0x0B, 0x00, 0x03, 0xFF };
    static byte[] ZoneMask(byte m) => new byte[] { 0x07, 0x14, 0x8F, m, 0x0B, 0x00, 0x03, 0xFF };

    static byte MaskOf(string? z) => z switch { "A" => 0xF0, "B" => 0x0F, "All" => 0xFF, "Both" => 0xFF, _ => 0x00 };

    /// <summary>One zone frame pair: the mode frame and the zone mask.</summary>
    static void Zones(HidStream s, byte mask) { s.SetFeature(ZoneMode); s.SetFeature(ZoneMask(mask)); }

    /// <summary>Plays one pulse. In Zones mode every zone flashes together; in Bar mode the level meter is used.</summary>
    static void PlayPulse(HidStream s, MouseHw st)
    {
        if (st.PulseMode == "Bar")
        {
            foreach (var (lv, ms) in Shape(st)) { s.SetFeature(Level(lv)); Thread.Sleep(ms); }
            return;
        }
        byte idle = MaskOf(st.IdleLight);
        foreach (var (mask, ms) in ZoneShape(st)) { Zones(s, mask); Thread.Sleep(ms); }
        Zones(s, idle);
    }

    public static readonly (string Tag, string Name)[] ZoneStyles =
    {
        ("Flash", "Flash"), ("Double", "Double flash"), ("Triple", "Triple flash"), ("Strobe", "Strobe"), ("Heartbeat", "Heartbeat"),
        ("Long", "Long glow"), ("Stutter", "Stutter (speeds up)"), ("Echo", "Echo (fading repeats)"), ("PingPong", "Ping-pong (A then B)"),
        ("FlipFlop", "Flip-flop (A/B alternate)"), ("Roll", "Roll (A, both, B)"), ("Sos", "SOS (morse)"), ("Train", "Pulse train"), ("Pop", "Pop (old sweep)"),
    };

    /// <summary>Zone masks over time for the chosen style: A = F0, B = 0F, both = FF. When the idle light already covers the flash, the flash becomes a blink off.</summary>
    public static List<(byte mask, int ms)> ZoneShape(MouseHw m)
    {
        double sp = Math.Clamp(m.PulseSpeedPercent, 25, 400) / 100.0;
        int W(double ms) => Math.Max(10, (int)(ms / sp));
        byte idle = MaskOf(m.IdleLight), flash = MaskOf(m.PulseZones);
        byte on = idle == flash && idle != 0 ? (byte)0x00 : flash;
        int hold = Math.Clamp(m.PulseHoldMs, 0, 600);
        byte A = 0xF0, B = 0x0F, AB = 0xFF;
        if (idle == AB) { A = 0x0F; B = 0xF0; AB = 0x00; } // idle on: the pattern is drawn by switching zones OFF
        var r = new List<(byte, int)>();
        void On(double ms) => r.Add((on, W(ms)));
        void Off(double ms) => r.Add((idle, W(ms)));
        switch (m.PulseStyle)
        {
            case "Double": On(60 + hold / 2); Off(70); On(60 + hold / 2); break;
            case "Triple": for (int i = 0; i < 3; i++) { On(55 + hold / 3); Off(65); } break;
            case "Strobe": for (int i = 0; i < 5; i++) { On(35); Off(35); } break;
            case "Heartbeat": On(60 + hold / 2); Off(80); On(110 + hold); break;
            case "Long": On(250 + hold * 2); break;
            case "Stutter": { double d = 130; for (int i = 0; i < 6; i++) { On(d * .55); Off(d * .45); d *= .72; } break; }
            case "Echo": { double d = 150 + hold; for (int i = 0; i < 4; i++) { On(d); Off(60); d *= .55; } break; }
            case "PingPong": for (int i = 0; i < 4; i++) { r.Add(((byte)(i % 2 == 0 ? A : B), W(80 + hold / 3))); } break;
            case "FlipFlop": for (int i = 0; i < 8; i++) r.Add(((byte)(i % 2 == 0 ? A : B), W(55))); break;
            case "Roll": r.Add((A, W(90))); r.Add((AB, W(90 + hold))); r.Add((B, W(90))); break;
            case "Sos": { foreach (int len in new[] { 1, 1, 1, 3, 3, 3, 1, 1, 1 }) { On(len == 1 ? 70 : 190); Off(70); } break; }
            case "Train": for (int i = 0; i < 6; i++) { On(40); Off(50); } break;
            case "Pop": for (int l = 1; l <= 6; l++) { On(18); } On(hold); Off(30); break;
            default: On(60 + hold); break; // Flash
        }
        r.Add((idle, 5));
        return r;
    }

    static byte[] Level(int l) => new byte[] { 0x07, 0x13, 0xFF, 0xE1, (byte)(0x10 + Math.Clamp(l, 0, 7)), 0x00, 0x03, 0xFF };

    static void Block(HidStream s, byte[] vals)
    {
        for (int n = 0; n < 8; n++) s.SetFeature(new byte[] { 0x07, 0x18, 0x03, (byte)n, 0x18, 0x00, vals[n], 0x07 });
        s.SetFeature(new byte[] { 0x07, 0x18, 0x00, 0x00, 0x18, 0x00, 0x00, 0x00 });
        for (int n = 0; n < 8; n++) s.SetFeature(new byte[] { 0x07, 0x18, 0x05, (byte)n, 0x18, 0x00, vals[n], 0x07 });
        s.SetFeature(new byte[] { 0x07, 0x18, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });
    }

    static void EnterLive(HidStream s) { Block(s, new byte[8]); s.SetFeature(Init); }

    static void ExitLive(HidStream s, int variant) => Block(s, HwLab.MouseBlock18(variant));

    public void Enable(bool on)
    {
        if (on && !running)
        {
            running = true;
            worker = new Thread(Loop) { IsBackground = true, Name = "mouse-live" };
            worker.Start();
        }
        else if (!on && running)
        {
            running = false; wake.Set(); worker?.Join(2000);
        }
    }

    public void Pulse() { Interlocked.Exchange(ref pending, 1); wake.Set(); }

    bool Open()
    {
        try
        {
            var dev = HwLab.FindMouse();
            if (dev == null) { Status = "mouse not found"; return false; }
            stream = dev.Open();
            EnterLive(stream); live = true;
            Thread.Sleep(2200); // the mouse app waits about 2 seconds after the switch before it streams
            for (int k = 0; k < 3; k++) { stream.SetFeature(Init); Thread.Sleep(280); stream.SetFeature(Level(0)); Thread.Sleep(220); }
            Status = "live";
            Log.Write("mouse live: entered live mode");
            return true;
        }
        catch (Exception e) { Status = "mouse: " + e.Message; Log.Write("mouse live open: " + e.Message); return false; }
    }

    void Loop()
    {
        try
        {
            while (running)
            {
                bool audio = AudioReactive;
                if (audio && !audioHeld) { AudioAnalyzer.Instance.Acquire(); audioHeld = true; }
                else if (!audio && audioHeld) { AudioAnalyzer.Instance.Release(); audioHeld = false; }
                bool zoneIdle = !audio && Settings.PulseMode != "Bar" && Settings.ClickPulse;
                wake.WaitOne(audio ? 45 : zoneIdle ? 350 : 2000);
                if (!running) break;
                bool clicked = Interlocked.Exchange(ref pending, 0) != 0;
                if (!clicked && audio)
                {
                    if (stream == null && !Open()) { Thread.Sleep(1000); continue; }
                    try
                    {
                        double gain = 0.5 + Math.Clamp(Settings.AudioSensitivity, 0, 100) / 100.0 * 1.5;
                        double v = Math.Clamp(AudioAnalyzer.Instance.CurrentLevel * gain, 0, 1);
                        int l = (int)Math.Round(Math.Pow(v, 1.6) * 7);
                        if (l != lastLevel || ++sinceSend >= 6) { stream!.SetFeature(Level(l)); lastLevel = l; sinceSend = 0; if (audioSends++ < 3) Log.Write("mouse live: audio level " + l); }
                    }
                    catch (Exception e) { Log.Write("mouse audio: " + e.Message); try { stream?.Dispose(); } catch { } stream = null; live = false; }
                    continue;
                }
                if (!clicked)
                {
                    if (zoneIdle && live && stream != null) { try { Zones(stream, MaskOf(Settings.IdleLight)); } catch { } }
                    continue;
                }
                if (stream == null && !Open()) { Thread.Sleep(1000); continue; }
                try
                {
                    PlayPulse(stream!, Settings);
                    pulses++; if (pulses <= 3) Log.Write("mouse live: pulse sent");
                }
                catch (Exception e) { Log.Write("mouse live: " + e.Message); try { stream?.Dispose(); } catch { } stream = null; live = false; }
            }
        }
        finally
        {
            if (audioHeld) { AudioAnalyzer.Instance.Release(); audioHeld = false; }
            try { if (stream != null && live) ExitLive(stream, RestoreVariant()); } catch (Exception e) { Log.Write("mouse live exit: " + e.Message); }
            try { stream?.Dispose(); } catch { }
            stream = null; live = false; Status = "off";
        }
    }

    /// <summary>For the Test button: enter live mode, pulse four times, put the mouse back.</summary>
    public static string Test(int variant, MouseHw? settings = null)
    {
        try
        {
            var dev = HwLab.FindMouse();
            if (dev == null) return "Mouse not found.";
            using var s = dev.Open();
            EnterLive(s);
            Thread.Sleep(2200);
            for (int k = 0; k < 3; k++) { s.SetFeature(Init); Thread.Sleep(280); s.SetFeature(Level(0)); Thread.Sleep(220); }
            var st = settings ?? new MouseHw();
            for (int rep = 0; rep < 4; rep++)
            {
                PlayPulse(s, st);
                Thread.Sleep(450);
            }
            ExitLive(s, variant);
            return "Sent 4 pulses, then put the mouse back.";
        }
        catch (Exception e) { return "Failed: " + e.Message; }
    }

    public void Dispose() => Enable(false);
}
