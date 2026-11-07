using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using static InputFX.Native;

namespace InputFX;

// ======================================================================= helpers
static class Col
{
    public static Color Parse(string? s, Color fallback)
    {
        try { if (!string.IsNullOrWhiteSpace(s)) return (Color)ColorConverter.ConvertFromString(s); } catch { }
        return fallback;
    }

    public static (double r, double g, double b) Hsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360 / 60; int i = (int)h; double f = h - i;
        double p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        return i switch { 0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q) };
    }

    public static double Lerp(double a, double b, double t) => a + (b - a) * t;
}

static class BrushCache
{
    static readonly Dictionary<uint, SolidColorBrush> brushes = new();
    static readonly Dictionary<(uint, int), Pen> pens = new();

    public static SolidColorBrush Get(double r, double g, double b, double a)
    {
        byte A = (byte)(Math.Clamp(a, 0, 1) * 31 + 0.5) , R = (byte)(Math.Clamp(r, 0, 1) * 31 + 0.5), G = (byte)(Math.Clamp(g, 0, 1) * 31 + 0.5), B = (byte)(Math.Clamp(b, 0, 1) * 31 + 0.5);
        uint key = (uint)(A << 15 | R << 10 | G << 5 | B);
        if (brushes.TryGetValue(key, out var br)) return br;
        if (brushes.Count > 6000) brushes.Clear();
        br = new SolidColorBrush(Color.FromArgb((byte)(A * 255 / 31), (byte)(R * 255 / 31), (byte)(G * 255 / 31), (byte)(B * 255 / 31)));
        br.Freeze();
        brushes[key] = br;
        return br;
    }

    public static Pen GetPen(double r, double g, double b, double a, double width)
    {
        var br = Get(r, g, b, a);
        uint key = (uint)br.Color.A << 24 | (uint)br.Color.R << 16 | (uint)br.Color.G << 8 | br.Color.B;
        int w = (int)(width * 2);
        if (pens.TryGetValue((key, w), out var p)) return p;
        if (pens.Count > 3000) pens.Clear();
        p = new Pen(br, w / 2.0) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        pens[(key, w)] = p;
        return p;
    }
}

// ======================================================================= particle model
struct Particle
{
    public double X, Y, Vx, Vy, Life, MaxLife, Size, Rot, Vrot, Drag, Gy;
    public double R, G, B;
    public byte Kind; // 0 fire blob, 1 streak, 2 confetti rect, 3 pixel square, 4 star
    public bool Fire;
    public bool Alive => Life > 0;
}

sealed class BurstState
{
    public Particle[] P = Array.Empty<Particle>();
    public int Count;
    public double Age, Duration, ScalePx;
    public double ShockMax;
    public bool Shock, Flash, Pixels;
    public string Style = "Explosion";
    public double FlashR = 1, FlashG = .9, FlashB = .6;
    public double Hue;
}

// ======================================================================= overlay window
sealed class OverlayWindow : Window
{
    readonly FxVisual visual = new();
    IntPtr hwnd;
    int cx, cy, sizePx;
    public bool Active { get; private set; }
    public DateTime StartedAt { get; private set; }
    BurstState? burst;

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;
        Left = -32000; Top = -32000; Width = 10; Height = 10;
        Content = visual;
        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            ex |= WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
        };
    }

    public void EnsureCreated()
    {
        if (hwnd == IntPtr.Zero) { Show(); Hide(); }
    }

    public void Begin(BurstState b, int centerX, int centerY, int sidePx)
    {
        EnsureCreated();
        burst = b; cx = centerX; cy = centerY; sizePx = sidePx;
        visual.Burst = b;
        Active = true; StartedAt = DateTime.UtcNow;
        if (!IsVisible) Show();
        Place();
        visual.InvalidateVisual();
    }

    void Place() => SetWindowPos(hwnd, HWND_TOPMOST, cx - sizePx / 2, cy - sizePx / 2, sizePx, sizePx, SWP_NOACTIVATE | SWP_SHOWWINDOW);

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (Active && hwnd != IntPtr.Zero) Place();
    }

    public void Tick(double dt)
    {
        if (!Active || burst == null) return;
        var b = burst;
        b.Age += dt;
        bool any = false;
        for (int i = 0; i < b.Count; i++)
        {
            ref var p = ref b.P[i];
            if (!p.Alive) continue;
            p.Life -= dt;
            if (p.Life <= 0) continue;
            any = true;
            double drag = Math.Exp(-p.Drag * dt);
            p.Vx *= drag; p.Vy = p.Vy * drag + p.Gy * dt;
            p.X += p.Vx * dt; p.Y += p.Vy * dt;
            p.Rot += p.Vrot * dt;
        }
        visual.Dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (!any && b.Age > b.Duration) { Finish(); return; }
        visual.InvalidateVisual();
    }

    public void Finish()
    {
        Active = false; burst = null; visual.Burst = null;
        if (IsVisible) Hide();
    }
}

sealed class FxVisual : FrameworkElement
{
    public BurstState? Burst;
    public double Dpi = 1;

    protected override void OnRender(DrawingContext dc)
    {
        var b = Burst;
        if (b == null) return;
        double s = 1.0 / Math.Max(0.5, Dpi); // px -> dip
        double ox = ActualWidth / 2, oy = ActualHeight / 2;
        double age = b.Age;

        // flash core
        if (b.Flash && age < 0.22)
        {
            double t = age / 0.22, a = (1 - t) * (1 - t), r = (30 + 120 * Math.Sqrt(t)) * b.ScalePx * s;
            var br = new RadialGradientBrush();
            br.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * a), 255, 255, 240), 0));
            br.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(200 * a), (byte)(b.FlashR * 255), (byte)(b.FlashG * 255), (byte)(b.FlashB * 255)), 0.35));
            br.GradientStops.Add(new GradientStop(Color.FromArgb(0, (byte)(b.FlashR * 255), (byte)(b.FlashG * 255), (byte)(b.FlashB * 255)), 1));
            dc.DrawEllipse(br, null, new Point(ox, oy), r, r);
        }

        // shockwave rings
        if (b.Shock)
        {
            int rings = b.Style == "Shockwave" ? 3 : 1;
            for (int k = 0; k < rings; k++)
            {
                double t = (age - k * 0.09) / (b.Duration * 0.75);
                if (t <= 0 || t >= 1) continue;
                double e = 1 - Math.Pow(1 - t, 3);
                double r = e * b.ShockMax * s, a = Math.Pow(1 - t, 1.6) * 0.85, w = Math.Max(1, (7 * (1 - t) + 1) * b.ScalePx) * s;
                var pen = BrushCache.GetPen(b.FlashR, b.FlashG * .95 + .05, b.FlashB * .9 + .1, a, w);
                dc.DrawEllipse(null, pen, new Point(ox, oy), r, r);
            }
        }

        for (int i = 0; i < b.Count; i++)
        {
            ref var p = ref b.P[i];
            if (!p.Alive) continue;
            double t = Math.Clamp(p.Life / p.MaxLife, 0, 1); // 1 fresh -> 0 dead
            double r, g, bl, a;
            if (p.Fire) { FireColor(t, out r, out g, out bl); a = Math.Min(1, t * 1.7); }
            else
            {
                double w = Math.Clamp((t - 0.72) / 0.28, 0, 1); // white-hot at birth
                r = Col.Lerp(p.R, 1, w * .8); g = Col.Lerp(p.G, 1, w * .8); bl = Col.Lerp(p.B, 1, w * .8);
                double dim = 0.35 + 0.65 * t;
                r *= dim; g *= dim; bl *= dim;
                a = Math.Min(1, t * 1.8);
            }
            double x = ox + p.X * s, y = oy + p.Y * s;
            switch (p.Kind)
            {
                case 1: // streak
                    {
                        double len = 0.035;
                        var pen = BrushCache.GetPen(r, g, bl, a, Math.Max(1, p.Size * s * 0.35));
                        dc.DrawLine(pen, new Point(x, y), new Point(x - p.Vx * len * s, y - p.Vy * len * s));
                        break;
                    }
                case 2: // confetti
                    {
                        double sz = p.Size * s;
                        double flip = Math.Abs(Math.Cos(p.Rot * 1.7));
                        dc.PushTransform(new TranslateTransform(x, y));
                        dc.PushTransform(new RotateTransform(p.Rot * 57.3));
                        dc.DrawRectangle(BrushCache.Get(r, g, bl, a), null, new Rect(-sz, -sz * .5 * flip, sz * 2, sz * flip + 0.5));
                        dc.Pop(); dc.Pop();
                        break;
                    }
                case 3: // pixel
                    {
                        double sz = Math.Max(2, Math.Round(p.Size * s / 3) * 3);
                        dc.DrawRectangle(BrushCache.Get(r, g, bl, a), null, new Rect(Math.Round(x / 3) * 3, Math.Round(y / 3) * 3, sz, sz));
                        break;
                    }
                case 4: // glowing star dot
                    {
                        double sz = p.Size * s * (0.6 + 0.4 * t);
                        dc.DrawEllipse(BrushCache.Get(r, g, bl, a * .22), null, new Point(x, y), sz * 2.4, sz * 2.4);
                        dc.DrawEllipse(BrushCache.Get(Math.Min(1, r + .4), Math.Min(1, g + .4), Math.Min(1, bl + .4), a), null, new Point(x, y), sz * .7, sz * .7);
                        break;
                    }
                default: // fire blob with glow
                    {
                        double sz = p.Size * s * (0.35 + 0.65 * t);
                        dc.DrawEllipse(BrushCache.Get(r, g, bl, a * .20), null, new Point(x, y), sz * 1.9, sz * 1.9);
                        dc.DrawEllipse(BrushCache.Get(r, g, bl, a), null, new Point(x, y), sz, sz);
                        break;
                    }
            }
        }
    }

    static void FireColor(double t, out double r, out double g, out double b)
    {
        // t: 1 = fresh. white -> yellow -> orange -> red -> smoke
        if (t > .75) { double u = (t - .75) / .25; r = 1; g = Col.Lerp(.82, .98, u); b = Col.Lerp(.3, .85, u); }
        else if (t > .5) { double u = (t - .5) / .25; r = 1; g = Col.Lerp(.47, .82, u); b = Col.Lerp(.08, .3, u); }
        else if (t > .25) { double u = (t - .25) / .25; r = Col.Lerp(.78, 1, u); g = Col.Lerp(.16, .47, u); b = Col.Lerp(.04, .08, u); }
        else { double u = t / .25; r = Col.Lerp(.25, .78, u); g = Col.Lerp(.12, .16, u); b = Col.Lerp(.12, .04, u); }
    }
}

// ======================================================================= pool + controller
public sealed class FxController
{
    readonly List<OverlayWindow> windows = new();
    readonly DispatcherTimer timer;
    readonly Random rng = new();
    readonly Sfx sfx = new();
    DateTime last = DateTime.UtcNow, lastWheel = DateTime.MinValue, lastSpawn = DateTime.MinValue;
    const int MaxWindows = 14;

    public FxController()
    {
        timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) =>
        {
            var now = DateTime.UtcNow; double dt = Math.Min(0.05, (now - last).TotalSeconds); last = now;
            bool any = false;
            foreach (var w in windows) if (w.Active) { w.Tick(dt); any |= w.Active; }
            if (!any) timer.Stop();
        };
    }

    /// <summary>Closes every overlay window and stops the animation clock (used by background mode).</summary>
    public void Shutdown()
    {
        timer.Stop();
        foreach (var w in windows) { try { w.Finish(); w.Close(); } catch { } }
        windows.Clear();
    }

    /// <summary>One big ring that grows from (x, y) and reaches radiusPx after durMs. Used for waves that travel between mouse, screen and keyboard.</summary>
    public void SpawnRing(int x, int y, double radiusPx, int durMs, string colorMode = "Fire", string color = "#FF8A1E")
    {
        var fx = new ScreenFx { Style = "Shockwave", Particles = 4, Flash = false, Shockwave = true, Sound = false, ColorMode = colorMode, Color = color, GravityPercent = 0,
            SizePercent = (int)Math.Clamp(radiusPx / 190.0 * 100, 40, 1500), DurationMs = (int)Math.Clamp(durMs / 0.75, 200, 4000) };
        Spawn(fx, x, y, false, 3200);
    }

    public void SetupSound(ScreenFx fx) => sfx.Configure(fx.VolumePercent / 100.0);

    /// <summary>Returns true if an effect was spawned.</summary>
    public bool Handle(Profile profile, InputEvt e, out int sx, out int sy)
    {
        sx = sy = 0;
        var fx = profile.Screen;
        if (!fx.Enabled) return false;
        switch (e.Kind)
        {
            case InputKind.Key:
                if (!fx.OnKeys) return false;
                if (fx.IgnoreAutoRepeat && e.Repeat) return false;
                if (fx.IgnoreModifiers && Keys.IsModifier(e.Vk)) return false;
                if (!PassesFilter(fx, e.Vk)) return false;
                break;
            case InputKind.Mouse:
                if (!(e.Button switch { "Left" => fx.OnMouseLeft, "Right" => fx.OnMouseRight, "Middle" => fx.OnMouseMiddle, _ => fx.OnMouseSide })) return false;
                break;
            case InputKind.Wheel:
                if (!fx.OnWheel) return false;
                if ((DateTime.UtcNow - lastWheel).TotalMilliseconds < 140) return false;
                lastWheel = DateTime.UtcNow;
                break;
        }

        if (e.Kind == InputKind.Key) PositionForKey(fx, out sx, out sy);
        else { sx = e.X; sy = e.Y; }

        Spawn(fx, sx, sy, e.Kind == InputKind.Key);
        return true;
    }

    static bool PassesFilter(ScreenFx fx, int vk)
    {
        if (fx.KeyFilterMode == "All" || string.IsNullOrWhiteSpace(fx.KeyFilterList)) return true;
        bool listed = fx.KeyFilterList.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Any(n => Keys.Parse(n) == vk);
        return fx.KeyFilterMode == "Only" ? listed : !listed;
    }

    void PositionForKey(ScreenFx fx, out int x, out int y)
    {
        GetCursorPos(out var cur);
        x = cur.X; y = cur.Y;
        try
        {
            if (fx.KeyPosition == "Caret")
            {
                var fg = GetForegroundWindow();
                uint tid = GetWindowThreadProcessId(fg, out _);
                var gi = new GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<GUITHREADINFO>() };
                if (GetGUIThreadInfo(tid, ref gi) && gi.hwndCaret != IntPtr.Zero && (gi.rcCaret.Bottom - gi.rcCaret.Top) > 0)
                {
                    var pt = new POINT { X = gi.rcCaret.Left, Y = (gi.rcCaret.Top + gi.rcCaret.Bottom) / 2 };
                    if (ClientToScreen(gi.hwndCaret, ref pt)) { x = pt.X; y = pt.Y; }
                }
            }
            else if (fx.KeyPosition == "WindowCenter")
            {
                if (GetWindowRect(GetForegroundWindow(), out var r) && r.Right > r.Left) { x = (r.Left + r.Right) / 2; y = (r.Top + r.Bottom) / 2; }
            }
            else if (fx.KeyPosition == "Random")
            {
                var sb = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(cur.X, cur.Y)).Bounds;
                x = sb.Left + rng.Next(sb.Width); y = sb.Top + rng.Next(sb.Height);
            }
        }
        catch { }
    }

    public void Spawn(ScreenFx fx, int x, int y, bool fromKey, int maxSide = 1400)
    {
        if ((DateTime.UtcNow - lastSpawn).TotalMilliseconds < 6) return;
        lastSpawn = DateTime.UtcNow;
        double scale = Math.Clamp(fx.SizePercent, 20, 400) / 100.0;
        var b = BuildBurst(fx, scale);
        int half = (int)((b.Style is "Fireworks" or "Confetti" ? 300 : 250) * scale);
        int side = Math.Clamp(half * 2, 120, maxSide);

        var w = windows.FirstOrDefault(v => !v.Active);
        if (w == null)
        {
            if (windows.Count < MaxWindows) { w = new OverlayWindow(); w.EnsureCreated(); windows.Add(w); }
            else w = windows.OrderBy(v => v.StartedAt).First();
        }
        w.Begin(b, x, y, side);
        if (!timer.IsEnabled) { last = DateTime.UtcNow; timer.Start(); }

        if (fx.Sound && (!fromKey || fx.SoundOnKeys)) sfx.Play(fromKey);
    }

    BurstState BuildBurst(ScreenFx fx, double scale)
    {
        var b = new BurstState { Style = fx.Style, ScalePx = scale, Duration = Math.Clamp(fx.DurationMs, 150, 4000) / 1000.0, Shock = fx.Shockwave, Flash = fx.Flash };
        double dur = b.Duration;
        int n = Math.Clamp(fx.Particles, 4, 300);
        double gScale = Math.Clamp(fx.GravityPercent, -100, 300) / 100.0;
        var baseCol = Col.Parse(fx.Color, Color.FromRgb(255, 138, 30));
        b.Hue = rng.NextDouble() * 360;
        double fr = baseCol.R / 255.0, fg = baseCol.G / 255.0, fb = baseCol.B / 255.0;
        b.FlashR = fx.ColorMode == "Fire" ? 1 : Math.Min(1, fr + .25); b.FlashG = fx.ColorMode == "Fire" ? .65 : Math.Min(1, fg + .25); b.FlashB = fx.ColorMode == "Fire" ? .15 : Math.Min(1, fb + .25);
        if (fx.ColorMode is "Rainbow" or "Random" or "Neon" or "Ice") { var c0 = PickColor(fx, b, 0, fr, fg, fb); b.FlashR = c0.r; b.FlashG = c0.g; b.FlashB = c0.b; }

        switch (fx.Style)
        {
            case "Sparks": b.Shock = false; break;
            case "Shockwave": b.Shock = true; n = Math.Min(n, 14); break;
        }
        b.ShockMax = (fx.Style == "Shockwave" ? 190 : 150) * scale;
        int streaks = fx.Style switch { "Explosion" => n / 2, "Sparks" => n, "Fireworks" => n / 3, "Shockwave" => 6, _ => 0 };
        int blobs = fx.Style switch { "Explosion" => n, "Fireworks" => n, "Confetti" => n, "Pixels" => n, "Shockwave" => 8, _ => 0 };
        b.P = new Particle[blobs + streaks];
        int idx = 0;
        bool fire = fx.ColorMode == "Fire";
        double burstHue = b.Hue;

        for (int i = 0; i < blobs; i++)
        {
            double ang = rng.NextDouble() * Math.PI * 2;
            var p = new Particle { MaxLife = dur * (0.55 + rng.NextDouble() * 0.45), Fire = fire };
            p.Life = p.MaxLife;
            var c = PickColor(fx, b, i, fr, fg, fb); p.R = c.r; p.G = c.g; p.B = c.b;
            switch (fx.Style)
            {
                case "Fireworks":
                    {
                        double sp = (120 + rng.NextDouble() * 200) * scale;
                        p.Kind = 4; p.Size = (2.5 + rng.NextDouble() * 2) * scale; p.Drag = 1.6; p.Gy = 260 * gScale * scale;
                        p.Vx = Math.Cos(ang) * sp; p.Vy = Math.Sin(ang) * sp; p.Fire = false;
                        if (fx.ColorMode == "Fire") { var hc = Col.Hsv(burstHue + rng.NextDouble() * 50 - 25, .85, 1); p.R = hc.r; p.G = hc.g; p.B = hc.b; }
                        p.MaxLife = p.Life = dur * (0.7 + rng.NextDouble() * 0.3);
                        break;
                    }
                case "Confetti":
                    {
                        double sp = (150 + rng.NextDouble() * 300) * scale; double up = -0.55;
                        p.Kind = 2; p.Size = (3 + rng.NextDouble() * 4) * scale; p.Drag = 1.1; p.Gy = 520 * gScale * scale;
                        p.Vx = Math.Cos(ang) * sp; p.Vy = Math.Sin(ang) * sp * .8 + up * 140 * scale; p.Fire = false;
                        p.Rot = rng.NextDouble() * 6; p.Vrot = (rng.NextDouble() - .5) * 18;
                        if (fx.ColorMode == "Fire") { var hc = Col.Hsv(rng.NextDouble() * 360, .8, 1); p.R = hc.r; p.G = hc.g; p.B = hc.b; }
                        p.MaxLife = p.Life = dur * (0.8 + rng.NextDouble() * 0.5);
                        break;
                    }
                case "Pixels":
                    {
                        double sp = (100 + rng.NextDouble() * 260) * scale;
                        p.Kind = 3; p.Size = (6 + rng.NextDouble() * 6) * scale; p.Drag = 1.8; p.Gy = 420 * gScale * scale; p.Fire = false;
                        p.Vx = Math.Cos(ang) * sp; p.Vy = Math.Sin(ang) * sp;
                        if (fx.ColorMode == "Fire") { p.R = 1; p.G = .35 + rng.NextDouble() * .6; p.B = .05; }
                        break;
                    }
                case "Shockwave":
                    {
                        double sp = (60 + rng.NextDouble() * 100) * scale;
                        p.Kind = 0; p.Size = (3 + rng.NextDouble() * 3) * scale; p.Drag = 3; p.Gy = 0;
                        p.Vx = Math.Cos(ang) * sp; p.Vy = Math.Sin(ang) * sp;
                        break;
                    }
                default: // Explosion
                    {
                        double sp = (60 + Math.Pow(rng.NextDouble(), .7) * 330) * scale;
                        p.Kind = 0; p.Size = (4 + rng.NextDouble() * 10) * scale; p.Drag = 2.3;
                        p.Gy = (fire ? -60 : 40) * scale + 330 * (gScale - .4) * scale * .6;
                        p.Vx = Math.Cos(ang) * sp; p.Vy = Math.Sin(ang) * sp;
                        break;
                    }
            }
            b.P[idx++] = p;
        }
        for (int i = 0; i < streaks; i++)
        {
            double ang = rng.NextDouble() * Math.PI * 2, sp = (260 + rng.NextDouble() * 520) * scale;
            var p = new Particle { Kind = 1, Size = (2.5 + rng.NextDouble() * 2) * scale, Drag = 2.4, Gy = 700 * gScale * scale, Fire = false };
            p.MaxLife = p.Life = dur * (0.35 + rng.NextDouble() * 0.45);
            p.Vx = Math.Cos(ang) * sp; p.Vy = Math.Sin(ang) * sp;
            var c = fx.ColorMode == "Fire" ? (r: 1.0, g: .8 + rng.NextDouble() * .2, b: .35 + rng.NextDouble() * .3) : PickColor(fx, b, i, fr, fg, fb);
            p.R = c.r; p.G = c.g; p.B = c.b;
            b.P[idx++] = p;
        }
        b.Count = idx;
        return b;
    }

    (double r, double g, double b) PickColor(ScreenFx fx, BurstState burst, int i, double fr, double fg, double fb)
    {
        switch (fx.ColorMode)
        {
            case "Fixed": { double j = (rng.NextDouble() - .5) * .18; return (Math.Clamp(fr + j, 0, 1), Math.Clamp(fg + j, 0, 1), Math.Clamp(fb + j, 0, 1)); }
            case "Rainbow": return Col.Hsv(burst.Hue + i * 11 + rng.NextDouble() * 30, .85, 1);
            case "Ice": return Col.Hsv(190 + rng.NextDouble() * 40, .55 + rng.NextDouble() * .4, 1);
            case "Neon":
                {
                    var pal = new[] { (1.0, .1, .8), (.1, 1.0, 1.0), (.55, 1.0, .1), (1.0, .95, .1), (.6, .2, 1.0) };
                    return pal[rng.Next(pal.Length)];
                }
            case "Random": return Col.Hsv(rng.NextDouble() * 360, .8, 1);
            default: return (1, .55, .1);
        }
    }
}

// ======================================================================= sound synthesis
sealed class Sfx
{
    readonly List<SoundPlayer> booms = new(), pops = new();
    int bi, pi;
    double vol = -1;
    readonly Random rng = new(7);

    public void Configure(double volume)
    {
        volume = Math.Clamp(volume, 0, 1);
        if (Math.Abs(volume - vol) < 0.005) return;
        vol = volume;
        booms.Clear(); pops.Clear();
        for (int v = 0; v < 3; v++) for (int k = 0; k < 2; k++) booms.Add(Make(Boom(volume, v)));
        for (int v = 0; v < 3; v++) pops.Add(Make(Pop(volume, v)));
    }

    public void Play(bool key)
    {
        try
        {
            if (booms.Count == 0) Configure(0.35);
            if (key) pops[pi++ % pops.Count].Play(); else booms[bi++ % booms.Count].Play();
        }
        catch { }
    }

    static SoundPlayer Make(byte[] wav) { var sp = new SoundPlayer(new MemoryStream(wav)); sp.Load(); return sp; }

    byte[] Boom(double volume, int variant)
    {
        const int sr = 22050; int n = (int)(sr * 0.5);
        var s = new short[n];
        double lp = 0, ph = 0;
        double f0 = 58 + variant * 9;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / sr;
            double noise = rng.NextDouble() * 2 - 1;
            double cutoff = 0.55 * Math.Exp(-t * 7) + 0.03;
            lp += (noise - lp) * cutoff;
            double thump = Math.Sin(ph); ph += 2 * Math.PI * (f0 * (1 + 1.4 * Math.Exp(-t * 18))) / sr;
            double env = Math.Exp(-t * 6.5) * Math.Min(1, t * 400);
            double v = (lp * 1.5 * env + thump * 0.9 * Math.Exp(-t * 9)) * 0.7;
            s[i] = (short)(Math.Clamp(v * volume, -1, 1) * 32000);
        }
        return Wav(s, sr);
    }

    byte[] Pop(double volume, int variant)
    {
        const int sr = 22050; int n = (int)(sr * 0.11);
        var s = new short[n];
        double lp = 0, ph = 0, f = 220 + variant * 70;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / sr;
            lp += ((rng.NextDouble() * 2 - 1) - lp) * 0.35;
            ph += 2 * Math.PI * f * Math.Exp(-t * 20) / sr;
            double env = Math.Exp(-t * 38) * Math.Min(1, t * 900);
            double v = (lp * .6 + Math.Sin(ph) * .6) * env * .6;
            s[i] = (short)(Math.Clamp(v * volume, -1, 1) * 32000);
        }
        return Wav(s, sr);
    }

    static byte[] Wav(short[] data, int sr)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int bytes = data.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(sr); w.Write(sr * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(bytes);
        foreach (var v in data) w.Write(v);
        return ms.ToArray();
    }
}
