using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using static InputFX.Native;

namespace InputFX;

/// <summary>Key names, parsing and synthetic input.</summary>
public static class Keys
{
    static readonly Dictionary<string, int> Alias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0xA2, ["control"] = 0xA2, ["lctrl"] = 0xA2, ["rctrl"] = 0xA3,
        ["alt"] = 0xA4, ["lalt"] = 0xA4, ["ralt"] = 0xA5, ["altgr"] = 0xA5,
        ["shift"] = 0xA0, ["lshift"] = 0xA0, ["rshift"] = 0xA1,
        ["win"] = 0x5B, ["lwin"] = 0x5B, ["rwin"] = 0x5C, ["windows"] = 0x5B,
        ["esc"] = 0x1B, ["escape"] = 0x1B, ["enter"] = 0x0D, ["return"] = 0x0D, ["space"] = 0x20, ["tab"] = 0x09,
        ["caps"] = 0x14, ["capslock"] = 0x14, ["backspace"] = 0x08, ["bksp"] = 0x08, ["del"] = 0x2E, ["delete"] = 0x2E,
        ["ins"] = 0x2D, ["insert"] = 0x2D, ["home"] = 0x24, ["end"] = 0x23, ["pgup"] = 0x21, ["pageup"] = 0x21,
        ["pgdn"] = 0x22, ["pagedown"] = 0x22, ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
        ["printscreen"] = 0x2C, ["prtsc"] = 0x2C, ["pause"] = 0x13, ["scrolllock"] = 0x91, ["numlock"] = 0x90,
        ["menu"] = 0x5D, ["apps"] = 0x5D,
        ["volumeup"] = 0xAF, ["volumedown"] = 0xAE, ["mute"] = 0xAD, ["volumemute"] = 0xAD,
        ["playpause"] = 0xB3, ["nexttrack"] = 0xB0, ["prevtrack"] = 0xB1, ["stop"] = 0xB2,
        [";"] = 0xBA, ["="] = 0xBB, [","] = 0xBC, ["-"] = 0xBD, ["."] = 0xBE, ["/"] = 0xBF, ["`"] = 0xC0,
        ["["] = 0xDB, ["\\"] = 0xDC, ["]"] = 0xDD, ["'"] = 0xDE,
    };

    public static readonly string[] MouseSources = { "MouseMiddle", "MouseX1", "MouseX2" };

    public static int Parse(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;
        name = name.Trim();
        if (Alias.TryGetValue(name, out int a)) return a;
        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z') return c;
            if (c is >= '0' and <= '9') return c;
        }
        if (name.StartsWith("num", StringComparison.OrdinalIgnoreCase) && name.Length == 4 && char.IsDigit(name[3]))
            return 0x60 + (name[3] - '0');
        if (name.StartsWith("f", StringComparison.OrdinalIgnoreCase) && int.TryParse(name.AsSpan(1), out int fn) && fn is >= 1 and <= 24)
            return 0x70 + fn - 1;
        if (Enum.TryParse<Key>(name, true, out var key))
        {
            int v = KeyInterop.VirtualKeyFromKey(key);
            if (v != 0) return v;
        }
        return 0;
    }

    public static string NameOf(int vk)
    {
        foreach (var kv in Alias) if (kv.Value == vk && kv.Key.Length > 1 && !kv.Key.StartsWith('l') && !kv.Key.StartsWith('r')) return Cap(kv.Key);
        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= 0x60 and <= 0x69) return "Num" + (vk - 0x60);
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F);
        var k = KeyInterop.KeyFromVirtualKey(vk);
        return k == Key.None ? $"VK{vk:X2}" : k.ToString();
    }

    static string Cap(string s) => char.ToUpperInvariant(s[0]) + s[1..];

    public static bool IsModifier(int vk) => vk is 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C or 0x10 or 0x11 or 0x12;

    public static List<int> ParseCombo(string combo)
    {
        var res = new List<int>();
        foreach (var part in combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int v = Parse(part);
            if (v != 0) res.Add(v);
        }
        // "+" itself (e.g. "Ctrl++") is not supported; use the Text action instead.
        return res;
    }

    public static string Friendly(string? s) => string.IsNullOrWhiteSpace(s) ? "?" : s;
}

public static class Injector
{
    public static readonly UIntPtr Marker = (UIntPtr)0x49465831;
    static readonly HashSet<int> Extended = new() { 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x5B, 0x5C, 0x5D, 0x6F, 0x90, 0xA3, 0xA5, 0x2C };

    public static void Key(int vk, bool down)
    {
        if (vk == 0) return;
        var inp = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)vk,
                    wScan = (ushort)MapVirtualKey((uint)vk, 0),
                    dwFlags = (Extended.Contains(vk) ? KEYEVENTF_EXTENDEDKEY : 0) | (down ? 0u : KEYEVENTF_KEYUP),
                    dwExtraInfo = Marker
                }
            }
        };
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    public static void Combo(IReadOnlyList<int> vks, bool down)
    {
        if (down) foreach (var v in vks) Key(v, true);
        else for (int i = vks.Count - 1; i >= 0; i--) Key(vks[i], false);
    }

    public static void Press(IReadOnlyList<int> vks) { Combo(vks, true); Combo(vks, false); }

    public static void Text(string s)
    {
        foreach (char c in s)
        {
            if (c == '\r') continue;
            if (c == '\n') { Press(new[] { 0x0D }); continue; }
            var down = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE, dwExtraInfo = Marker } } };
            var up = down; up.U.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
            SendInput(2, new[] { down, up }, Marshal.SizeOf<INPUT>());
        }
    }

    public static void MouseButton(string btn, bool down)
    {
        uint flags = 0, data = 0;
        switch (btn.ToLowerInvariant())
        {
            case "left": flags = down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP; break;
            case "right": flags = down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP; break;
            case "middle": flags = down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP; break;
            case "x1": flags = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; data = 1; break;
            case "x2": flags = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; data = 2; break;
            default: return;
        }
        var inp = new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, mouseData = data, dwExtraInfo = Marker } } };
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    public static void Wheel(int notches)
    {
        var inp = new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_WHEEL, mouseData = unchecked((uint)(notches * 120)), dwExtraInfo = Marker } } };
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    public static void Run(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.Length == 0) return;
        try
        {
            string file = cmd, args = "";
            if (cmd.StartsWith('"')) { int e = cmd.IndexOf('"', 1); if (e > 0) { file = cmd[1..e]; args = cmd[(e + 1)..].Trim(); } }
            else { int sp = cmd.IndexOf(' '); if (sp > 0 && !System.IO.File.Exists(cmd)) { file = cmd[..sp]; args = cmd[(sp + 1)..]; } }
            Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
        }
        catch (Exception e) { Log.Write("run failed: " + cmd + " : " + e.Message); }
    }
}

/// <summary>Runs macro scripts. One instance per engine; macros run on thread-pool tasks.</summary>
public class MacroRunner
{
    readonly Dictionary<string, CancellationTokenSource> running = new();
    readonly object gate = new();

    public bool IsRunning(string name) { lock (gate) return running.ContainsKey(name); }

    public void Start(MacroDef m)
    {
        lock (gate)
        {
            if (running.ContainsKey(m.Name)) return;
            var cts = new CancellationTokenSource();
            running[m.Name] = cts;
            Task.Run(() => Run(m, cts.Token)).ContinueWith(_ => { lock (gate) running.Remove(m.Name); });
        }
    }

    public void Stop(string name) { lock (gate) { if (running.TryGetValue(name, out var c)) c.Cancel(); } }
    public void StopAll() { lock (gate) foreach (var c in running.Values) c.Cancel(); }

    void Run(MacroDef m, CancellationToken ct)
    {
        var lines = m.Script.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        int loops = m.Mode == "Once" ? Math.Clamp(m.Repeat, 1, 10000) : int.MaxValue;
        var held = new List<IReadOnlyList<int>>();
        try
        {
            for (int i = 0; i < loops && !ct.IsCancellationRequested; i++)
            {
                bool hadDelay = false;
                foreach (var line in lines)
                {
                    if (ct.IsCancellationRequested) break;
                    int sp = line.IndexOf(' ');
                    string cmd = (sp < 0 ? line : line[..sp]).ToLowerInvariant();
                    string arg = sp < 0 ? "" : line[(sp + 1)..].Trim();
                    switch (cmd)
                    {
                        case "press": Injector.Press(Keys.ParseCombo(arg)); break;
                        case "down": { var c = Keys.ParseCombo(arg); Injector.Combo(c, true); held.Add(c); break; }
                        case "up": { var c = Keys.ParseCombo(arg); Injector.Combo(c, false); break; }
                        case "text": Injector.Text(arg); break;
                        case "delay": case "wait": case "sleep":
                            hadDelay = true;
                            if (int.TryParse(arg, out int ms)) ct.WaitHandle.WaitOne(Math.Clamp(ms, 0, 600000));
                            break;
                        case "click": Injector.MouseButton(arg, true); Injector.MouseButton(arg, false); break;
                        case "mousedown": Injector.MouseButton(arg, true); break;
                        case "mouseup": Injector.MouseButton(arg, false); break;
                        case "wheel": if (int.TryParse(arg, out int n)) Injector.Wheel(n); break;
                        case "run": Injector.Run(arg); break;
                    }
                }
                if (!hadDelay) ct.WaitHandle.WaitOne(10);
            }
        }
        catch (Exception e) { Log.Write("macro error: " + e.Message); }
        finally { foreach (var c in held) Injector.Combo(c, false); }
    }
}
