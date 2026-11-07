using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace InputFX;

public static class KbModes
{
    public sealed record Mode(int Id, string Name, string Note, bool Reactive = false)
    {
        public string Title => (Note.Length > 0 ? $"{Name}   ({Note})" : Name);
        public override string ToString() => Title;
    }

    // Exact names and numbers, captured from the vendor app (click an effect, press Save, read the byte the keyboard receives).
    public static readonly Mode[] All =
    {
        new(1, "Fixed on", "one solid color"),
        new(2, "Respire", "breathing"),
        new(3, "Rainbow", ""),
        new(4, "Flash away", "key flashes when pressed", true),
        new(5, "Raindrops", ""),
        new(7, "Ripples shining", "ripples from pressed key", true),
        new(8, "Stars twinkle", ""),
        new(9, "Shadow disappear", ""),
        new(10, "Retro snake", ""),
        new(11, "Neon stream", ""),
        new(12, "Reaction", "pressed key lights up", true),
        new(13, "Sine wave", ""),
        new(15, "Rotating windmill", ""),
        new(16, "Colorful waterfall", ""),
        new(17, "Blossoming", ""),
        new(21, "Self-define", "your own per-key colors"),
        new(0, "OFF", "lights off"),
    };

    public static int IndexOf(int id) { for (int i = 0; i < All.Length; i++) if (All[i].Id == id) return i; return 0; }
}

/// <summary>Draws the keyboard and animates an approximation of a lighting mode, or of the custom stream burst.</summary>
public sealed class KbPreview : FrameworkElement
{
    readonly KbLayout layout = KbLayout.Load();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly long t0 = Stopwatch.GetTimestamp();
    readonly Random rng = new(3);
    readonly List<(KbLayout.KeyInfo key, double at, bool real)> presses = new();
    double lastDemo;

    public int HwMode { get; set; } = 7;
    public bool UseStream { get; set; }
    /// <summary>Playback speed of the recorded animation (1 = as recorded).</summary>
    public double HwSpeedFactor { get; set; } = 1;
    public KeyboardHw Stream { get; set; } = new();
    public Color Tint { get; set; } = Color.FromRgb(60, 140, 255);

    public KbPreview()
    {
        Height = 250; MinWidth = 520;
        timer.Tick += (_, _) => InvalidateVisual();
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) timer.Start(); else timer.Stop(); };
    }

    double Now => (Stopwatch.GetTimestamp() - t0) / (double)Stopwatch.Frequency;

    public void Press(int vk)
    {
        if (!IsVisible) return;
        if (!layout.ByVk.TryGetValue(vk, out var k)) return;
        presses.Add((k, Now, true));
        if (presses.Count > 30) presses.RemoveAt(0);
    }

    protected override Size MeasureOverride(Size a) => new(double.IsInfinity(a.Width) ? 600 : a.Width, Height);

    protected override void OnRender(DrawingContext dc)
    {
        double now = Now;
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(18, 20, 24)), null, new Rect(0, 0, ActualWidth, ActualHeight), 8, 8);
        presses.RemoveAll(p => now - p.at > 3);

        // automatic demo presses so Reaction / bursts are visible without typing
        bool wantsPresses = UseStream || HwMode is 4 or 7 or 12;
        if (wantsPresses && now - lastDemo > 1.3 && !presses.Any(p => p.real && now - p.at < 4))
        {
            lastDemo = now;
            var k = layout.Keys[rng.Next(layout.Keys.Count)];
            presses.Add((k, now, false));
        }

        const double lw = 740, lh = 262; // layout extents with margin
        double s = Math.Min((ActualWidth - 20) / lw, (ActualHeight - 12) / lh);
        double ox = (ActualWidth - lw * s) / 2 - 20 * s, oy = (ActualHeight - lh * s) / 2 + 2 * s;

        (double r, double g, double b)[] col;
        if (UseStream)
        {
            var live = presses.Select(p => (p.key, now - p.at, Burst(p.key)[0], Burst(p.key)[1], Burst(p.key)[2]));
            col = KbCompose.Compute(layout, Stream, live, now, Stream.AudioEnabled ? AudioAnalyzer.Instance.Snapshot() : null);
            double br = Math.Clamp(Stream.Brightness, 0, 100) / 100.0;
            for (int i = 0; i < col.Length; i++) col[i] = (col[i].r * br, col[i].g * br, col[i].b * br);
        }
        else col = HwColors(now);

        foreach (var k in layout.Keys)
        {
            var c = col[k.Led];
            double glow = Math.Max(c.r, Math.Max(c.g, c.b));
            var fill = new SolidColorBrush(Color.FromRgb(B(0.10 + c.r * .9), B(0.10 + c.g * .9), B(0.11 + c.b * .9)));
            var rect = new Rect(ox + (k.X - k.W / 2) * s + 1, oy + (k.Y - k.H / 2) * s + 1, Math.Max(3, k.W * s - 2), Math.Max(3, k.H * s - 2));
            if (glow > .35) dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb((byte)(40 * glow), B(c.r), B(c.g), B(c.b))), null, Rect.Inflate(rect, 2.5, 2.5), 5, 5);
            dc.DrawRoundedRectangle(fill, null, rect, 3, 3);
        }
    }

    double[] Burst(KbLayout.KeyInfo _)
    {
        if (Stream.RainbowBurst) { var h = Col.Hsv(Now * 90, .9, 1); return new[] { h.r, h.g, h.b }; }
        var c = Col.Parse(Stream.BurstColor, Color.FromRgb(255, 106, 0));
        return new[] { c.R / 255.0, c.G / 255.0, c.B / 255.0 };
    }

    static byte B(double v) => (byte)(Math.Clamp(v, 0, 1) * 255);

    (double r, double g, double b)[] HwColors(double t)
    {
        var res = new (double r, double g, double b)[KbLayout.LedCount];
        if (KbAnims.Fill(HwMode, t * HwSpeedFactor, layout, res)) return res;
        // press-reactive modes are simulated from real key presses
        foreach (var k in layout.Keys)
        {
            double sum = 0, hue = 0;
            foreach (var p in presses)
            {
                double age = t - p.at; if (age < 0) continue;
                double d = Math.Sqrt((k.X - p.key.X) * (k.X - p.key.X) + (k.Y - p.key.Y) * (k.Y - p.key.Y));
                double v = 0;
                switch (HwMode)
                {
                    case 12: if (d < 8) v = Math.Max(0, 1 - age / 1.2); break;                       // Reaction: the pressed key only
                    case 4: if (d < 8) v = Math.Max(0, 1 - age / 0.45); break;                       // Flash away: quick flash
                    case 7: v = Math.Exp(-Math.Pow((d - age * 190) / 26, 2)) * Math.Max(0, 1 - age / 1.3); break; // Ripples
                }
                if (v > sum) { sum = v; hue = (p.at * 70) % 360; }
            }
            if (sum > 0.01) { var c = Col.Hsv(hue, .85, 1); res[k.Led] = (c.r * sum, c.g * sum, c.b * sum); }
        }
        return res;
    }

    static double Hash(int n) { unchecked { uint h = (uint)n * 2654435761u; h ^= h >> 15; h *= 2246822519u; h ^= h >> 13; return (h & 0xFFFF) / 65535.0; } }
}

