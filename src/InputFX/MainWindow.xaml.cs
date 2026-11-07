using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace InputFX;

public partial class MainWindow : Window
{
    readonly AppHost host;
    AppConfig Config => host.Config;
    Profile Current => host.Engine.Active;
    bool loading;
    readonly DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    public bool ForceClose;

    public MainWindow(AppHost h)
    {
        host = h;
        InitializeComponent();
        AboutVersion.Text = "InputFX " + typeof(MainWindow).Assembly.GetName().Version?.ToString(3);
        StyleBox.ItemsSource = new[] { "Explosion", "Fireworks", "Sparks", "Confetti", "Shockwave", "Pixels" };
        ColorModeBox.ItemsSource = new[] { "Fire", "Fixed", "Rainbow", "Ice", "Neon", "Random" };
        ActionCol.ItemsSource = new[] { "Key", "Text", "Run", "Macro", "Block", "Profile" };
        AutoKindCol.ItemsSource = new[] { "Click", "Key" };
        AutoModeCol.ItemsSource = new[] { "Toggle", "Hold" };
        KbModeList.ItemsSource = KbModes.All;
        var bg = new List<(string Tag, string Name)> { ("Static", "Solid color"), ("Dim", "Dim color"), ("Breathing", "Breathing"), ("Rainbow", "Rainbow drift"), ("Off", "Dark"), ("Typing", "Typing rhythm (reacts to how you type)"), ("Garden", "Garden (grows while you type)") };
        foreach (var a in KbAnims.Available) bg.Add(($"Anim:{a.Id}", "Animation: " + a.Name));
        BgBox.ItemsSource = bg.Select(x => new { x.Tag, x.Name }).ToList();
        GameList.ItemsSource = GameRunner.Catalog.Select(f => f()).ToList();
        GameList.SelectionChanged += (_, _) => { if (GameList.SelectedItem is Game gm) GameHelp.Text = gm.Help; };
        GameList.SelectedIndex = 0;
        GameScreen.Runner = host.Games;
        PulseStyleBox.ItemsSource = MouseLive.ZoneStyles.Select(x => new { x.Tag, x.Name }).ToList();
        MouseVarBox.ItemsSource = HwLab.MouseVariants.Select(v => v.Note.Length > 0 ? $"{v.Name}  ({v.Note})" : v.Name).ToList();
        MouseVarBox.SelectedIndex = 0;
        host.Engine.ProfileChanged += _ => Dispatcher.BeginInvoke(RefreshAll);
        host.Engine.EnabledChanged += _ => Dispatcher.BeginInvoke(RefreshHeader);
        statusTimer.Tick += (_, _) => UpdateStatus();
        statusTimer.Start();
        Loaded += (_, _) => RefreshAll();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!ForceClose) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }

    // ---------------------------------------------------------------- refresh
    void RefreshAll()
    {
        loading = true;
        try
        {
            ProfileBox.ItemsSource = null;
            ProfileBox.ItemsSource = Config.Profiles;
            ProfileBox.SelectedItem = Current;
            Tabs.DataContext = null;
            Tabs.DataContext = Current;
            RemapGrid.ItemsSource = null;
            RemapGrid.ItemsSource = Current.Remaps;
            CrossCard.DataContext = Config; AlertCard.DataContext = Config;
            AutoGrid.ItemsSource = null;
            AutoGrid.ItemsSource = Current.AutoTools;
            ExtraGamesBox.Text = string.Join(Environment.NewLine, Config.ExtraGameApps);
            MacroList.ItemsSource = null;
            MacroList.ItemsSource = Current.Macros;
            MacroList.SelectedIndex = Current.Macros.Count > 0 ? 0 : -1;
            MacroEditor.DataContext = MacroList.SelectedItem;
            MacroEditor.IsEnabled = MacroList.SelectedItem != null;
            AppsBox.Text = string.Join(Environment.NewLine, Current.Apps);
            AutoSwitchBox.IsChecked = Config.AutoSwitch;
            UseStreamBox.IsChecked = !Current.Keyboard.UseHwMode && Current.Keyboard.Enabled;
            KbModeList.SelectedItem = KbModes.All[KbModes.IndexOf(Current.Keyboard.HwMode)];
            Preview.Stream = Current.Keyboard;
            Preview.Tint = Col.Parse(Current.Keyboard.BaseColor, System.Windows.Media.Color.FromRgb(60, 140, 255));
            MouseNote.Text = "These are the mouse app's own backlight modes. 'Button Response' lights the mouse when you click. Your mouse originally used Ambilight.";
            UpdatePreviewCaption();
            StartupBox.IsChecked = host.IsAutoStart();
            RefreshHeader();
        }
        finally { loading = false; }
    }

    void RefreshHeader()
    {
        loading = true;
        EnabledBox.IsChecked = host.Engine.Enabled;
        EnabledBox.Content = host.Engine.Enabled ? "InputFX is ON" : "InputFX is OFF";
        StatusText.Text = "Active profile: " + Current.Name;
        loading = false;
    }

    static readonly double[] SpeedFactor = { 0.35, 0.6, 1.0, 1.5, 2.2 };

    void UpdateStatus()
    {
        if (!IsVisible) return;
        GuardStatus.Text = host.Guard.Check() ? "Locked right now: " + host.Guard.Reason : "Ready: no game detected.";
        GuardStatus.Foreground = host.Guard.Locked ? System.Windows.Media.Brushes.DarkRed : System.Windows.Media.Brushes.SeaGreen;
        Preview.HwSpeedFactor = SpeedFactor[Math.Clamp(Current.Keyboard.HwSpeed, 0, 4)];
        MouseLiveStatus.Text = (Current.Mouse.ClickPulse || Current.Mouse.AudioReactive) ? "Mouse live mode: " + host.Mouse.Status : "";
        AudioStatus.Text = Current.Keyboard.AudioEnabled ? "Audio: " + AudioAnalyzer.Instance.Status : "";
        if (host.Games.Current is Game cg)
        {
            GameScore.Text = $"{cg.Name}   score {cg.Score}" + (cg.Over ? "   GAME OVER, Space restarts" : "");
            GameBest.Text = Config.HighScores.TryGetValue(cg.Name, out int hs) ? $"Best so far: {hs}" : "";
        }
        else GameScore.Text = "";
        var s = host.Lights.Status;
        KbStatus.Text = !Current.Keyboard.Enabled ? "Keyboard streaming is off."
            : s.Connected ? $"Keyboard connected · {s.Frames} frames sent · last frame {s.LastFrameMs:F0} ms"
            : "Keyboard: " + (string.IsNullOrEmpty(s.Message) ? "starting…" : s.Message);
        var pr = Process.GetCurrentProcess();
        FootText.Text = $"Memory {pr.WorkingSet64 / 1048576} MB  ·  Config: {ConfigStore.FilePath}";
    }

    // ---------------------------------------------------------------- header
    void Enabled_Changed(object s, RoutedEventArgs e) { if (!loading) host.Engine.Enabled = EnabledBox.IsChecked == true; RefreshHeader(); }

    void ProfileBox_Changed(object s, SelectionChangedEventArgs e)
    {
        if (loading || ProfileBox.SelectedItem is not Profile p) return;
        host.Engine.SetActive(p.Name);
    }

    void NewProfile_Click(object s, RoutedEventArgs e)
    {
        var name = Prompt("New profile", "Name for the new profile:", "Profile " + (Config.Profiles.Count + 1));
        if (name == null) return;
        if (Config.Profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) { MessageBox.Show(this, "A profile with that name exists."); return; }
        Config.Profiles.Add(new Profile { Name = name });
        host.Engine.SetActive(name);
        RefreshAll();
    }

    void CopyProfile_Click(object s, RoutedEventArgs e)
    {
        var name = Prompt("Copy profile", "Name for the copy:", Current.Name + " copy");
        if (name == null) return;
        if (Config.Profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) { MessageBox.Show(this, "A profile with that name exists."); return; }
        var json = System.Text.Json.JsonSerializer.Serialize(Current);
        var copy = System.Text.Json.JsonSerializer.Deserialize<Profile>(json)!;
        copy.Name = name; copy.IsDefault = false; copy.Apps = new();
        Config.Profiles.Add(copy);
        host.Engine.SetActive(name);
        RefreshAll();
    }

    void RenameProfile_Click(object s, RoutedEventArgs e)
    {
        var name = Prompt("Rename profile", "New name:", Current.Name);
        if (name == null || name == Current.Name) return;
        if (Config.Profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) { MessageBox.Show(this, "A profile with that name exists."); return; }
        Current.Name = name; Config.ActiveProfile = name;
        ConfigStore.Save(Config);
        RefreshAll();
    }

    void DeleteProfile_Click(object s, RoutedEventArgs e)
    {
        if (Config.Profiles.Count <= 1) { MessageBox.Show(this, "You need at least one profile."); return; }
        if (MessageBox.Show(this, $"Delete profile '{Current.Name}'?", "InputFX", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var del = Current;
        var next = Config.Profiles.First(p => p != del && p.IsDefault) ?? Config.Profiles.First(p => p != del);
        host.Engine.SetActive(next.Name);
        Config.Profiles.Remove(del);
        if (!Config.Profiles.Any(p => p.IsDefault)) Config.Profiles[0].IsDefault = true;
        ConfigStore.Save(Config);
        RefreshAll();
    }

    string? Prompt(string title, string label, string initial)
    {
        var w = new Window { Title = title, Width = 380, Height = 160, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false };
        var sp = new StackPanel { Margin = new Thickness(16) };
        var tb = new TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 12) };
        var ok = new Button { Content = "OK", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80 };
        sp.Children.Add(new TextBlock { Text = label });
        sp.Children.Add(tb);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        row.Children.Add(ok); row.Children.Add(cancel); sp.Children.Add(row);
        w.Content = sp;
        string? result = null;
        ok.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(tb.Text)) { result = tb.Text.Trim(); w.DialogResult = true; } };
        w.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        w.ShowDialog();
        return result;
    }

    // ---------------------------------------------------------------- screen fx
    void PickColor_Click(object s, RoutedEventArgs e)
    {
        var c = Col.Parse(Current.Screen.Color, System.Windows.Media.Color.FromRgb(255, 138, 30));
        using var dlg = new System.Windows.Forms.ColorDialog { Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B), FullOpen = true };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        Current.Screen.Color = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
        Current.Screen.ColorMode = "Fixed";
        Tabs.DataContext = null; Tabs.DataContext = Current;
    }

    void TestFx_Click(object s, RoutedEventArgs e)
    {
        var fx = Current.Screen;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            Native.GetCursorPos(out var p);
            host.Fx.SetupSound(fx);
            host.Fx.Spawn(fx, p.X, p.Y, false);
        };
        t.Start();
    }

    // ---------------------------------------------------------------- keyboard lights
    void TestKb_Click(object s, RoutedEventArgs e)
    {
        var k = Clone(Current.Keyboard); k.Enabled = true; k.UseHwMode = false;
        host.Config.LastKbMode = 0;
        host.Lights.Configure(k);
        var keys = new[] { 0x47, 0x54, 0x4A, 0x20, 0x31, 0x0D };
        int i = 0;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        t.Tick += (_, _) =>
        {
            if (i < keys.Length) host.Lights.Press(keys[i++]);
            else { t.Stop(); host.ApplyKeyboard(Current); }
        };
        t.Start();
    }

    void LedTest_Click(object s, RoutedEventArgs e)
    {
        var k = Clone(Current.Keyboard); k.Enabled = true; k.Style = "Key"; k.DurationMs = 1100; k.RainbowBurst = false; k.BurstColor = "#FFFFFF"; k.Base = "Dim"; k.UseHwMode = false;
        host.Config.LastKbMode = 0;
        host.Lights.Configure(k);
        var keys = new[] { 0x1B, 0x31, 0x51, 0x41, 0x5A, 0x20, 0x0D, 0x27 };
        var names = new[] { "Esc", "1", "Q", "A", "Z", "Space", "Enter", "Right arrow" };
        int i = 0;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1300) };
        t.Tick += (_, _) =>
        {
            if (i < keys.Length) { host.Lights.Press(keys[i]); LabStatus.Text = $"LED test: {names[i]} should flash white…"; i++; }
            else { t.Stop(); LabStatus.Text = "LED test finished. If a different key flashed, tell me which."; host.ApplyKeyboard(Current); }
        };
        t.Start();
    }

    static T Clone<T>(T o) => System.Text.Json.JsonSerializer.Deserialize<T>(System.Text.Json.JsonSerializer.Serialize(o))!;

    // ---------------------------------------------------------------- hardware lab
    bool VendorAppRunning(out string which)
    {
        which = "";
        foreach (var n in new[] { "OemDrv", "Gaming Mouse 3.0" })
            if (Process.GetProcessesByName(n).Length > 0) { which = n; return true; }
        return false;
    }

    void KbModeList_Changed(object s, SelectionChangedEventArgs e)
    {
        if (KbModeList.SelectedItem is not KbModes.Mode m) return;
        Preview.HwMode = m.Id;
        if (!loading) PrevBuiltIn.IsChecked = true;
        UpdatePreviewCaption();
    }

    void Prev_Changed(object s, RoutedEventArgs e)
    {
        if (Preview == null) return;
        Preview.UseStream = PrevStream.IsChecked == true;
        UpdatePreviewCaption();
    }

    void UpdatePreviewCaption()
    {
        if (PreviewCaption == null || KbModeList == null) return;
        if (PrevStream.IsChecked == true) PreviewCaption.Text = "Showing your custom explosion stream. On the real keyboard it updates about 7 times per second.";
        else if (KbModeList.SelectedItem is KbModes.Mode m)
            PreviewCaption.Text = m.Reactive ? $"{m.Name}: reacts to the keys you press (press keys on your real keyboard to see the preview react). Runs inside the keyboard, so it is smooth. Preview is approximate."
                : $"{m.Name}: recorded from the keyboard app's own preview. Runs inside the keyboard, so it is smooth.";
    }

    void UseStream_Changed(object s, RoutedEventArgs e)
    {
        if (loading) return;
        bool on = UseStreamBox.IsChecked == true;
        Current.Keyboard.Enabled = on;
        Current.Keyboard.UseHwMode = !on;
        PrevStream.IsChecked = on;
        host.ApplyKeyboard(Current);
    }

    void KbUseMode_Click(object s, RoutedEventArgs e)
    {
        if (KbModeList.SelectedItem is not KbModes.Mode m) return;
        if (VendorAppRunning(out var w) && w == "OemDrv") { LabStatus.Text = "Close the EXCALIBUR Software app first."; return; }
        Current.Keyboard.UseHwMode = true;
        Current.Keyboard.Enabled = false;
        Current.Keyboard.HwMode = m.Id;
        UseStreamBox.IsChecked = false;
        host.Lights.Configure(new KeyboardHw { Enabled = false });
        LabStatus.Text = $"Writing mode {m.Id} ({m.Name}) to the keyboard…";
        host.WriteKeyboardMode(m.Id, Current.Keyboard.HwSpeed, Current.Keyboard.HwBrightness);
    }

    async void KbRestore_Click(object s, RoutedEventArgs e)
    {
        if (VendorAppRunning(out var w) && w == "OemDrv") { LabStatus.Text = "Close the EXCALIBUR Software app first."; return; }
        Current.Keyboard.UseHwMode = true; Current.Keyboard.Enabled = false; Current.Keyboard.HwMode = 11;
        UseStreamBox.IsChecked = false;
        host.Lights.Configure(new KeyboardHw { Enabled = false });
        LabStatus.Text = "Restoring original mode…";
        host.WriteKeyboardMode(11, 4, 4);
        KbModeList.SelectedItem = KbModes.All[KbModes.IndexOf(11)];
        await Task.CompletedTask;
    }

    public void SelectTab(int i) { if (i >= 0 && i < Tabs.Items.Count) Tabs.SelectedIndex = i; }
    public void SetLabStatus(string m) => Dispatcher.BeginInvoke(() => LabStatus.Text = m);
    public void NotePress(InputEvt e) { if (e.Kind == InputKind.Key && !e.Repeat && IsVisible) Preview.Press(e.Vk); }

    async void MouseVar_Click(object s, RoutedEventArgs e)
    {
        if (VendorAppRunning(out var w) && w == "Gaming Mouse 3.0") { LabStatus.Text = "Close the Gaming Mouse app first."; return; }
        int idx = MouseVarBox.SelectedIndex; if (idx < 0) return;
        LabStatus.Text = "Writing to the mouse… don't unplug it.";
        MouseProg.Value = 0;
        var prog = new Progress<int>(v => MouseProg.Value = v);
        var res = await HwLab.ApplyMouseVariant(idx, prog);
        if (res.Contains("written")) Config.LastMouseVariant = idx;
        LabStatus.Text = res;
    }

    // ---------------------------------------------------------------- remaps
    Action<InputEvt>? captureHandler;
    void CaptureKey_Click(object s, RoutedEventArgs e)
    {
        if (captureHandler != null) return;
        CaptureText.Text = "  Press a key or a mouse side/middle button now…";
        captureHandler = ev =>
        {
            string? src = ev.Kind switch
            {
                InputKind.Key => Keys.NameOf(ev.Vk),
                InputKind.Mouse when ev.Button is "Middle" or "X1" or "X2" => "Mouse" + ev.Button,
                _ => null
            };
            if (src == null) return;
            host.Engine.Pressed -= captureHandler!; captureHandler = null;
            Dispatcher.BeginInvoke(() =>
            {
                Current.Remaps.Add(new RemapRule { Source = src, Action = "Key", Target = "" });
                RemapGrid.ItemsSource = null; RemapGrid.ItemsSource = Current.Remaps;
                CaptureText.Text = $"  Added a row for {src}. Set what it should do.";
            });
        };
        host.Engine.Pressed += captureHandler;
    }

    // ---------------------------------------------------------------- macros
    void MacroList_Changed(object s, SelectionChangedEventArgs e)
    {
        MacroEditor.DataContext = MacroList.SelectedItem;
        MacroEditor.IsEnabled = MacroList.SelectedItem != null;
    }

    void AddMacro_Click(object s, RoutedEventArgs e)
    {
        var name = "Macro " + (Current.Macros.Count + 1);
        Current.Macros.Add(new MacroDef { Name = name });
        MacroList.ItemsSource = null; MacroList.ItemsSource = Current.Macros;
        MacroList.SelectedIndex = Current.Macros.Count - 1;
    }

    void DelMacro_Click(object s, RoutedEventArgs e)
    {
        if (MacroList.SelectedItem is not MacroDef m) return;
        Current.Macros.Remove(m);
        MacroList.ItemsSource = null; MacroList.ItemsSource = Current.Macros;
        MacroList.SelectedIndex = Current.Macros.Count > 0 ? 0 : -1;
    }

    async void RunMacro_Click(object s, RoutedEventArgs e)
    {
        if (MacroList.SelectedItem is not MacroDef m) return;
        FootText.Text = "Macro starts in 2 seconds — click where it should type.";
        await Task.Delay(2000);
        host.Engine.Macros.Start(m);
    }

    void StopMacro_Click(object s, RoutedEventArgs e) => host.Engine.Macros.StopAll();

    // ---------------------------------------------------------------- apps
    void AppsBox_LostFocus(object s, RoutedEventArgs e)
    {
        if (loading) return;
        Current.Apps = AppsBox.Text.Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    void RunningBox_Opened(object? s, EventArgs e)
    {
        RunningBox.ItemsSource = Process.GetProcesses().Where(p => { try { return p.MainWindowHandle != IntPtr.Zero && p.ProcessName != "InputFX"; } catch { return false; } })
            .Select(p => p.ProcessName.ToLowerInvariant()).Distinct().OrderBy(x => x).ToList();
    }

    void AddRunning_Click(object s, RoutedEventArgs e)
    {
        if (RunningBox.SelectedItem is not string n) return;
        if (!Current.Apps.Contains(n)) Current.Apps.Add(n);
        AppsBox.Text = string.Join(Environment.NewLine, Current.Apps);
    }

    void IsDefault_Click(object s, RoutedEventArgs e)
    {
        foreach (var p in Config.Profiles) p.IsDefault = p == Current;
        Tabs.DataContext = null; Tabs.DataContext = Current;
    }

    void AutoSwitch_Changed(object s, RoutedEventArgs e) { if (!loading) Config.AutoSwitch = AutoSwitchBox.IsChecked == true; }

    // ---------------------------------------------------------------- settings
    void Startup_Changed(object s, RoutedEventArgs e) { if (!loading) host.SetAutoStart(StartupBox.IsChecked == true); }
    void OpenFolder_Click(object s, RoutedEventArgs e) { System.IO.Directory.CreateDirectory(ConfigStore.Dir); Process.Start("explorer.exe", ConfigStore.Dir); }
    void OpenLog_Click(object s, RoutedEventArgs e)
    {
        var p = System.IO.Path.Combine(ConfigStore.Dir, "inputfx.log");
        if (System.IO.File.Exists(p)) Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
    }
    void Repo_RequestNavigate(object s, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    async void MousePulseTest_Click(object s, RoutedEventArgs e)
    {
        MouseNote.Text = "Watch the mouse…";
        int v = Config.LastMouseVariant;
        var st = Current.Mouse;
        MouseNote.Text = await Task.Run(() => MouseLive.Test(v, st));
    }

    void ExtraGames_LostFocus(object s, RoutedEventArgs e)
    {
        var list = ExtraGamesBox.Text.Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        Config.ExtraGameApps.Clear(); Config.ExtraGameApps.AddRange(list);
    }

    void TestAlert_Click(object s, RoutedEventArgs e) => host.Alerts.Trigger("Test alert", Config.Alerts.Apps.FirstOrDefault()?.Color ?? "#5865F2");

    void ResetGarden_Click(object s, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Start a fresh garden?", "InputFX", MessageBoxButton.YesNo) == MessageBoxResult.Yes) Array.Clear(Garden.Instance.Growth);
    }

    void Devices_Click(object s, RoutedEventArgs e) => host.ShowDevices();

    void Eco_Click(object s, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Turn on background mode?\n\nEffects, keyboard and mouse lights, audio and games pause so InputFX uses almost no power. Your remaps, macros and the auto clicker keep working. This window closes; use the tray icon to turn it off again.", "InputFX", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK) host.SetEco(true);
    }

    // ---------------------------------------------------------------- games
    void GamePlay_Click(object s, RoutedEventArgs e)
    {
        int i = GameList.SelectedIndex; if (i < 0) return;
        host.StartGame(GameRunner.Catalog[i]());
        GameHelp.Text = ((Game)GameList.SelectedItem).Help;
    }

    void GameStop_Click(object s, RoutedEventArgs e) => host.Games.Stop();
}
