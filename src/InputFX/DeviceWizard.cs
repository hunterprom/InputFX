using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HidSharp;

namespace InputFX;

public sealed record SeenDevice(string Kind, ushort Vid, ushort Pid, string Name, string Support, bool FullSupport);

public static class DeviceCatalog
{
    /// <summary>Devices InputFX can light up on the hardware itself.</summary>
    static readonly (ushort vid, ushort pid, string kind, string name)[] Known =
    {
        (0x258A, 0x019D, "Keyboard", "EXCALIBUR / White Shark K916 keyboard (SINOWEALTH)"),
        (0x258A, 0x0150, "Keyboard", "EXCALIBUR keyboard, wireless dongle (SINOWEALTH)"),
        (0x30FA, 0x1440, "Mouse", "Galahad GM-5007 / USB GAMING MOUSE"),
    };

    public static (string kind, string name)? Lookup(ushort vid, ushort pid)
    {
        foreach (var k in Known) if (k.vid == vid && k.pid == pid) return (k.kind, k.name);
        return null;
    }

    /// <summary>All keyboards and mice currently plugged in.</summary>
    public static List<SeenDevice> Scan()
    {
        var found = new Dictionary<(ushort, ushort), (bool kb, bool mouse, string name)>();
        foreach (var d in DeviceList.Local.GetHidDevices())
        {
            bool kb = false, mouse = false;
            try
            {
                foreach (var item in d.GetReportDescriptor().DeviceItems)
                    foreach (var u in item.Usages.GetAllValues())
                    {
                        if (u == 0x00010006) kb = true;
                        if (u == 0x00010002) mouse = true;
                    }
            }
            catch { continue; }
            if (!kb && !mouse) continue;
            var key = ((ushort)d.VendorID, (ushort)d.ProductID);
            string name = "";
            try { name = d.GetProductName(); } catch { }
            found.TryGetValue(key, out var cur);
            found[key] = (cur.kb || kb, cur.mouse || mouse, string.IsNullOrWhiteSpace(cur.name) ? name : cur.name);
        }
        var res = new List<SeenDevice>();
        foreach (var kv in found)
        {
            var (vid, pid) = kv.Key;
            var known = Lookup(vid, pid);
            string kind = known?.kind ?? (kv.Value.kb && !kv.Value.mouse ? "Keyboard" : kv.Value.mouse && !kv.Value.kb ? "Mouse" : "Keyboard + mouse");
            string name = known?.name ?? (string.IsNullOrWhiteSpace(kv.Value.name) ? "Unnamed HID device" : kv.Value.name);
            res.Add(new SeenDevice(kind, vid, pid, name,
                known != null ? "Full support: lights on the device, plus everything else" : "Works with: screen effects, remaps, macros, auto clicker, games on screen. Lights on the device itself are not supported yet.",
                known != null));
        }
        return res.OrderByDescending(x => x.FullSupport).ThenBy(x => x.Kind).ToList();
    }

    public static string Report(IEnumerable<SeenDevice> devs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("InputFX device report");
        sb.AppendLine("Windows: " + Environment.OSVersion.VersionString);
        foreach (var d in devs) sb.AppendLine($"- {d.Kind}: {d.Name}  VID {d.Vid:X4} PID {d.Pid:X4}  [{(d.FullSupport ? "supported" : "not supported yet")}]");
        return sb.ToString();
    }
}

/// <summary>"Is my stuff supported?" window: lists keyboards and mice, tells what works, tests lights, copies a report for support requests.</summary>
public sealed class DeviceWizard : Window
{
    readonly AppHost host;
    readonly StackPanel list = new();
    readonly TextBlock status = new() { Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };
    List<SeenDevice> devices = new();

    public DeviceWizard(AppHost h)
    {
        host = h;
        Title = "InputFX: your keyboard and mouse";
        Width = 760; Height = 560; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(244, 245, 247)); FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
        var root = new DockPanel { Margin = new Thickness(18) };
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(new TextBlock { Text = "Your keyboard and mouse", FontSize = 20, FontWeight = FontWeights.Bold });
        head.Children.Add(new TextBlock { Text = "Plug them in and they show up here. Anything with a green tick gets lights on the device itself. Every keyboard and mouse gets screen explosions, remaps, macros, the auto clicker and the games on screen.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 4, 0, 0) });
        DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);

        var bar = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        Button B(string text, Action a) { var b = new Button { Content = text, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 6) }; b.Click += (_, _) => a(); return b; }
        bar.Children.Add(B("Rescan", Rescan));
        bar.Children.Add(B("Test keyboard lights", TestKeyboard));
        bar.Children.Add(B("Test mouse lights", TestMouse));
        bar.Children.Add(B("Copy device report", () => { Clipboard.SetText(DeviceCatalog.Report(devices)); status.Text = "Copied. Paste it into a GitHub issue to ask for support for your device."; }));
        var done = B("Done", () => { host.Config.WizardDone = true; ConfigStore.Save(host.Config); Close(); });
        bar.Children.Add(done);
        DockPanel.SetDock(bar, Dock.Bottom); root.Children.Add(bar);
        DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);

        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        Rescan();
        DeviceList.Local.Changed += OnChanged;
        Closed += (_, _) => DeviceList.Local.Changed -= OnChanged;
    }

    void OnChanged(object? s, DeviceListChangedEventArgs e) => Dispatcher.BeginInvoke(Rescan);

    void Rescan()
    {
        devices = DeviceCatalog.Scan();
        list.Children.Clear();
        foreach (var d in devices)
        {
            var card = new Border { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(216, 220, 225)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 8) };
            var sp = new StackPanel();
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = d.FullSupport ? "✔" : "•", Foreground = d.FullSupport ? Brushes.SeaGreen : Brushes.Gray, FontSize = 18, Margin = new Thickness(0, 0, 10, 0), FontWeight = FontWeights.Bold });
            row.Children.Add(new TextBlock { Text = $"{d.Name}", FontWeight = FontWeights.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = $"   {d.Kind}  ·  {d.Vid:X4}:{d.Pid:X4}", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(row);
            sp.Children.Add(new TextBlock { Text = d.Support, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(28, 4, 0, 0) });
            card.Child = sp; list.Children.Add(card);
        }
        if (devices.Count == 0) list.Children.Add(new TextBlock { Text = "No keyboard or mouse found. Plug one in and it appears here on its own.", Foreground = Brushes.Gray });
        var warn = new List<string>();
        foreach (var n in new[] { "OemDrv", "Gaming Mouse 3.0" }) if (Process.GetProcessesByName(n).Length > 0) warn.Add(n);
        status.Text = warn.Count > 0 ? "Heads up: the vendor app (" + string.Join(", ", warn) + ") is running. Close it before using the lights, because two programs cannot control the same device." : "";
    }

    void TestKeyboard()
    {
        if (!devices.Any(d => d.Kind == "Keyboard" && d.FullSupport)) { status.Text = "No supported keyboard found. Screen effects work with any keyboard."; return; }
        status.Text = "Look at the keyboard: all keys light white and breathe for 3 seconds.";
        var t0 = DateTime.UtcNow;
        host.Lights.SetExternal(() =>
        {
            double v = 0.35 + 0.65 * (0.5 + 0.5 * Math.Sin((DateTime.UtcNow - t0).TotalSeconds * 5));
            var f = new (double r, double g, double b)[KbLayout.LedCount];
            for (int i = 0; i < f.Length; i++) f[i] = (v, v, v);
            return f;
        });
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3.2) };
        timer.Tick += (_, _) => { timer.Stop(); host.Lights.SetExternal(null); host.Config.LastKbMode = 0; host.ApplyKeyboardNow(); status.Text = "Keyboard test finished. If nothing lit up, close the vendor app and try again."; };
        timer.Start();
    }

    async void TestMouse()
    {
        if (!devices.Any(d => d.Kind == "Mouse" && d.FullSupport)) { status.Text = "No supported mouse found. Screen effects work with any mouse."; return; }
        status.Text = "Mouse test: switching to live mode (about 4 seconds), then 4 flashes. Takes about 10 seconds.";
        host.Mouse.Enable(false);
        int v = host.Config.LastMouseVariant;
        var st = host.Engine.Active.Mouse;
        status.Text = await Task.Run(() => MouseLive.Test(v, st));
        host.ApplyMouseNow();
    }
}
