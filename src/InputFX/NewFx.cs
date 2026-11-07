using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace InputFX;

// ======================================================================= typing rhythm
/// <summary>Measures how fast and how steadily you type, for the "Typing rhythm" lighting.</summary>
public sealed class TypingMeter
{
    public static TypingMeter Instance { get; } = new();
    readonly Queue<double> stamps = new();
    double lastPress;
    public int Streak { get; private set; }
    public double Energy { get; private set; }
    public double Steady { get; private set; }
    public int MilestoneFlash;           // set to a streak count when a milestone is hit
    public double MilestoneAt;

    static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public void Press()
    {
        double t = Now;
        if (t - lastPress < 0.45) Streak++; else Streak = 1;
        lastPress = t;
        stamps.Enqueue(t);
        while (stamps.Count > 24 || (stamps.Count > 0 && t - stamps.Peek() > 6)) stamps.Dequeue();
        if (Streak is 10 or 25 or 50 or 100 or 200) { MilestoneFlash = Streak; MilestoneAt = t; }
    }

    /// <summary>Call once per rendered frame.</summary>
    public void Update()
    {
        double t = Now;
        var arr = stamps.ToArray();
        double rate = 0;
        if (arr.Length >= 2 && t - arr[^1] < 1.5) rate = (arr.Length - 1) / Math.Max(0.3, arr[^1] - arr[0]);
        double target = Math.Clamp(rate / 7.0, 0, 1);
        double idle = Math.Clamp((t - lastPress) / 2.5, 0, 1);
        target *= 1 - idle;
        Energy += (target - Energy) * (target > Energy ? 0.5 : 0.18);
        if (arr.Length >= 5)
        {
            var d = new List<double>(); for (int i = 1; i < arr.Length; i++) d.Add(arr[i] - arr[i - 1]);
            double mean = d.Average(), sd = Math.Sqrt(d.Average(x => (x - mean) * (x - mean)));
            Steady = Math.Clamp(1 - sd / Math.Max(0.05, mean), 0, 1);
        }
        else Steady *= 0.9;
        if (t - lastPress > 1.2) Streak = 0;
    }

    public (double r, double g, double b) Color(KbLayout.KeyInfo k, double now)
    {
        double e = Energy;
        double wave = 0.6 + 0.4 * Math.Sin(k.X * 0.025 - now * (2 + 7 * e));
        double v = (0.07 + 0.93 * e) * wave;
        return Col.Hsv(225 - 225 * e - 25 * Steady, 0.55 + 0.4 * e, Math.Clamp(v, 0, 1));
    }
}

// ======================================================================= garden
/// <summary>A tiny garden on the 14 x 5 key block that grows while you type, and keeps growing over days.</summary>
public sealed class Garden
{
    public static Garden Instance { get; } = new();
    public int[] Growth = new int[14];
    readonly Random rng = new();
    readonly List<(double x, double y, double born, double phase)> flies = new();

    static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public void Load(int[]? saved) { if (saved is { Length: 14 }) Growth = saved; }

    public void Press(KbLayout.KeyInfo k)
    {
        int col = Math.Clamp(k.Led / 6, 0, 13);
        int pick = Math.Clamp(col + rng.Next(-1, 2), 0, 13);
        Growth[pick] = Math.Min(Growth[pick] + 1, 5 * 24);
        flies.Add((col, 3.0, Now, rng.NextDouble() * 6.28));
        if (flies.Count > 18) flies.RemoveAt(0);
    }

    public (double r, double g, double b)[] Render(double now)
    {
        var res = new (double r, double g, double b)[KbLayout.LedCount];
        double t = Now;
        for (int x = 0; x < 14; x++)
        {
            for (int y = 0; y < 5; y++) res[y + 6 * x] = (0.01, 0.015, 0.05 + 0.03 * (4 - y) / 4.0); // night sky
            res[4 + 6 * x] = (0.28, 0.16, 0.06); // soil
            int h = Math.Min(Growth[x] / 24 + (Growth[x] > 0 ? 1 : 0), 4);
            for (int i = 0; i < h; i++) res[3 - i + 6 * x] = (0.05, 0.55 - i * 0.05, 0.1);
            if (Growth[x] >= 4 * 24) // in bloom
            {
                var c = Col.Hsv((x * 47 + t * 20) % 360, .7, 1);
                res[3 - 3 + 6 * x] = c;
            }
        }
        foreach (var f in flies.ToList())
        {
            double age = t - f.born;
            if (age > 4) { flies.Remove(f); continue; }
            double fx = f.x + Math.Sin(age * 2.3 + f.phase) * 1.6, fy = f.y - age * 0.6 + Math.Sin(age * 3 + f.phase) * 0.5;
            int cx = (int)Math.Round(fx), cy = (int)Math.Round(fy);
            if (cx < 0 || cx > 13 || cy < 0 || cy > 3) continue;
            double v = Math.Clamp(1 - age / 4, 0, 1) * (0.6 + 0.4 * Math.Sin(age * 14));
            var cur = res[cy + 6 * cx];
            res[cy + 6 * cx] = (Math.Min(1, cur.r + 0.9 * v), Math.Min(1, cur.g + 1.0 * v), Math.Min(1, cur.b + 0.2 * v));
        }
        return res;
    }
}

// ======================================================================= alerts
public sealed class AlertRule { public string App { get; set; } = ""; public string Color { get; set; } = "#FFFFFF"; }

public sealed class AlertsCfg
{
    public bool Enabled { get; set; } = false;
    public bool TaskbarFlash { get; set; } = true;
    public bool Battery { get; set; } = true;
    public bool Cpu { get; set; } = true;
    public List<AlertRule> Apps { get; set; } = new()
    {
        new() { App = "discord", Color = "#5865F2" }, new() { App = "outlook", Color = "#2F8CFF" }, new() { App = "teams", Color = "#6264A7" },
        new() { App = "slack", Color = "#E01E5A" }, new() { App = "telegram", Color = "#2AABEE" }, new() { App = "whatsapp", Color = "#25D366" },
        new() { App = "thunderbird", Color = "#FF8A00" }, new() { App = "msedge", Color = "#FFC14D" }, new() { App = "chrome", Color = "#FFC14D" },
    };
}

/// <summary>Turns "something wants your attention" into light: taskbar button flashes, low battery, hot CPU, your own timer.</summary>
public sealed class AlertHub : NativeWindow, IDisposable
{
    [DllImport("user32.dll")] static extern bool RegisterShellHookWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool DeregisterShellHookWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string s);

    public static readonly List<(double start, double r, double g, double b)> Active = new();
    static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    readonly uint shellMsg;
    public Func<AlertsCfg> Config = () => new AlertsCfg();
    public event Action<string, string>? Raised;   // (title, colour)
    readonly System.Threading.Timer poll;
    DateTime cpuHighSince = DateTime.MinValue, lastBattery = DateTime.MinValue, lastFlash = DateTime.MinValue;
    PerformanceCounter? cpu;

    public AlertHub()
    {
        CreateHandle(new CreateParams());
        shellMsg = RegisterWindowMessage("SHELLHOOK");
        RegisterShellHookWindow(Handle);
        try { cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total"); cpu.NextValue(); } catch { cpu = null; }
        poll = new System.Threading.Timer(_ => Poll(), null, 5000, 5000);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == shellMsg && (int)m.WParam == 0x8006) // HSHELL_FLASH
        {
            try
            {
                var cfg = Config();
                if (cfg.Enabled && cfg.TaskbarFlash && (DateTime.UtcNow - lastFlash).TotalSeconds > 2.5)
                {
                    var name = Native.ProcessNameOf(m.LParam) ?? "app";
                    if (name != "inputfx")
                    {
                        lastFlash = DateTime.UtcNow;
                        var rule = cfg.Apps.FirstOrDefault(a => name.Contains(a.App, StringComparison.OrdinalIgnoreCase));
                        Trigger($"{name} needs you", rule?.Color ?? "#FFFFFF");
                    }
                }
            }
            catch (Exception e) { Log.Write("alert: " + e.Message); }
        }
        base.WndProc(ref m);
    }

    void Poll()
    {
        try
        {
            var cfg = Config(); if (!cfg.Enabled) return;
            if (cfg.Battery)
            {
                var ps = SystemInformation.PowerStatus;
                if (ps.PowerLineStatus == PowerLineStatus.Offline && ps.BatteryLifePercent < 0.15 && (DateTime.UtcNow - lastBattery).TotalMinutes > 5) { lastBattery = DateTime.UtcNow; Trigger("Battery low", "#FF3030"); }
            }
            if (cfg.Cpu && cpu != null)
            {
                float v = cpu.NextValue();
                if (v > 92) { if (cpuHighSince == DateTime.MinValue) cpuHighSince = DateTime.UtcNow; else if ((DateTime.UtcNow - cpuHighSince).TotalSeconds > 20) { cpuHighSince = DateTime.UtcNow.AddMinutes(2); Trigger("CPU is maxed out", "#FF8A00"); } }
                else cpuHighSince = DateTime.MinValue;
            }
        }
        catch { }
    }

    public void Trigger(string title, string hexColor)
    {
        var c = Col.Parse(hexColor, System.Windows.Media.Color.FromRgb(255, 255, 255));
        lock (Active) { Active.Add((Now, c.R / 255.0, c.G / 255.0, c.B / 255.0)); if (Active.Count > 4) Active.RemoveAt(0); }
        Raised?.Invoke(title, hexColor);
    }

    public static void Apply((double r, double g, double b)[] rgb, KbLayout layout, double nowStamp)
    {
        lock (Active)
        {
            Active.RemoveAll(a => Now - a.start > 2.6);
            foreach (var a in Active)
            {
                double age = Now - a.start;
                double pulse = (0.5 + 0.5 * Math.Sin(age * 9)) * Math.Clamp(1 - age / 2.6, 0, 1);
                foreach (var k in layout.Keys)
                {
                    var c = rgb[k.Led];
                    rgb[k.Led] = (c.r * (1 - pulse * .8) + a.r * pulse, c.g * (1 - pulse * .8) + a.g * pulse, c.b * (1 - pulse * .8) + a.b * pulse);
                }
            }
        }
    }

    public void Dispose() { poll.Dispose(); try { DeregisterShellHookWindow(Handle); DestroyHandle(); } catch { } }
}
