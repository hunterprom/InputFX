using System.Diagnostics;
using System.IO;
using System.Reflection;
using HidSharp;

namespace InputFX;

/// <summary>Key positions and LED indices of the EXCALIBUR (SINOWEALTH 258A:019D) keyboard, parsed from the vendor's KB.ini.</summary>
public sealed class KbLayout
{
    public sealed record KeyInfo(int Vk, double X, double Y, int Led, double W = 32, double H = 32);
    public List<KeyInfo> Keys { get; } = new();
    public Dictionary<int, KeyInfo> ByVk { get; } = new();
    public const int LedCount = 126;

    public static KbLayout Load()
    {
        var l = new KbLayout();
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("layout.csv");
        if (s == null) return l;
        using var r = new StreamReader(s);
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("vk")) continue;
            var f = line.Split(',');
            if (f.Length < 6) continue;
            try
            {
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                int vk = int.Parse(f[0], ci); double x = double.Parse(f[1], ci), y = double.Parse(f[2], ci), w = double.Parse(f[3], ci), h = double.Parse(f[4], ci); int led = int.Parse(f[5], ci);
                if (led < 0 || led >= LedCount) continue;
                var k = new KeyInfo(vk, x, y, led, w, h);
                l.Keys.Add(k);
                if (vk != 0xFA && vk != 0xFD) l.ByVk.TryAdd(vk, k);
            }
            catch { }
        }
        return l;
    }
}

public sealed class KbStatus
{
    public bool Connected;
    public double LastFrameMs;
    public long Frames;
    public string Message = "";
}

/// <summary>
/// Streams per-key colours to the keyboard (vendor command 0x08, feature report 9, 520 bytes).
/// The keyboard accepts about 7 frames per second, so bursts are rendered from wall-clock time at each send.
/// </summary>
public sealed class KeyboardLights : IDisposable
{
    public KbLayout Layout { get; } = KbLayout.Load();
    public KbStatus Status { get; } = new();

    HidStream? stream;
    Thread? worker;
    volatile bool running;
    readonly AutoResetEvent wake = new(false);
    readonly object gate = new();
    readonly List<(KbLayout.KeyInfo key, long t0, double r, double g, double b)> bursts = new();
    KeyboardHw cfg = new();
    long startTicks = Stopwatch.GetTimestamp();
    readonly Random rng = new();
    bool baseDirty = true;
    bool audioHeld;
    /// <summary>When set (mini games), every frame comes from here instead of the normal composer.</summary>
    volatile Func<(double r, double g, double b)[]>? external;

    public bool Active => running;

    public void Configure(KeyboardHw c)
    {
        lock (gate) { cfg = c; baseDirty = true; }
        bool wantAudio = c.Enabled && c.AudioEnabled;
        if (wantAudio && !audioHeld) { AudioAnalyzer.Instance.Acquire(); audioHeld = true; }
        else if (!wantAudio && audioHeld) { AudioAnalyzer.Instance.Release(); audioHeld = false; }
        if (c.Enabled || external != null) { Start(); wake.Set(); } else Stop();
    }

    /// <summary>Takes over the whole keyboard (used by the games). Pass null to give it back.</summary>
    public void SetExternal(Func<(double r, double g, double b)[]>? source)
    {
        external = source;
        if (source != null) { Start(); wake.Set(); }
        else { lock (gate) baseDirty = true; if (!cfg.Enabled) Stop(); else wake.Set(); }
    }

    public void Press(int vk)
    {
        if (!running || external != null) return;
        TypingMeter.Instance.Press();
        if (!Layout.ByVk.TryGetValue(vk, out var key)) return;
        Garden.Instance.Press(key);
        AddBurst(key);
    }

    /// <summary>A burst on the bottom row of keys at a horizontal fraction (0 = left edge, 1 = right edge). Used by cross-device ripples.</summary>
    public void BurstAtX(double frac)
    {
        if (!running || external != null) return;
        double minX = Layout.Keys.Min(k => k.X), maxX = Layout.Keys.Max(k => k.X), maxY = Layout.Keys.Max(k => k.Y);
        double tx = minX + Math.Clamp(frac, 0, 1) * (maxX - minX);
        var key = Layout.Keys.Where(k => k.Y > maxY - 40).OrderBy(k => Math.Abs(k.X - tx)).FirstOrDefault();
        if (key != null) AddBurst(key);
    }

    public void MousePress()
    {
        if (!running || !cfg.OnMouse || external != null) return;
        // mouse clicks flash from the centre of the main block
        var centre = Layout.Keys.OrderBy(k => Math.Abs(k.X - 300) + Math.Abs(k.Y - 150)).FirstOrDefault();
        if (centre != null) AddBurst(centre);
    }

    void AddBurst(KbLayout.KeyInfo key)
    {
        double r, g, b;
        lock (gate)
        {
            switch (cfg.BurstColorMode)
            {
                case "Random": { var c = Col.Hsv(rng.NextDouble() * 360, .9, 1); (r, g, b) = c; break; }
                case "Rainbow": { var c = Col.Hsv((Stopwatch.GetTimestamp() - startTicks) / (double)Stopwatch.Frequency * 120, .9, 1); (r, g, b) = c; break; }
                default:
                    if (cfg.RainbowBurst) { var c = Col.Hsv(rng.NextDouble() * 360, .9, 1); (r, g, b) = c; }
                    else { var c = Col.Parse(cfg.BurstColor, System.Windows.Media.Color.FromRgb(255, 106, 0)); (r, g, b) = (c.R / 255.0, c.G / 255.0, c.B / 255.0); }
                    break;
            }
            bursts.Add((key, Stopwatch.GetTimestamp(), r, g, b));
            if (bursts.Count > 24) bursts.RemoveAt(0);
        }
        wake.Set();
    }

    void Start()
    {
        if (running) return;
        running = true;
        worker = new Thread(Loop) { IsBackground = true, Name = "kb-lights" };
        worker.Start();
    }

    public void Stop()
    {
        if (audioHeld) { AudioAnalyzer.Instance.Release(); audioHeld = false; }
        if (!running) return;
        running = false;
        wake.Set();
        worker?.Join(1500);
        CloseDevice();
    }

    bool OpenDevice()
    {
        try
        {
            var dev = DeviceList.Local.GetHidDevices().FirstOrDefault(d => d.VendorID == 0x258A && (d.ProductID == 0x019D || d.ProductID == 0x0150) && d.GetMaxFeatureReportLength() == 520);
            if (dev == null) { Status.Connected = false; Status.Message = "EXCALIBUR keyboard not found"; return false; }
            stream = dev.Open();
            Status.Connected = true; Status.Message = "connected"; Log.Write("keyboard stream opened");
            return true;
        }
        catch (Exception e) { Status.Connected = false; Status.Message = e.Message; Log.Write("kb open: " + e.Message); return false; }
    }

    void CloseDevice() { try { stream?.Dispose(); } catch { } stream = null; Status.Connected = false; }

    void Loop()
    {
        var frame = new byte[520];
        int failures = 0;
        long lastSend = 0;
        bool lastWasBase = false;
        while (running)
        {
            KeyboardHw c; List<(KbLayout.KeyInfo key, long t0, double r, double g, double b)> live; bool dirty;
            var ext = external;
            AudioFrame? audio = null;
            lock (gate)
            {
                c = cfg; dirty = baseDirty;
                double durTicks = Math.Max(150, c.DurationMs) / Math.Clamp(c.BurstSpeedPercent, 25, 400) * 100.0 / 1000.0 * Stopwatch.Frequency;
                bursts.RemoveAll(x => Stopwatch.GetTimestamp() - x.t0 > durTicks);
                live = bursts.ToList();
            }
            bool useAudio = ext == null && c.AudioEnabled;
            if (useAudio)
            {
                audio = AudioAnalyzer.Instance.Snapshot();
                if (c.AudioStyle == "Bass" && audio.Beat) AddBurst(Layout.Keys[rng.Next(Layout.Keys.Count)]);
            }
            bool animatedBase = c.Base is "Rainbow" or "Breathing" or "Typing" or "Garden" || c.Base.StartsWith("Anim:") || AlertHub.Active.Count > 0;
            bool needSend = ext != null || live.Count > 0 || dirty || animatedBase || useAudio || (lastWasBase == false);
            if (!needSend)
            {
                wake.WaitOne(500);
                // heartbeat: keep the software lighting alive
                if (Stopwatch.GetTimestamp() - lastSend < 2 * Stopwatch.Frequency) continue;
            }

            if (stream == null && !OpenDevice()) { wake.WaitOne(2000); continue; }

            if (ext != null) RenderExternal(frame, ext);
            else Render(frame, c, live, audio);
            try
            {
                var sw = Stopwatch.StartNew();
                stream!.SetFeature(frame);
                Status.LastFrameMs = sw.Elapsed.TotalMilliseconds; Status.Frames++;
                lastSend = Stopwatch.GetTimestamp();
                lastWasBase = live.Count == 0;
                lock (gate) { baseDirty = false; }
                failures = 0;
            }
            catch (Exception e)
            {
                Log.Write("kb send: " + e.Message);
                CloseDevice();
                if (++failures > 5) { Status.Message = "keyboard unreachable"; wake.WaitOne(3000); }
            }
        }
    }

    static void Header(byte[] f) { Array.Clear(f); f[0] = 9; f[1] = 8; f[4] = 1; f[6] = 0x7A; f[7] = 0x01; }

    static void Pack(byte[] f, (double r, double g, double b)[] rgb, double bright)
    {
        for (int i = 0; i < KbLayout.LedCount; i++)
        {
            f[8 + i * 3] = (byte)(Math.Clamp(rgb[i].r * bright, 0, 1) * 255);
            f[9 + i * 3] = (byte)(Math.Clamp(rgb[i].g * bright, 0, 1) * 255);
            f[10 + i * 3] = (byte)(Math.Clamp(rgb[i].b * bright, 0, 1) * 255);
        }
    }

    void RenderExternal(byte[] f, Func<(double r, double g, double b)[]> src)
    {
        Header(f);
        Pack(f, src(), 1.0);
    }

    void Render(byte[] f, KeyboardHw c, List<(KbLayout.KeyInfo key, long t0, double r, double g, double b)> live, AudioFrame? audio)
    {
        Header(f);
        double now = (Stopwatch.GetTimestamp() - startTicks) / (double)Stopwatch.Frequency;
        long nowTicks = Stopwatch.GetTimestamp();
        var rgb = KbCompose.Compute(Layout, c, live.Select(b => (b.key, (nowTicks - b.t0) / (double)Stopwatch.Frequency, b.r, b.g, b.b)), now, audio);
        Pack(f, rgb, Math.Clamp(c.Brightness, 0, 100) / 100.0);
    }

    public void Dispose() { Stop(); }
}
