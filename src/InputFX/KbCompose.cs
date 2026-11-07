using System.Diagnostics;
using System.Windows.Media;

namespace InputFX;

/// <summary>Built-in animated backgrounds (our own implementations of the classic keyboard light patterns).</summary>
public static class KbAnims
{
    public static readonly (int Id, string Name)[] Available =
    {
        (1, "Fixed on"), (2, "Respire"), (3, "Rainbow"), (5, "Raindrops"), (8, "Stars twinkle"), (9, "Shadow disappear"),
        (10, "Retro snake"), (11, "Neon stream"), (13, "Sine wave"), (15, "Rotating windmill"), (16, "Colorful waterfall"), (17, "Blossoming"),
    };

    public static bool Has(int id) => Available.Any(a => a.Id == id);

    static double H(int n) => KbCompose.Hash(n);

    /// <summary>Writes the animation's colours for time <paramref name="t"/> (seconds) into rgb, for keys that exist.</summary>
    public static bool Fill(int id, double t, KbLayout layout, (double r, double g, double b)[] rgb)
    {
        if (!Has(id)) return false;
        double cx = 380, cy = 140;
        int idx = 0;
        foreach (var k in layout.Keys)
        {
            idx++;
            double x = k.X, y = k.Y, dx = x - cx, dy = y - cy, ang = Math.Atan2(dy, dx), dist = Math.Sqrt(dx * dx + dy * dy);
            (double r, double g, double b) c;
            switch (id)
            {
                case 1: c = (0.2, 0.55, 1.0); break;
                case 2: c = Col.Hsv(t * 25, 1, 0.12 + 0.88 * (0.5 + 0.5 * Math.Sin(t * 2.4))); break;
                case 3: c = Col.Hsv(x * .5 + t * 30, 1, 1); break;
                case 5:
                    {
                        int col = (int)(x / 36); double ph = (H(col) * 3.1 + t * (0.7 + H(col + 40) * .6)) % 1.6;
                        double d = y / 260.0 - ph + .1; c = Col.Hsv(200, .6, Math.Max(0, 1 - Math.Abs(d) * 9)); break;
                    }
                case 8: { double ph = H(k.Led * 7) * 6.28, v = Math.Pow(Math.Max(0, Math.Sin(t * (1 + H(k.Led) * 1.5) + ph)), 10); c = (v, v, Math.Min(1, v + .1)); break; }
                case 9: { double sh = Math.Exp(-Math.Pow((x - ((t * 220) % 900 - 60)) / 80, 2)); c = Col.Hsv(x * .4, 1, 1 - .92 * sh); break; }
                case 10: { double head = (t * 14) % 98, d = ((head - idx) + 98) % 98; c = d < 9 ? Col.Hsv(120, 1, 1 - d / 9.0) : (0.02, 0.02, 0.04); break; }
                case 11: c = Col.Hsv(x * .5 - t * 110, 1, 1); break;
                case 13: c = Col.Hsv(x * .45 + 50 * Math.Sin(t * 2.5 + x / 55), 1, .35 + .65 * (0.5 + 0.5 * Math.Sin(t * 3 + x / 45))); break;
                case 15: { double spoke = Math.Pow(Math.Max(0, Math.Cos(3 * (ang - t * 1.6))), 2); c = Col.Hsv(ang * 57.3 + t * 40, 1, .15 + .85 * spoke); break; }
                case 16: c = Col.Hsv(y * 1.6 - t * 130 + x * .15, 1, 1); break;
                default: // 17 Blossoming: rings of colour opening from the middle
                    {
                        double sum = 0;
                        for (int i = 0; i < 3; i++) { double per = 1.8, e = ((t + i * per / 3.0) % per) / per; sum += Math.Exp(-Math.Pow((dist - e * 320) / 28, 2)) * (1 - e); }
                        c = Col.Hsv(t * 60, 1, Math.Min(1, .08 + sum)); break;
                    }
            }
            rgb[k.Led] = c;
        }
        return true;
    }
}

public sealed class AudioFrame
{
    public double[] Bands = new double[19];
    public double Level, Bass;
    public bool Beat;
}

public static class KbCompose
{
    public const double Pitch = 36.0;

    public static double Hash(int n) { unchecked { uint h = (uint)n * 2654435761u; h ^= h >> 15; h *= 2246822519u; h ^= h >> 13; return (h & 0xFFFF) / 65535.0; } }

    public static readonly string[] BurstStyles = { "Explosion", "Fireworks", "Shockwave", "Pulse", "Ripple", "Cross", "Spiral", "Sparkle", "Wave", "Key" };

    /// <summary>Colour of every LED: background, then audio, then press bursts.</summary>
    public static (double r, double g, double b)[] Compute(KbLayout layout, KeyboardHw c, IEnumerable<(KbLayout.KeyInfo key, double age, double r, double g, double b)> live, double now, AudioFrame? audio)
    {
        var rgb = new (double r, double g, double b)[KbLayout.LedCount];
        var bc = Col.Parse(c.BaseColor, Color.FromRgb(27, 42, 107));
        double br = bc.R / 255.0, bg = bc.G / 255.0, bb = bc.B / 255.0;
        double bgT = now * Math.Clamp(c.BgSpeedPercent, 25, 400) / 100.0;

        bool filled = false;
        if (c.Base.StartsWith("Anim:") && int.TryParse(c.Base.AsSpan(5), out int animId)) filled = KbAnims.Fill(animId, bgT, layout, rgb);
        if (c.Base == "Typing")
        {
            var tm = TypingMeter.Instance; tm.Update();
            foreach (var k in layout.Keys) rgb[k.Led] = tm.Color(k, bgT);
            // streak meter on the top row, one key per 5 presses in a row
            int lit = Math.Min(14, tm.Streak / 5);
            for (int col = 0; col < lit; col++) rgb[0 + 6 * col] = (1, 1, 1);
            if (tm.MilestoneFlash > 0 && Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency - tm.MilestoneAt < 0.9)
                foreach (var k in layout.Keys) { var cc = rgb[k.Led]; rgb[k.Led] = (Math.Min(1, cc.r + .5), Math.Min(1, cc.g + .5), Math.Min(1, cc.b + .5)); }
            filled = true;
        }
        else if (c.Base == "Garden") { var g = Garden.Instance.Render(bgT); for (int i = 0; i < rgb.Length; i++) rgb[i] = g[i]; filled = true; }
        if (!filled)
            foreach (var k in layout.Keys)
            {
                (double r, double g, double b) col = c.Base switch
                {
                    "Off" => (0, 0, 0),
                    "Dim" => (br * .25, bg * .25, bb * .25),
                    "Rainbow" => Col.Hsv(bgT * 40 + k.X * 0.45, 1, .8),
                    "Breathing" => (br * (0.25 + 0.75 * (0.5 + 0.5 * Math.Sin(bgT * 2.2))), bg * (0.25 + 0.75 * (0.5 + 0.5 * Math.Sin(bgT * 2.2))), bb * (0.25 + 0.75 * (0.5 + 0.5 * Math.Sin(bgT * 2.2)))),
                    _ => (br, bg, bb),
                };
                rgb[k.Led] = col;
            }

        if (audio != null && c.AudioEnabled) ApplyAudio(layout, c, rgb, audio, now);
        AlertHub.Apply(rgb, layout, now);

        double durS = Math.Max(150, c.DurationMs) / 1000.0;
        double spd = Math.Clamp(c.BurstSpeedPercent, 25, 400) / 100.0;
        foreach (var bu in live)
        {
            double t = Math.Clamp(bu.age * spd / durS, 0, 1);
            if (t >= 1) continue;
            double radius = Math.Clamp(c.RadiusKeys, 1, 24) * Pitch;
            double cr = bu.r, cg = bu.g, cb = bu.b;
            if (c.BurstColorMode == "Fire") { FireGrad(t, out cr, out cg, out cb); }
            foreach (var k in layout.Keys)
            {
                double dx = k.X - bu.key.X, dy = k.Y - bu.key.Y, d = Math.Sqrt(dx * dx + dy * dy);
                double amt = Amount(c.Style, dx, dy, d, t, radius, k.Led, bu.age);
                if (amt <= 0.003) continue;
                var cur = rgb[k.Led];
                double white = c.Style is "Explosion" or "Fireworks" ? Math.Max(0, 1 - d / (Pitch * 1.3)) * (1 - t) * .6 : 0;
                rgb[k.Led] = (Math.Min(1, cur.r + (cr + white) * amt), Math.Min(1, cur.g + (cg + white) * amt), Math.Min(1, cur.b + (cb + white) * amt));
            }
        }
        return rgb;
    }

    static double Amount(string style, double dx, double dy, double d, double t, double radius, int led, double age)
    {
        double e = 1 - Math.Pow(1 - t, 2);
        switch (style)
        {
            case "Key": return d < 8 ? 1 - t : 0;
            case "Pulse": return Math.Max(0, 1 - d / radius) * (1 - t) * (1 - t);
            case "Ripple": return Math.Exp(-Math.Pow((d - radius * Math.Sqrt(t)) / (Pitch * .9), 2)) * (1 - t);
            case "Shockwave":
                {
                    double r1 = radius * e, r2 = radius * (1 - Math.Pow(1 - Math.Max(0, t - .22), 2));
                    return Math.Min(1, Math.Exp(-Math.Pow((d - r1) / (Pitch * .75), 2)) * (1 - t) + .65 * Math.Exp(-Math.Pow((d - r2) / (Pitch * .75), 2)) * (1 - t)) * (d < 6 ? 1 : 1);
                }
            case "Cross":
                {
                    double reach = radius * e;
                    double h = Math.Exp(-Math.Pow(dy / (Pitch * .55), 2)) * (Math.Abs(dx) <= reach ? 1 : 0) * Math.Max(0, 1 - Math.Abs(dx) / (radius + 1));
                    double v = Math.Exp(-Math.Pow(dx / (Pitch * .55), 2)) * (Math.Abs(dy) <= reach ? 1 : 0) * Math.Max(0, 1 - Math.Abs(dy) / (radius + 1));
                    return Math.Max(h, v) * (1 - t);
                }
            case "Spiral":
                {
                    double ang = Math.Atan2(dy, dx), ring = Math.Exp(-Math.Pow((d - radius * e) / (Pitch * 1.1), 2));
                    double arm = Math.Pow(0.5 + 0.5 * Math.Cos(3 * (ang - t * 9)), 2);
                    return Math.Max(ring * arm, d < Pitch * .8 ? 1 - t : 0) * (1 - t * .6);
                }
            case "Sparkle":
                {
                    if (d > radius) return 0;
                    double rnd = Hash(led * 31 + (int)(age * 14));
                    return rnd > 0.55 ? (1 - d / radius) * (1 - t) * 1.2 : 0;
                }
            case "Wave":
                {
                    double reach = radius * e;
                    double band = Math.Exp(-Math.Pow((Math.Abs(dx) - reach) / (Pitch * .9), 2)) * Math.Exp(-Math.Pow(dy / (Pitch * 1.8), 2));
                    return band * (1 - t);
                }
            case "Fireworks":
                {
                    double ringR = radius * (1 - Math.Pow(1 - t, 3));
                    double ring = Math.Exp(-Math.Pow((d - ringR) / (Pitch * .55), 2));
                    double sp = Hash(led * 17 + (int)(age * 12)) > 0.35 ? 1 : 0.15;
                    double core = Math.Max(0, 1 - d / (Pitch * 1.1)) * Math.Pow(1 - t, 3);
                    return Math.Min(1, ring * sp * (1 - t) * 1.2 + core);
                }
            default: // Explosion
                {
                    double core = Math.Max(0, 1 - d / (Pitch * 1.25)) * Math.Pow(1 - t, 1.5);
                    double ringR = radius * e;
                    double ring = Math.Exp(-Math.Pow((d - ringR) / (Pitch * .85), 2)) * Math.Pow(1 - t, 1.2);
                    return Math.Min(1, core + ring);
                }
        }
    }

    static void FireGrad(double t, out double r, out double g, out double b)
    {
        if (t < .25) { r = 1; g = 1 - t; b = 0.9 - t * 2.5; }
        else if (t < .6) { double u = (t - .25) / .35; r = 1; g = .75 - u * .45; b = 0.2 - u * .2; }
        else { double u = (t - .6) / .4; r = 1 - u * .45; g = .3 - u * .25; b = 0; }
        b = Math.Max(0, b);
    }

    static void ApplyAudio(KbLayout layout, KeyboardHw c, (double r, double g, double b)[] rgb, AudioFrame a, double now)
    {
        double gain = 0.5 + Math.Clamp(c.AudioSensitivity, 0, 100) / 50.0;
        switch (c.AudioStyle)
        {
            case "Beat":
                {
                    double pulse = Math.Clamp(a.Bass * gain, 0, 1);
                    var col = Col.Hsv(now * 40, .6, 1);
                    double sc = 0.3 + 0.7 * pulse; // the background breathes with the beat
                    foreach (var k in layout.Keys) { var cur = rgb[k.Led]; rgb[k.Led] = (Math.Min(1, cur.r * sc + col.r * pulse * .35), Math.Min(1, cur.g * sc + col.g * pulse * .35), Math.Min(1, cur.b * sc + col.b * pulse * .35)); }
                    break;
                }
            case "Flow":
                {
                    foreach (var k in layout.Keys)
                    {
                        int col = k.Led / 6; double band = a.Bands[Math.Clamp(col, 0, 18)] * gain;
                        double v = Math.Clamp(band, 0, 1);
                        var h = Col.Hsv(col * 20 + now * 50, 1, 1);
                        var cur = rgb[k.Led]; rgb[k.Led] = (cur.r * (1 - v * .8) * .6 + h.r * v, cur.g * (1 - v * .8) * .6 + h.g * v, cur.b * (1 - v * .8) * .6 + h.b * v);
                    }
                    break;
                }
            case "Bass": break; // handled as automatic bursts
            default: // Equalizer
                {
                    // duck the background so the bars stand out on top of any animation
                    foreach (var k in layout.Keys) { var bgc = rgb[k.Led]; rgb[k.Led] = (bgc.r * .3, bgc.g * .3, bgc.b * .3); }
                    foreach (var k in layout.Keys)
                    {
                        int col = k.Led / 6, row = k.Led % 6;
                        double h = Math.Clamp(a.Bands[Math.Clamp(col, 0, 18)] * gain, 0, 1) * 6.0; // rows lit from the bottom
                        double lit = Math.Clamp(h - (5 - row), 0, 1);
                        if (lit <= 0) continue;
                        double hue = 120 - 120 * ((5 - row) / 5.0); // green at the bottom, red at the top
                        var cc = Col.Hsv(hue, 1, 1);
                        var cur = rgb[k.Led]; rgb[k.Led] = (cur.r * (1 - lit) + cc.r * lit, cur.g * (1 - lit) + cc.g * lit, cur.b * (1 - lit) + cc.b * lit);
                    }
                    break;
                }
        }
    }
}
