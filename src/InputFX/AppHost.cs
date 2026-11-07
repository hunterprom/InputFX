using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace InputFX;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--selftest")) { SelfTest.Run(); return; }
        using var mutex = new Mutex(true, "InputFX.SingleInstance.7c1f", out bool first);
        if (!first)
        {
            System.Windows.MessageBox.Show("InputFX is already running (look for the icon in the tray, bottom-right).", "InputFX");
            return;
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("unhandled: " + e.ExceptionObject);
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) => { Log.Write("ui exception: " + e.Exception); e.Handled = true; };
        var host = new AppHost(app, args.Contains("--minimized"));
        int ti = Array.IndexOf(args, "--tab");
        if (ti >= 0 && ti + 1 < args.Length && int.TryParse(args[ti + 1], out int tab)) app.Dispatcher.BeginInvoke(() => { host.ShowWindow(); host.SelectTab(tab); }, DispatcherPriority.ApplicationIdle);
        app.Run();
        host.Dispose();
    }
}

public sealed class AppHost : IDisposable
{
    public AppConfig Config { get; }
    public Engine Engine { get; }
    public FxController Fx { get; }
    public KeyboardLights Lights { get; }
    public GameRunner Games { get; } = new();
    public MouseLive Mouse { get; } = new();
    public GameGuard Guard { get; } = new();
    public AlertHub Alerts { get; private set; } = null!;
    public AutoRunner Auto { get; }

    readonly System.Windows.Application app;
    readonly WinForms.NotifyIcon tray;
    MainWindow? window;
    readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    string lastJson = "";
    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public AppHost(System.Windows.Application a, bool startMinimized)
    {
        app = a;
        Config = ConfigStore.Load();
        Engine = new Engine(Config);
        Fx = new FxController();
        Lights = new KeyboardLights();
        Fx.SetupSound(Engine.Active.Screen);
        ApplyKeyboard(Engine.Active);

        Auto = new AutoRunner(Guard);
        Guard.Extra = Config.ExtraGameApps;
        Auto.SetSource(() => Engine.Active.AutoTools);
        Auto.Notice += m => app.Dispatcher.BeginInvoke(() => tray?.ShowBalloonTip(1200, "InputFX", m, WinForms.ToolTipIcon.Info));
        Engine.AutoKeyFilter = Auto.Key;
        Engine.MacroGuard = () => Guard.Check();
        Garden.Instance.Load(Config.Garden); Config.Garden = Garden.Instance.Growth;
        Alerts = new AlertHub { Config = () => Config.EcoMode ? new AlertsCfg { Enabled = false } : Config.Alerts };
        Alerts.Raised += (title, color) => app.Dispatcher.BeginInvoke(() =>
        {
            tray?.ShowBalloonTip(2500, "InputFX", title, WinForms.ToolTipIcon.Info);
            if (Engine.Active.Mouse.ClickPulse && !Config.EcoMode) Mouse.Pulse();
        });
        Engine.GameKeyFilter = Games.Key;
        Mouse.RestoreVariant = () => Config.LastMouseVariant;
        ApplyMouse(Engine.Active);
        if (Config.EcoMode) { Config.LastKbMode = 0; }
        Games.SaveScore = (name, score) => { if (!Config.HighScores.TryGetValue(name, out int best) || score > best) Config.HighScores[name] = score; };
        Games.Stopped += () => { Lights.SetExternal(null); Config.LastKbMode = 0; ApplyKeyboard(Engine.Active); };
        Engine.Pressed += OnInput;
        Engine.ProfileChanged += p =>
        {
            Fx.SetupSound(p.Screen);
            Auto.StopAll();
            ApplyKeyboard(p);
            ApplyMouse(p);
            UpdateTray();
        };
        Engine.Notice += msg => tray?.ShowBalloonTip(1500, "InputFX", msg, WinForms.ToolTipIcon.Info);

        tray = new WinForms.NotifyIcon { Icon = MakeIcon(), Visible = true, Text = "InputFX" };
        tray.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) ShowWindow(); };
        tray.ContextMenuStrip = new WinForms.ContextMenuStrip();
        tray.ContextMenuStrip.Opening += (_, _) => BuildMenu();
        UpdateTray();

        lastJson = JsonSerializer.Serialize(Config, Json);
        saveTimer.Tick += (_, _) => SaveIfChanged();
        saveTimer.Start();

        if (Config.EcoMode) { startMinimized = true; Native.SetEfficiencyMode(true); saveTimer.Interval = TimeSpan.FromSeconds(6); }
        if (!startMinimized) app.Dispatcher.BeginInvoke(ShowWindow, DispatcherPriority.ApplicationIdle);
        if (!Config.WizardDone) app.Dispatcher.BeginInvoke(ShowDevices, DispatcherPriority.ApplicationIdle);
    }

    void OnInput(InputEvt e)
    {
        if (Config.EcoMode) return;
        var p = Engine.Active;
        try
        {
            Fx.Handle(p, e, out _, out _);
            window?.NotePress(e);
            if (Config.CrossRipples) Cross(e);
            if (p.Mouse.ClickPulse && e.Kind == InputKind.Mouse) Mouse.Pulse();
            if (p.Keyboard.Enabled && !p.Keyboard.UseHwMode)
            {
                if (e.Kind == InputKind.Key && !e.Repeat) Lights.Press(e.Vk);
                else if (e.Kind == InputKind.Mouse) Lights.MousePress();
            }
        }
        catch (Exception ex) { Log.Write("fx: " + ex.Message); }
    }

    bool hwWriting;

    public void ApplyKeyboardNow() => ApplyKeyboard(Engine.Active);

    DeviceWizard? wizard;
    public void ShowDevices()
    {
        if (wizard == null || !wizard.IsLoaded) { wizard = new DeviceWizard(this); wizard.Closed += (_, _) => wizard = null; }
        wizard.Show(); wizard.Activate();
    }

    public void ApplyMouseNow() => ApplyMouse(Engine.Active);

    void ApplyMouse(Profile p)
    {
        if (Config.EcoMode) { Mouse.Enable(false); return; }
        Mouse.AudioReactive = p.Mouse.AudioReactive;
        Mouse.AudioSensitivity = p.Mouse.AudioSensitivity;
        Mouse.Settings = p.Mouse;
        Mouse.Enable(p.Mouse.ClickPulse || p.Mouse.AudioReactive);
    }

    /// <summary>Cross-device ripples: a click sends a wave down the screen to the keyboard; a key press sends one up from the keyboard to the pointer.</summary>
    void Cross(InputEvt e)
    {
        if (Config.EcoMode) return;
        double speed = Math.Max(300, Config.CrossSpeed);
        Native.GetCursorPos(out var cur);
        var sb = WinForms.Screen.FromPoint(new System.Drawing.Point(cur.X, cur.Y)).Bounds;
        if (e.Kind == InputKind.Mouse)
        {
            double dist = Math.Max(60, sb.Bottom - e.Y);
            int ms = (int)(dist / speed * 1000);
            double frac = (e.X - sb.Left) / (double)Math.Max(1, sb.Width);
            Fx.SpawnRing(e.X, e.Y, dist, ms, "Ice", "#6CC4FF");
            Delay(ms, () => Lights.BurstAtX(frac));
        }
        else if (e.Kind == InputKind.Key && Lights.Layout.ByVk.TryGetValue(e.Vk, out var k))
        {
            double minX = Lights.Layout.Keys.Min(x => x.X), maxX = Lights.Layout.Keys.Max(x => x.X);
            int sx = sb.Left + (int)((k.X - minX) / Math.Max(1, maxX - minX) * sb.Width), sy = sb.Bottom - 10;
            double dist = Math.Max(60, Math.Sqrt((cur.X - sx) * (double)(cur.X - sx) + (cur.Y - sy) * (double)(cur.Y - sy)));
            int ms = (int)(dist / speed * 1000);
            Fx.SpawnRing(sx, sy, dist, ms, "Fire");
            Delay(ms, () => { Fx.Spawn(new ScreenFx { Style = "Sparks", Particles = 14, Flash = true, Shockwave = false, DurationMs = 450, SizePercent = 70 }, cur.X, cur.Y, false); if (Engine.Active.Mouse.ClickPulse) Mouse.Pulse(); });
        }
    }

    void Delay(int ms, Action a)
    {
        var tm = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, ms)) };
        tm.Tick += (_, _) => { tm.Stop(); try { a(); } catch { } };
        tm.Start();
    }

    public bool Eco => Config.EcoMode;

    /// <summary>Background mode: one switch that stops everything that costs power but keeps remaps, macros and the auto clicker.</summary>
    public void SetEco(bool on)
    {
        Config.EcoMode = on;
        if (on)
        {
            if (Games.Running) Games.Stop();
            Fx.Shutdown();
            Lights.Stop();
            Mouse.Enable(false);
            if (window != null) { window.ForceClose = true; window.Close(); window = null; }
            Config.LastKbMode = 0;
            ApplyKeyboard(Engine.Active); // hand the keyboard back to its own smooth built-in mode
            saveTimer.Interval = TimeSpan.FromSeconds(6);
            Native.SetEfficiencyMode(true);
            tray.ShowBalloonTip(2000, "InputFX", "Background mode on: effects paused, remaps and macros still work.", WinForms.ToolTipIcon.Info);
        }
        else
        {
            Native.SetEfficiencyMode(false);
            saveTimer.Interval = TimeSpan.FromMilliseconds(800);
            Config.LastKbMode = 0;
            ApplyKeyboard(Engine.Active);
            ApplyMouse(Engine.Active);
            Fx.SetupSound(Engine.Active.Screen);
        }
        ConfigStore.Save(Config);
        UpdateTray();
    }

    public void StartGame(Game g)
    {
        if (Config.EcoMode) { tray.ShowBalloonTip(2000, "InputFX", "Turn off background mode to play.", WinForms.ToolTipIcon.Info); return; }
        if (Games.Running) Games.Stop();
        Games.Start(g);
        Lights.SetExternal(Games.LedFrame);
    }


    /// <summary>Applies the profile's keyboard lighting choice: the keyboard's own mode, or our custom stream.</summary>
    public void ApplyKeyboard(Profile p)
    {
        var k = p.Keyboard;
        if (k.UseHwMode || Config.EcoMode)
        {
            Lights.Configure(new KeyboardHw { Enabled = false });
            if (Config.LastKbMode != KbKey(k) && !hwWriting) WriteKeyboardMode(k.HwMode, k.HwSpeed, k.HwBrightness);
        }
        else
        {
            Lights.Configure(k);
            if (k.Enabled) Config.LastKbMode = 0; // streaming overrides the stored mode
        }
    }

    static int KbKey(KeyboardHw k) => k.HwMode * 100 + k.HwSpeed * 10 + k.HwBrightness;

    public async void WriteKeyboardMode(int mode, int speed = -1, int bright = -1)
    {
        if (System.Diagnostics.Process.GetProcessesByName("OemDrv").Length > 0) { Engine_Notice("Close the EXCALIBUR Software app first."); return; }
        hwWriting = true;
        var msg = await Task.Run(() => HwLab.ApplyKeyboardMode(mode, Lights, speed, bright));
        hwWriting = false;
        if (msg.Contains("written")) Config.LastKbMode = mode * 100 + (speed < 0 ? 4 : speed) * 10 + (bright < 0 ? 4 : bright);
        Engine_Notice(msg);
    }

    void Engine_Notice(string m) { try { tray.ShowBalloonTip(2000, "InputFX", m, WinForms.ToolTipIcon.Info); } catch { } window?.SetLabStatus(m); }

    void SaveIfChanged()
    {
        try
        {
            var j = JsonSerializer.Serialize(Config, Json);
            if (j == lastJson) return;
            lastJson = j;
            ConfigStore.Save(Config);
            Engine.ProfileEdited();
            Fx.SetupSound(Engine.Active.Screen);
            ApplyKeyboard(Engine.Active);
            ApplyMouse(Engine.Active);
        }
        catch (Exception e) { Log.Write("save: " + e.Message); }
    }

    public void SelectTab(int i) => window?.SelectTab(i);

    public void ShowWindow()
    {
        window ??= new MainWindow(this);
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    void UpdateTray()
    {
        tray.Text = ("InputFX · " + Engine.Active.Name + (Engine.Enabled ? "" : " (paused)") + (Config.EcoMode ? " · background" : ""));
        if (tray.Text.Length > 63) tray.Text = tray.Text[..63];
    }

    void BuildMenu()
    {
        var m = tray.ContextMenuStrip!;
        m.Items.Clear();
        m.Items.Add("Open settings", null, (_, _) => ShowWindow());
        m.Items.Add("My devices", null, (_, _) => ShowDevices());
        var eco = new WinForms.ToolStripMenuItem("Background mode (low power)") { Checked = Config.EcoMode };
        eco.Click += (_, _) => SetEco(!Config.EcoMode);
        m.Items.Add(eco);
        var en = new WinForms.ToolStripMenuItem("Effects and remaps on") { Checked = Engine.Enabled, CheckOnClick = false };
        en.Click += (_, _) => { Engine.Enabled = !Engine.Enabled; UpdateTray(); };
        m.Items.Add(en);
        var pm = new WinForms.ToolStripMenuItem("Profile");
        foreach (var p in Config.Profiles)
        {
            var name = p.Name;
            var it = new WinForms.ToolStripMenuItem(name) { Checked = p == Engine.Active };
            it.Click += (_, _) => Engine.SetActive(name);
            pm.DropDownItems.Add(it);
        }
        m.Items.Add(pm);
        if (Games.Running) m.Items.Add("Stop game", null, (_, _) => Games.Stop());
        m.Items.Add(new WinForms.ToolStripSeparator());
        m.Items.Add("Quit InputFX", null, (_, _) => Quit());
    }

    void Quit()
    {
        ConfigStore.Save(Config);
        if (window != null) { window.ForceClose = true; window.Close(); }
        tray.Visible = false;
        app.Shutdown();
    }

    // ---------------------------------------------------------------- startup registration
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public bool IsAutoStart()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("InputFX") != null; } catch { return false; }
    }

    public void SetAutoStart(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true)!;
            if (on) k.SetValue("InputFX", $"\"{Environment.ProcessPath}\" --minimized");
            else k.DeleteValue("InputFX", false);
            Config.StartWithWindows = on;
        }
        catch (Exception e) { Log.Write("autostart: " + e.Message); }
    }

    // ---------------------------------------------------------------- icon
    static Icon MakeIcon()
    {
        var res = typeof(AppHost).Assembly.GetManifestResourceStream("InputFX.ico");
        if (res != null) using (res) return new Icon(res, WinForms.SystemInformation.SmallIconSize);
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var pts = new List<PointF>();
            for (int i = 0; i < 16; i++)
            {
                double a = i * Math.PI / 8 - Math.PI / 2, r = i % 2 == 0 ? 15 : 7;
                pts.Add(new PointF((float)(16 + Math.Cos(a) * r), (float)(16 + Math.Sin(a) * r)));
            }
            using var outer = new SolidBrush(Color.FromArgb(232, 89, 12));
            using var inner = new SolidBrush(Color.FromArgb(255, 212, 59));
            g.FillPolygon(outer, pts.ToArray());
            g.FillEllipse(inner, 9, 9, 14, 14);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        saveTimer.Stop();
        Lights.Dispose();
        Auto.StopAll();
        Alerts.Dispose();
        Mouse.Dispose();
        Engine.Dispose();
        tray.Dispose();
    }
}
