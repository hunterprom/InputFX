using System.IO;

namespace InputFX;

/// <summary>Headless checks, run with: InputFX.exe --selftest. Writes selftest.txt next to the config.</summary>
public static class SelfTest
{
    public static void Run()
    {
        var sb = new System.Text.StringBuilder();
        var rng = new Random(1);
        try
        {
            foreach (var make in GameRunner.Catalog)
            {
                var g = make();
                var grid = new Px[Game.W, Game.H];
                g.Reset();
                int overs = 0, maxScore = 0;
                for (int i = 0; i < 6000; i++)
                {
                    if (rng.Next(3) == 0) g.Press((Btn)rng.Next(5));
                    g.Step(); g.Draw(grid);
                    if (g.Over) { overs++; g.Press(Btn.Action); }
                    maxScore = Math.Max(maxScore, g.Score);
                    foreach (var p in grid) if (double.IsNaN(p.R) || double.IsNaN(p.G) || double.IsNaN(p.B)) throw new Exception(g.Name + " produced NaN");
                }
                sb.AppendLine($"game {g.Name}: ok, restarts={overs}, bestScore={maxScore}");
            }

            var layout = KbLayout.Load();
            sb.AppendLine($"layout keys: {layout.Keys.Count}");
            foreach (var style in KbCompose.BurstStyles)
                foreach (var bg in new[] { "Static", "Rainbow", "Anim:3", "Anim:11" })
                {
                    var cfg = new KeyboardHw { Style = style, Base = bg, RadiusKeys = 10, BurstColorMode = "Fire", AudioEnabled = true, AudioStyle = "Equalizer" };
                    var af = new AudioFrame(); for (int i = 0; i < 19; i++) af.Bands[i] = i / 19.0;
                    double sumLit = 0;
                    foreach (double age in new[] { 0.05, 0.3, 0.6 })
                    {
                        var rgb = KbCompose.Compute(layout, cfg, new[] { (layout.Keys[20], age, 1.0, .5, .1) }, 1.0, af);
                        foreach (var c in rgb) { if (double.IsNaN(c.r + c.g + c.b)) throw new Exception($"NaN in {style}/{bg}"); sumLit += c.r + c.g + c.b; }
                    }
                    if (bg == "Static") sb.AppendLine($"burst {style}: ok (energy {sumLit:F0})");
                }

            foreach (var st in MouseLive.ZoneStyles)
                foreach (var idle in new[] { "Off", "All" })
                {
                    var shape = MouseLive.ZoneShape(new MouseHw { PulseStyle = st.Tag, IdleLight = idle });
                    if (shape.Count == 0 || shape.Any(x => x.ms < 5)) throw new Exception("bad pulse shape " + st.Tag);
                }
            sb.AppendLine($"mouse pulse styles: {MouseLive.ZoneStyles.Length} ok");

            var tm = TypingMeter.Instance; for (int i = 0; i < 20; i++) { tm.Press(); Thread.Sleep(80); tm.Update(); }
            var tcfg = new KeyboardHw { Base = "Typing" };
            var tr = KbCompose.Compute(layout, tcfg, Array.Empty<(KbLayout.KeyInfo, double, double, double, double)>(), 1.0, null);
            sb.AppendLine($"typing rhythm: energy {tm.Energy:F2}, streak {tm.Streak}, lit {tr.Count(c => c.r + c.g + c.b > 0.1)} keys");
            Garden.Instance.Press(layout.Keys[5]); Garden.Instance.Press(layout.Keys[9]);
            var gr = KbCompose.Compute(layout, new KeyboardHw { Base = "Garden" }, Array.Empty<(KbLayout.KeyInfo, double, double, double, double)>(), 1.0, null);
            sb.AppendLine($"garden: ok ({gr.Count(c => c.r + c.g + c.b > 0.05)} cells lit)");

            var guard = new GameGuard();
            sb.AppendLine($"game guard: locked={guard.Check()} {guard.Reason}");

            var devs = DeviceCatalog.Scan();
            sb.AppendLine("devices: " + string.Join("; ", devs.Select(d => $"{d.Kind} {d.Vid:X4}:{d.Pid:X4} {(d.FullSupport ? "supported" : "generic")}")));

            var a = AudioAnalyzer.Instance;
            a.Acquire();
            Thread.Sleep(1500);
            var fr = a.Snapshot();
            sb.AppendLine($"audio: {a.Status}, level {fr.Level:F3}");
            a.Release();

            var f04 = HwLabTemplate();
            sb.AppendLine("kb template frame ok: " + f04);
        }
        catch (Exception e) { sb.AppendLine("FAILED: " + e); }
        Directory.CreateDirectory(ConfigStore.Dir);
        File.WriteAllText(Path.Combine(ConfigStore.Dir, "selftest.txt"), sb.ToString());
    }

    static string HwLabTemplate()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        using var s = asm.GetManifestResourceStream("kb_cfg04.bin")!;
        var b = new byte[520]; s.ReadExactly(b);
        return $"effect byte {b[18]}, slot7 {b[78]:x2} {b[79]:x2}";
    }
}
