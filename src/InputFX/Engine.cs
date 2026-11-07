using System.Runtime.InteropServices;
using static InputFX.Native;

namespace InputFX;

public enum InputKind { Key, Mouse, Wheel }
public readonly record struct InputEvt(InputKind Kind, int Vk, string Button, bool Repeat, int X, int Y);

/// <summary>Low-level keyboard/mouse hooks, remapping, macros, profile switching. Must be created on the UI thread.</summary>
public sealed class Engine : IDisposable
{
    public AppConfig Config { get; }
    public Profile Active { get; private set; }
    public MacroRunner Macros { get; } = new();

    public event Action<InputEvt>? Pressed;
    public event Action<Profile>? ProfileChanged;
    public event Action<string>? Notice;
    /// <summary>While a keyboard game runs, return true to swallow game keys.</summary>
    public Func<int, bool, bool>? GameKeyFilter;
    /// <summary>Auto clicker hotkeys: (vk, down, repeat) -> swallow?</summary>
    public Func<int, bool, bool, bool>? AutoKeyFilter;
    /// <summary>Returns true when repeating macros must not run (game running).</summary>
    public Func<bool>? MacroGuard;

    bool enabled = true;
    public bool Enabled
    {
        get => enabled;
        set
        {
            if (enabled == value) return;
            enabled = value;
            if (!value) { Macros.StopAll(); ReleaseAllRemaps(); }
            Config.Enabled = value;
            Notice?.Invoke(value ? "InputFX enabled" : "InputFX paused (Ctrl+Alt+Shift+F12 to resume)");
            EnabledChanged?.Invoke(value);
        }
    }
    public event Action<bool>? EnabledChanged;

    IntPtr kbHook, mouseHook, fgHook;
    readonly LowLevelProc kbProc, mouseProc;
    readonly WinEventProc fgProc;
    readonly bool[] keyDown = new bool[256];
    Dictionary<int, RemapRule> keyRules = new();
    Dictionary<string, RemapRule> mouseRules = new();
    readonly Dictionary<string, List<int>> activeCombos = new();
    readonly HashSet<string> swallowedUps = new();

    public Engine(AppConfig cfg)
    {
        Config = cfg;
        enabled = cfg.Enabled;
        Active = cfg.Profiles.FirstOrDefault(p => p.Name == cfg.ActiveProfile) ?? cfg.Profiles[0];
        RebuildRules();
        kbProc = KbProc; mouseProc = MouseProc; fgProc = FgProc;
        var mod = GetModuleHandle(null);
        kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, kbProc, mod, 0);
        mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, mod, 0);
        fgHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, fgProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        if (kbHook == IntPtr.Zero || mouseHook == IntPtr.Zero) Log.Write("hook install failed: " + Marshal.GetLastWin32Error());
    }

    // ---------------- profiles ----------------
    public void SetActive(string name, bool save = true)
    {
        var p = Config.Profiles.FirstOrDefault(x => x.Name == name);
        if (p == null || p == Active) { if (p != null) RebuildRules(); return; }
        Macros.StopAll(); ReleaseAllRemaps();
        Active = p;
        Config.ActiveProfile = p.Name;
        RebuildRules();
        if (save) ConfigStore.Save(Config);
        ProfileChanged?.Invoke(p);
    }

    /// <summary>Call after the UI edited the active profile.</summary>
    public void ProfileEdited() { RebuildRules(); ProfileChanged?.Invoke(Active); }

    void FgProc(IntPtr hook, uint evt, IntPtr hwnd, int idObj, int idChild, uint thread, uint time)
    {
        if (!Config.AutoSwitch) return;
        try
        {
            var exe = ProcessNameOf(hwnd);
            if (exe == null || exe == "inputfx") return;
            var match = Config.Profiles.FirstOrDefault(p => p.Apps.Any(a => string.Equals(a.Trim().Replace(".exe", ""), exe, StringComparison.OrdinalIgnoreCase)))
                        ?? Config.Profiles.First(p => p.IsDefault);
            if (match != Active) SetActive(match.Name, false);
        }
        catch (Exception e) { Log.Write("fg: " + e.Message); }
    }

    // ---------------- remap rules ----------------
    void RebuildRules()
    {
        var k = new Dictionary<int, RemapRule>();
        var m = new Dictionary<string, RemapRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Active.Remaps)
        {
            if (!r.Enabled || string.IsNullOrWhiteSpace(r.Source)) continue;
            if (r.Source.StartsWith("Mouse", StringComparison.OrdinalIgnoreCase)) m[r.Source] = r;
            else { int vk = Keys.Parse(r.Source); if (vk != 0) k[vk] = r; }
        }
        keyRules = k; mouseRules = m;
    }

    void ReleaseAllRemaps()
    {
        foreach (var c in activeCombos.Values.ToList()) Injector.Combo(c, false);
        activeCombos.Clear();
        swallowedUps.Clear();
    }

    /// <summary>Returns true when the original event must be swallowed.</summary>
    bool HandleRemap(RemapRule? r, string id, bool down, bool repeat)
    {
        if (r == null)
        {
            // key-up of something we swallowed on key-down
            if (!down && swallowedUps.Remove(id)) return true;
            return false;
        }
        switch (r.Action)
        {
            case "Block": return true;
            case "Key":
                {
                    if (down)
                    {
                        if (!activeCombos.TryGetValue(id, out var combo)) { combo = Keys.ParseCombo(r.Target); if (combo.Count == 0) return false; activeCombos[id] = combo; Injector.Combo(combo, true); }
                        else if (repeat && combo.Count > 0) Injector.Key(combo[^1], true);
                    }
                    else if (activeCombos.Remove(id, out var combo)) Injector.Combo(combo, false);
                    return true;
                }
            case "Text":
                if (down && !repeat) Injector.Text(r.Target);
                return true;
            case "Run":
                if (down && !repeat) Injector.Run(r.Target);
                return true;
            case "Profile":
                if (down && !repeat) { var name = r.Target; System.Windows.Application.Current.Dispatcher.BeginInvoke(() => SetActive(name)); }
                return true;
            case "Macro":
                {
                    var m = Active.Macros.FirstOrDefault(x => string.Equals(x.Name, r.Target, StringComparison.OrdinalIgnoreCase));
                    if (m == null) return true;
                    if ((m.Mode != "Once" || m.Repeat > 1) && MacroGuard != null && MacroGuard()) { Notice?.Invoke("Repeating macros stay off while a game is running."); return true; }
                    if (down && !repeat)
                    {
                        if (m.Mode == "Toggle") { if (Macros.IsRunning(m.Name)) Macros.Stop(m.Name); else Macros.Start(m); }
                        else Macros.Start(m);
                    }
                    else if (!down && m.Mode == "WhileHeld") Macros.Stop(m.Name);
                    return true;
                }
        }
        return false;
    }

    // ---------------- hooks ----------------
    IntPtr KbProc(int n, IntPtr w, IntPtr l)
    {
        if (n < 0) return CallNextHookEx(kbHook, n, w, l);
        try
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(l);
            if (k.dwExtraInfo == Injector.Marker || k.vkCode == 0xE7 || k.vkCode > 255) return CallNextHookEx(kbHook, n, w, l);
            int msg = (int)w;
            bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            int vk = (int)k.vkCode;
            bool repeat = down && keyDown[vk];
            keyDown[vk] = down;

            if (down && vk == 0x7B && (GetAsyncKeyState(0x11) & 0x8000) != 0 && (GetAsyncKeyState(0x12) & 0x8000) != 0 && (GetAsyncKeyState(0x10) & 0x8000) != 0)
            {
                Enabled = !Enabled;
                return (IntPtr)1;
            }
            if (!enabled) return CallNextHookEx(kbHook, n, w, l);

            if (AutoKeyFilter != null && AutoKeyFilter(vk, down, repeat)) return (IntPtr)1;
            if (GameKeyFilter != null && GameKeyFilter(vk, down)) return (IntPtr)1;
            if (down) Pressed?.Invoke(new InputEvt(InputKind.Key, vk, "", repeat, 0, 0));
            keyRules.TryGetValue(vk, out var rule);
            if (HandleRemap(rule, "k" + vk, down, repeat)) return (IntPtr)1;
        }
        catch (Exception e) { Log.Write("kb hook: " + e.Message); }
        return CallNextHookEx(kbHook, n, w, l);
    }

    IntPtr MouseProc(int n, IntPtr w, IntPtr l)
    {
        if (n < 0) return CallNextHookEx(mouseHook, n, w, l);
        try
        {
            var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(l);
            if (m.dwExtraInfo == Injector.Marker || !enabled) return CallNextHookEx(mouseHook, n, w, l);
            int msg = (int)w;
            string? btn = null; bool down = false, isBtn = true;
            switch (msg)
            {
                case WM_LBUTTONDOWN: btn = "Left"; down = true; break;
                case WM_LBUTTONUP: btn = "Left"; break;
                case WM_RBUTTONDOWN: btn = "Right"; down = true; break;
                case WM_RBUTTONUP: btn = "Right"; break;
                case WM_MBUTTONDOWN: btn = "Middle"; down = true; break;
                case WM_MBUTTONUP: btn = "Middle"; break;
                case WM_XBUTTONDOWN: btn = (m.mouseData >> 16) == 1 ? "X1" : "X2"; down = true; break;
                case WM_XBUTTONUP: btn = (m.mouseData >> 16) == 1 ? "X1" : "X2"; break;
                case WM_MOUSEWHEEL: isBtn = false; Pressed?.Invoke(new InputEvt(InputKind.Wheel, 0, "Wheel", false, m.pt.X, m.pt.Y)); break;
                default: isBtn = false; break;
            }
            if (isBtn && btn != null)
            {
                if (down) Pressed?.Invoke(new InputEvt(InputKind.Mouse, 0, btn, false, m.pt.X, m.pt.Y));
                if (btn is "Middle" or "X1" or "X2")
                {
                    string src = "Mouse" + btn;
                    mouseRules.TryGetValue(src, out var rule);
                    bool wasSwallowed = false;
                    if (rule != null) wasSwallowed = HandleRemap(rule, src, down, false);
                    if (rule != null)
                    {
                        if (down) swallowedUps.Add(src);
                        else swallowedUps.Remove(src);
                        if (wasSwallowed) return (IntPtr)1;
                    }
                    else if (!down && swallowedUps.Remove(src)) return (IntPtr)1;
                }
            }
        }
        catch (Exception e) { Log.Write("mouse hook: " + e.Message); }
        return CallNextHookEx(mouseHook, n, w, l);
    }

    public void Dispose()
    {
        Macros.StopAll(); ReleaseAllRemaps();
        if (kbHook != IntPtr.Zero) UnhookWindowsHookEx(kbHook);
        if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
        if (fgHook != IntPtr.Zero) UnhookWinEvent(fgHook);
        kbHook = mouseHook = fgHook = IntPtr.Zero;
    }
}
