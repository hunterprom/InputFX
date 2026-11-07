using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace InputFX;

/// <summary>
/// Decides when automation (auto clicker, turbo, repeaters, repeating macros) must stay off: when a game, a game launcher's library, an
/// anti-cheat or a full-screen game is running. This protects people from bans and keeps InputFX out of competitive play. The built-in
/// rules cannot be switched off; users can only add more programs to the list.
/// </summary>
public sealed class GameGuard
{
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);

    static readonly string[] AntiCheat =
    {
        "easyanticheat", "easyanticheat_eos", "easyanticheat_launcher", "beservice", "battleye", "vgc", "vgtray", "faceitclient", "faceit", "esea",
        "eaanticheat", "xigncode", "x3", "nprotect", "gameguard", "mhyprot", "vanguard", "equ8", "ricochet",
    };

    static readonly string[] Games =
    {
        "valorant", "valorant-win64-shipping", "cs2", "csgo", "fortniteclient-win64-shipping", "fortnite", "r5apex", "overwatch", "league of legends",
        "leagueclient", "dota2", "rocketleague", "pubg", "tslgame", "cod", "modernwarfare", "blackops", "warzone", "rainbowsix", "rainbowsix_vulkan",
        "destiny2", "escapefromtarkov", "rustclient", "gta5", "fivem", "marvelrivals", "deadlock", "thefinals", "bf2042", "battlefield", "eldenring",
        "apexlegends", "roblox", "robloxplayerbeta", "minecraft", "javaw_minecraft", "forhonor", "foh", "acs", "ac2", "hellishquart",
    };

    static readonly string[] LibraryFolders =
    {
        @"\steamapps\common\", @"\epic games\", @"\riot games\", @"\ubisoft game launcher\games\", @"\gog galaxy\games\", @"\battle.net\", @"\origin games\", @"\ea games\",
    };

    public List<string> Extra { get; set; } = new();
    public bool Locked { get; private set; }
    public string Reason { get; private set; } = "";
    DateTime nextCheck = DateTime.MinValue;

    /// <summary>Cheap to call often: re-evaluates at most every 700 ms.</summary>
    public bool Check()
    {
        if (DateTime.UtcNow < nextCheck) return Locked;
        nextCheck = DateTime.UtcNow.AddMilliseconds(700);
        string why = "";
        try
        {
            if (SHQueryUserNotificationState(out int st) == 0 && st == 3) why = "a full-screen game is running";
            var fg = Native.GetForegroundWindow();
            var name = Native.ProcessNameOf(fg);
            if (why == "" && name != null)
            {
                if (Games.Contains(name) || Extra.Any(x => string.Equals(x.Trim().Replace(".exe", ""), name, StringComparison.OrdinalIgnoreCase))) why = $"'{name}' is on the game list";
                else
                {
                    var path = Native.ProcessPathOf(fg)?.ToLowerInvariant();
                    if (path != null && LibraryFolders.Any(f => path.Contains(f))) why = $"'{name}' looks like a game (game library folder)";
                }
            }
            if (why == "")
                foreach (var p in Process.GetProcesses())
                {
                    try { if (AntiCheat.Contains(p.ProcessName.ToLowerInvariant())) { why = $"anti-cheat '{p.ProcessName}' is running"; break; } } catch { }
                    finally { p.Dispose(); }
                }
        }
        catch (Exception e) { Log.Write("guard: " + e.Message); }
        Locked = why != ""; Reason = why;
        return Locked;
    }
}

public class AutoTool
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "Auto clicker";
    /// <summary>Click or Key</summary>
    public string Kind { get; set; } = "Click";
    /// <summary>Left, Right, Middle for clicks; a key name (E, Space...) for Key.</summary>
    public string Target { get; set; } = "Left";
    public int Cps { get; set; } = 10;
    public string Hotkey { get; set; } = "F6";
    /// <summary>Toggle or Hold</summary>
    public string Mode { get; set; } = "Toggle";
    /// <summary>Stop after this many presses, 0 = keep going.</summary>
    public int Limit { get; set; } = 0;
}

/// <summary>Runs the auto tools. Never starts while the game guard says locked, and stops the moment it becomes locked.</summary>
public sealed class AutoRunner
{
    public GameGuard Guard { get; }
    public event Action<string>? Notice;
    readonly Dictionary<AutoTool, CancellationTokenSource> running = new();
    readonly object gate = new();
    Func<IEnumerable<AutoTool>> tools = () => Array.Empty<AutoTool>();

    public AutoRunner(GameGuard g) { Guard = g; }
    public void SetSource(Func<IEnumerable<AutoTool>> f) => tools = f;

    public bool Active { get { lock (gate) return running.Count > 0; } }

    /// <summary>Returns true when the key was one of our hotkeys (so it is swallowed).</summary>
    public bool Key(int vk, bool down, bool repeat)
    {
        AutoTool? hit = null;
        foreach (var t in tools()) if (t.Enabled && Keys.Parse(t.Hotkey) == vk) { hit = t; break; }
        if (hit == null) return false;
        if (repeat) return true;
        if (hit.Mode == "Hold") { if (down) Start(hit); else Stop(hit); }
        else if (down) { if (IsRunning(hit)) Stop(hit); else Start(hit); }
        return true;
    }

    bool IsRunning(AutoTool t) { lock (gate) return running.ContainsKey(t); }

    void Start(AutoTool t)
    {
        if (Guard.Check()) { Notice?.Invoke($"{t.Name} stays off: {Guard.Reason}."); return; }
        lock (gate)
        {
            if (running.ContainsKey(t)) return;
            var cts = new CancellationTokenSource(); running[t] = cts;
            Task.Run(() => Loop(t, cts.Token)).ContinueWith(_ => { lock (gate) running.Remove(t); });
        }
        Notice?.Invoke($"{t.Name} ON ({t.Target}, {Math.Clamp(t.Cps, 1, 30)} per second)");
    }

    public void Stop(AutoTool t) { lock (gate) { if (running.TryGetValue(t, out var c)) c.Cancel(); } Notice?.Invoke(t.Name + " off"); }
    public void StopAll() { lock (gate) foreach (var c in running.Values) c.Cancel(); }

    void Loop(AutoTool t, CancellationToken ct)
    {
        int cps = Math.Clamp(t.Cps, 1, 30);
        double interval = 1000.0 / cps;
        var sw = Stopwatch.StartNew();
        double next = 0; int count = 0;
        int vk = t.Kind == "Key" ? Keys.Parse(t.Target) : 0;
        while (!ct.IsCancellationRequested)
        {
            if (Guard.Check()) { Notice?.Invoke($"{t.Name} stopped: {Guard.Reason}."); break; }
            if (sw.Elapsed.TotalMilliseconds >= next)
            {
                if (t.Kind == "Key") { if (vk != 0) { Injector.Key(vk, true); Thread.Sleep(8); Injector.Key(vk, false); } }
                else { Injector.MouseButton(t.Target, true); Thread.Sleep(6); Injector.MouseButton(t.Target, false); }
                count++; next += interval;
                if (t.Limit > 0 && count >= t.Limit) { Notice?.Invoke($"{t.Name} finished ({count})"); break; }
            }
            else ct.WaitHandle.WaitOne(1);
        }
    }
}
