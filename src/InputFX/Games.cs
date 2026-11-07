using System.Diagnostics;

namespace InputFX;

public enum Btn { Up, Down, Left, Right, Action }

public struct Px
{
    public double R, G, B;
    public Px(double r, double g, double b) { R = r; G = g; B = b; }
    public static readonly Px Dark = new(0.02, 0.02, 0.04);
    public Px Scale(double s) => new(R * s, G * s, B * s);
}

/// <summary>A tiny game drawn on the keyboard's 14 x 5 block of keys.</summary>
public abstract class Game
{
    public const int W = 14, H = 5;
    protected readonly Random rng = new();
    public abstract string Name { get; }
    public abstract string Help { get; }
    public virtual int TickMs => 200;
    public int Score { get; protected set; }
    public bool Over { get; protected set; }
    public abstract void Reset();
    public abstract void Step();
    public abstract void Draw(Px[,] g);
    public abstract void Press(Btn b);

    protected void Clear(Px[,] g, Px c) { for (int x = 0; x < W; x++) for (int y = 0; y < H; y++) g[x, y] = c; }
    protected void Set(Px[,] g, int x, int y, Px c) { if (x >= 0 && x < W && y >= 0 && y < H) g[x, y] = c; }
    protected bool Blink(int period = 3) => (Environment.TickCount64 / (TickMs * period)) % 2 == 0;
    protected void OverFlash(Px[,] g) { if (Blink(2)) for (int x = 0; x < W; x++) for (int y = 0; y < H; y++) if ((x + y) % 2 == 0) g[x, y] = new Px(.7, 0, 0); }
}

public sealed class Snake : Game
{
    public override string Name => "Snake";
    public override string Help => "Arrow keys or WASD to steer. Eat the red dots. Space restarts.";
    public override int TickMs => 230;
    readonly List<(int x, int y)> body = new();
    (int x, int y) food, dir, next;

    public override void Reset()
    {
        body.Clear(); body.Add((5, 2)); body.Add((4, 2)); body.Add((3, 2));
        dir = next = (1, 0); Score = 0; Over = false; PlaceFood();
    }

    void PlaceFood()
    {
        for (int i = 0; i < 200; i++)
        {
            var f = (rng.Next(W), rng.Next(H));
            if (!body.Contains(f)) { food = f; return; }
        }
    }

    public override void Press(Btn b)
    {
        if (Over) { if (b == Btn.Action) Reset(); return; }
        var d = b switch { Btn.Up => (0, -1), Btn.Down => (0, 1), Btn.Left => (-1, 0), Btn.Right => (1, 0), _ => dir };
        if (d.Item1 != -dir.x || d.Item2 != -dir.y) next = d;
    }

    public override void Step()
    {
        if (Over) return;
        dir = next;
        var h = body[0];
        var nh = ((h.x + dir.x + W) % W, (h.y + dir.y + H) % H);
        if (body.Contains(nh) && nh != body[^1]) { Over = true; return; }
        body.Insert(0, nh);
        if (nh == food) { Score++; PlaceFood(); }
        else body.RemoveAt(body.Count - 1);
    }

    public override void Draw(Px[,] g)
    {
        Clear(g, Px.Dark);
        Set(g, food.x, food.y, Blink(1) ? new Px(1, .1, .1) : new Px(.6, 0, 0));
        for (int i = body.Count - 1; i >= 0; i--) Set(g, body[i].x, body[i].y, i == 0 ? new Px(.5, 1, .2) : new Px(0, .55 - Math.Min(.3, i * .02), .1));
        if (Over) OverFlash(g);
    }
}

public sealed class Flappy : Game
{
    public override string Name => "Flappy Bird";
    public override string Help => "Space or Up to flap. Fly through the gaps in the green pipes.";
    public override int TickMs => 120;
    double y, vy; int tick;
    readonly List<(int x, int gap, bool scored)> pipes = new();

    public override void Reset() { y = 2; vy = 0; tick = 0; pipes.Clear(); Score = 0; Over = false; }

    public override void Press(Btn b)
    {
        if (Over) { if (b == Btn.Action) Reset(); return; }
        if (b is Btn.Action or Btn.Up) vy = -0.85;
    }

    public override void Step()
    {
        if (Over) return;
        tick++;
        vy = Math.Min(0.7, vy + 0.22); y += vy;
        if (y < 0) { y = 0; vy = 0; }
        if (y > H - 0.5) { Over = true; return; }
        if (tick % 2 == 0)
        {
            for (int i = 0; i < pipes.Count; i++) pipes[i] = (pipes[i].x - 1, pipes[i].gap, pipes[i].scored);
            pipes.RemoveAll(p => p.x < -1);
            if (pipes.Count == 0 || pipes[^1].x <= W - 6) pipes.Add((W, rng.Next(0, H - 1), false));
        }
        int by = (int)Math.Round(y);
        for (int i = 0; i < pipes.Count; i++)
        {
            var p = pipes[i];
            if (p.x == 3 && (by < p.gap || by > p.gap + 1)) Over = true;
            if (!p.scored && p.x < 3) { pipes[i] = (p.x, p.gap, true); Score++; }
        }
    }

    public override void Draw(Px[,] g)
    {
        Clear(g, new Px(.0, .04, .12));
        foreach (var p in pipes)
            for (int row = 0; row < H; row++)
                if (row < p.gap || row > p.gap + 1) Set(g, p.x, row, new Px(.1, .8, .15));
        Set(g, 3, (int)Math.Round(y), new Px(1, .85, 0));
        if (Over) OverFlash(g);
    }
}

public sealed class Pong : Game
{
    public override string Name => "Pong";
    public override string Help => "Up/Down (or W/S) moves your paddle on the left. First to lose 3 balls loses.";
    public override int TickMs => 150;
    int py, ay, bx, by, dx, dy, lives;

    public override void Reset() { py = 1; ay = 1; Serve(); Score = 0; lives = 3; Over = false; }

    void Serve() { bx = 6; by = rng.Next(1, H - 1); dx = rng.Next(2) == 0 ? -1 : 1; dy = rng.Next(2) == 0 ? -1 : 1; }

    public override void Press(Btn b)
    {
        if (Over) { if (b == Btn.Action) Reset(); return; }
        if (b == Btn.Up) py = Math.Max(0, py - 1);
        if (b == Btn.Down) py = Math.Min(H - 2, py + 1);
    }

    public override void Step()
    {
        if (Over) return;
        if (rng.NextDouble() < .72) { if (by < ay) ay = Math.Max(0, ay - 1); else if (by > ay + 1) ay = Math.Min(H - 2, ay + 1); }
        int nx = bx + dx, ny = by + dy;
        if (ny < 0 || ny >= H) { dy = -dy; ny = by + dy; }
        if (nx <= 0) { if (ny >= py && ny <= py + 1) { dx = 1; nx = 1; } else { lives--; if (lives <= 0) { Over = true; return; } Serve(); return; } }
        if (nx >= W - 1) { if (ny >= ay && ny <= ay + 1) { dx = -1; nx = W - 2; } else { Score++; Serve(); return; } }
        bx = nx; by = ny;
    }

    public override void Draw(Px[,] g)
    {
        Clear(g, Px.Dark);
        for (int i = 0; i < 2; i++) { Set(g, 0, py + i, new Px(.2, .8, 1)); Set(g, W - 1, ay + i, new Px(1, .4, .2)); }
        Set(g, bx, by, new Px(1, 1, 1));
        for (int l = 0; l < lives; l++) Set(g, 5 + l, 0, new Px(.8, 0, 0).Scale(.4));
        if (Over) OverFlash(g);
    }
}

public sealed class Breakout : Game
{
    public override string Name => "Breakout";
    public override string Help => "Left/Right (or A/D) moves the paddle on the bottom row. Break every brick.";
    public override int TickMs => 130;
    int px, bx, by, dx, dy, lives;
    bool[,] bricks = new bool[W, 2];

    public override void Reset() { px = 5; Score = 0; lives = 3; Over = false; Fill(); Serve(); }
    void Fill() { for (int x = 0; x < W; x++) for (int y = 0; y < 2; y++) bricks[x, y] = true; }
    void Serve() { bx = px + 1; by = H - 2; dx = rng.Next(2) == 0 ? -1 : 1; dy = -1; }

    public override void Press(Btn b)
    {
        if (Over) { if (b == Btn.Action) Reset(); return; }
        if (b == Btn.Left) px = Math.Max(0, px - 1);
        if (b == Btn.Right) px = Math.Min(W - 3, px + 1);
    }

    public override void Step()
    {
        if (Over) return;
        int nx = bx + dx, ny = by + dy;
        if (nx < 0 || nx >= W) { dx = -dx; nx = bx + dx; }
        if (ny < 0) { dy = 1; ny = by + dy; }
        if (ny < 2 && nx >= 0 && nx < W && bricks[nx, ny]) { bricks[nx, ny] = false; Score++; dy = -dy; ny = by + dy; if (ny < 0) ny = 0; }
        if (ny >= H - 1)
        {
            if (nx >= px - 0 && nx <= px + 2) { dy = -1; dx = nx == px ? -1 : nx == px + 2 ? 1 : dx; ny = H - 2; }
            else { lives--; if (lives <= 0) { Over = true; return; } Serve(); return; }
        }
        bx = nx; by = ny;
        bool any = false; for (int x = 0; x < W; x++) for (int y = 0; y < 2; y++) any |= bricks[x, y];
        if (!any) { Fill(); Serve(); }
    }

    public override void Draw(Px[,] g)
    {
        Clear(g, Px.Dark);
        for (int x = 0; x < W; x++) for (int y = 0; y < 2; y++) if (bricks[x, y]) Set(g, x, y, y == 0 ? new Px(.9, .2, .2) : new Px(.95, .6, .1));
        for (int i = 0; i < 3; i++) Set(g, px + i, H - 1, new Px(.2, .8, 1));
        Set(g, bx, by, new Px(1, 1, 1));
        if (Over) OverFlash(g);
    }
}

public sealed class Dino : Game
{
    public override string Name => "Dino Run";
    public override string Help => "Space or Up to jump over the cacti. It speeds up as you go.";
    public override int TickMs => 110;
    int jumpT = -1, tick;
    readonly List<(int x, int h, bool scored)> cacti = new();
    static readonly int[] Arc = { 1, 2, 2, 2, 2, 1, 1 };

    public override void Reset() { jumpT = -1; tick = 0; cacti.Clear(); Score = 0; Over = false; }

    public override void Press(Btn b)
    {
        if (Over) { if (b == Btn.Action) Reset(); return; }
        if ((b is Btn.Action or Btn.Up) && jumpT < 0) jumpT = 0;
    }

    int Height => jumpT < 0 ? 0 : Arc[Math.Min(jumpT, Arc.Length - 1)];

    public override void Step()
    {
        if (Over) return;
        tick++;
        if (jumpT >= 0) { jumpT++; if (jumpT >= Arc.Length) jumpT = -1; }
        int speedGate = Score > 12 ? 1 : 2;
        if (tick % speedGate == 0)
        {
            for (int i = 0; i < cacti.Count; i++) cacti[i] = (cacti[i].x - 1, cacti[i].h, cacti[i].scored);
            cacti.RemoveAll(c => c.x < 0);
            if ((cacti.Count == 0 || cacti[^1].x <= W - 5 - rng.Next(0, 3)) && rng.NextDouble() < .6) cacti.Add((W - 1, rng.Next(1, 3), false));
        }
        for (int i = 0; i < cacti.Count; i++)
        {
            var c = cacti[i];
            if (c.x == 2 && Height < c.h) Over = true;
            if (!c.scored && c.x < 2) { cacti[i] = (c.x, c.h, true); Score++; }
        }
    }

    public override void Draw(Px[,] g)
    {
        Clear(g, new Px(.05, .05, .08));
        for (int x = 0; x < W; x++) Set(g, x, H - 1, new Px(.25, .18, .1));
        foreach (var c in cacti) for (int h = 0; h < c.h; h++) Set(g, c.x, H - 2 - h, new Px(.9, .45, .1));
        Set(g, 2, H - 2 - Height, new Px(.3, 1, .3));
        if (Over) OverFlash(g);
    }
}

public sealed class PacMan : Game
{
    public override string Name => "Pac-Man";
    public override string Help => "Arrow keys or WASD. Eat every dot and avoid the ghosts.";
    public override int TickMs => 190;
    static readonly string[] Maze =
    {
        "..............",
        ".##.####.##.#.",
        "..............",
        ".#.####.##.##.",
        "..............",
    };
    bool[,] wall = new bool[W, H], dot = new bool[W, H];
    (int x, int y) pac, dir, want;
    readonly List<(int x, int y, (int dx, int dy) d)> ghosts = new();
    int tick, dotsLeft;

    public override void Reset() { Score = 0; Over = false; Load(); }

    void Load()
    {
        dotsLeft = 0;
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { wall[x, y] = Maze[y][x] == '#'; dot[x, y] = !wall[x, y]; if (dot[x, y]) dotsLeft++; }
        pac = (0, 0); dir = want = (0, 0);
        ghosts.Clear(); ghosts.Add((13, 4, (-1, 0))); ghosts.Add((13, 0, (-1, 0)));
        dot[0, 0] = false; dotsLeft--;
        tick = 0;
    }

    public override void Press(Btn b)
    {
        if (Over) { if (b == Btn.Action) Reset(); return; }
        want = b switch { Btn.Up => (0, -1), Btn.Down => (0, 1), Btn.Left => (-1, 0), Btn.Right => (1, 0), _ => want };
    }

    bool Free(int x, int y) => x >= 0 && x < W && y >= 0 && y < H && !wall[x, y];

    public override void Step()
    {
        if (Over) return;
        tick++;
        if (Free(pac.x + want.x, pac.y + want.y)) dir = want;
        if (Free(pac.x + dir.x, pac.y + dir.y)) pac = (pac.x + dir.x, pac.y + dir.y);
        if (dot[pac.x, pac.y]) { dot[pac.x, pac.y] = false; dotsLeft--; Score++; }
        if (ghosts.Any(g => g.x == pac.x && g.y == pac.y)) { Over = true; return; }
        if (tick % 2 == 0)
            for (int i = 0; i < ghosts.Count; i++)
            {
                var gh = ghosts[i];
                var opts = new List<(int dx, int dy)>();
                foreach (var d in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    if (Free(gh.x + d.Item1, gh.y + d.Item2) && !(d.Item1 == -gh.d.dx && d.Item2 == -gh.d.dy)) opts.Add(d);
                if (opts.Count == 0) opts.Add((-gh.d.dx, -gh.d.dy));
                (int dx, int dy) pick = opts[rng.Next(opts.Count)];
                if (rng.NextDouble() < .65)
                    pick = opts.OrderBy(o => Math.Abs(gh.x + o.dx - pac.x) + Math.Abs(gh.y + o.dy - pac.y)).First();
                ghosts[i] = (gh.x + pick.dx, gh.y + pick.dy, pick);
            }
        if (ghosts.Any(g => g.x == pac.x && g.y == pac.y)) { Over = true; return; }
        if (dotsLeft <= 0) { Score += 10; Load(); }
    }

    public override void Draw(Px[,] g)
    {
        Clear(g, Px.Dark);
        for (int x = 0; x < W; x++) for (int y = 0; y < H; y++)
        {
            if (wall[x, y]) g[x, y] = new Px(0, 0, .55);
            else if (dot[x, y]) g[x, y] = new Px(.35, .28, .08);
        }
        Set(g, pac.x, pac.y, new Px(1, .9, 0));
        for (int i = 0; i < ghosts.Count; i++) Set(g, ghosts[i].x, ghosts[i].y, i == 0 ? new Px(1, 0, 0) : new Px(1, .4, .8));
        if (Over) OverFlash(g);
    }
}

public sealed class DoomLite : Game
{
    public override string Name => "Doom-lite";
    public override string Help => "Left/Right turn, Up/Down move, Space shoots. Kill the green imps before they reach you.";
    public override int TickMs => 140;
    static readonly string[] Map =
    {
        "##########",
        "#........#",
        "#.##..##.#",
        "#.#....#.#",
        "#....#...#",
        "#.#..#.#.#",
        "#.##..##.#",
        "#........#",
        "##########",
    };
    double px, py, ang; int hp, tick;
    readonly List<(double x, double y)> imps = new();
    int flash;

    public override void Reset() { px = 1.5; py = 1.5; ang = 0.7; hp = 5; Score = 0; Over = false; imps.Clear(); tick = 0; flash = 0; Spawn(); Spawn(); }

    void Spawn()
    {
        for (int i = 0; i < 50; i++)
        {
            double x = 1 + rng.NextDouble() * 8, y = 1 + rng.NextDouble() * 7;
            if (Map[(int)y][(int)x] != '#' && Math.Abs(x - px) + Math.Abs(y - py) > 5) { imps.Add((x, y)); return; }
        }
    }

    bool Wall(double x, double y) => Map[(int)y][(int)x] == '#';

    public override void Press(Btn b)
    {
        if (Over) { if (b == Btn.Action) Reset(); return; }
        switch (b)
        {
            case Btn.Left: ang -= 0.32; break;
            case Btn.Right: ang += 0.32; break;
            case Btn.Up: Move(0.35); break;
            case Btn.Down: Move(-0.35); break;
            case Btn.Action: Shoot(); break;
        }
    }

    void Move(double d)
    {
        double nx = px + Math.Cos(ang) * d, ny = py + Math.Sin(ang) * d;
        if (!Wall(nx, py)) px = nx;
        if (!Wall(px, ny)) py = ny;
    }

    void Shoot()
    {
        flash = 2;
        int best = -1; double bd = 99;
        for (int i = 0; i < imps.Count; i++)
        {
            double dx = imps[i].x - px, dy = imps[i].y - py, dist = Math.Sqrt(dx * dx + dy * dy);
            double da = Math.Abs(NormAng(Math.Atan2(dy, dx) - ang));
            if (da < 0.16 + 0.1 / Math.Max(dist, .5) && dist < bd && dist < WallDist(Math.Atan2(dy, dx)) + .3) { bd = dist; best = i; }
        }
        if (best >= 0) { imps.RemoveAt(best); Score++; Spawn(); if (Score % 3 == 0) Spawn(); }
    }

    static double NormAng(double a) { while (a > Math.PI) a -= 2 * Math.PI; while (a < -Math.PI) a += 2 * Math.PI; return a; }

    double WallDist(double a)
    {
        double dx = Math.Cos(a), dy = Math.Sin(a), x = px, y = py;
        for (double t = 0; t < 12; t += 0.05) { x = px + dx * t; y = py + dy * t; if (Wall(x, y)) return t; }
        return 12;
    }

    public override void Step()
    {
        if (Over) return;
        tick++;
        if (flash > 0) flash--;
        if (tick % 3 == 0)
            for (int i = 0; i < imps.Count; i++)
            {
                double dx = px - imps[i].x, dy = py - imps[i].y, d = Math.Sqrt(dx * dx + dy * dy);
                if (d < 0.7) { hp--; imps[i] = (imps[i].x - dx / d * 1.5, imps[i].y - dy / d * 1.5); if (Wall(imps[i].x, imps[i].y)) imps[i] = (px + dx, py + dy); if (hp <= 0) { Over = true; return; } continue; }
                double nx = imps[i].x + dx / d * 0.32, ny = imps[i].y + dy / d * 0.32;
                if (!Wall(nx, imps[i].y)) imps[i] = (nx, imps[i].y);
                if (!Wall(imps[i].x, ny)) imps[i] = (imps[i].x, ny);
            }
    }

    public override void Draw(Px[,] g)
    {
        // ceiling / floor
        for (int x = 0; x < W; x++) for (int y = 0; y < H; y++) g[x, y] = y < 2 ? new Px(.03, .03, .08) : y == 2 ? Px.Dark : new Px(.1, .06, .03);
        var depth = new double[W];
        const double fov = 1.15;
        for (int x = 0; x < W; x++)
        {
            double a = ang + (x / (double)(W - 1) - .5) * fov;
            double d = WallDist(a) * Math.Cos(a - ang);
            depth[x] = d;
            int hgt = (int)Math.Clamp(Math.Round(H / Math.Max(d * 0.55, .4)), 1, H);
            int top = (H - hgt) / 2;
            double shade = Math.Clamp(1.25 / (1 + d * .45), .12, 1);
            var c = new Px(.85, .3, .12).Scale(shade);
            for (int y = top; y < top + hgt; y++) g[x, y] = c;
        }
        foreach (var im in imps)
        {
            double dx = im.x - px, dy = im.y - py, dist = Math.Sqrt(dx * dx + dy * dy);
            double da = NormAng(Math.Atan2(dy, dx) - ang);
            int col = (int)Math.Round((da / 1.15 + .5) * (W - 1));
            if (col < 0 || col >= W || Math.Abs(da) > .62) continue;
            if (dist * Math.Cos(da) > depth[col] + .1) continue;
            int hh = (int)Math.Clamp(Math.Round(3 / Math.Max(dist * .6, .5)), 1, 4);
            for (int y = 3 - hh; y < 3 && y >= 0; y++) g[col, y + 1 > H - 1 ? H - 1 : y + 1] = new Px(.2, 1, .2);
        }
        if (flash > 0) for (int y = 0; y < H; y++) { var c = g[W / 2, y]; g[W / 2, y] = new Px(Math.Min(1, c.R + .7), Math.Min(1, c.G + .7), Math.Min(1, c.B + .2)); }
        for (int i = 0; i < hp; i++) g[i, 0] = new Px(.9, 0, 0);
        if (Over) OverFlash(g);
    }
}

/// <summary>Runs one game, turns its pixels into keyboard LEDs, and swallows the game keys while playing.</summary>
public sealed class GameRunner
{
    public static readonly Func<Game>[] Catalog =
    {
        () => new Snake(), () => new Flappy(), () => new PacMan(), () => new Pong(), () => new Breakout(), () => new Dino(), () => new DoomLite(),
    };

    public Game? Current { get; private set; }
    readonly object gate = new();
    readonly Stopwatch clock = Stopwatch.StartNew();
    double nextTick;
    readonly Px[,] grid = new Px[Game.W, Game.H];
    public event Action? Stopped;
    public Action<int>? ScoreChanged;
    int lastScore = -1;
    public string? HighScoreKey;
    public Action<string, int>? SaveScore;

    public bool Running => Current != null;

    public void Start(Game g)
    {
        lock (gate) { Current = g; g.Reset(); nextTick = clock.Elapsed.TotalMilliseconds + g.TickMs; lastScore = -1; }
    }

    public void Stop()
    {
        Game? g;
        lock (gate) { g = Current; Current = null; }
        if (g != null) { SaveScore?.Invoke(g.Name, g.Score); Stopped?.Invoke(); }
    }

    void Advance()
    {
        var g = Current; if (g == null) return;
        double now = clock.Elapsed.TotalMilliseconds;
        int guard = 0;
        while (now >= nextTick && guard++ < 6) { g.Step(); nextTick += g.TickMs; }
        if (guard >= 6) nextTick = now + g.TickMs;
        if (g.Score != lastScore) { lastScore = g.Score; ScoreChanged?.Invoke(g.Score); }
    }

    /// <summary>Current picture, 14 x 5 (copy).</summary>
    public Px[,]? Snapshot()
    {
        lock (gate)
        {
            if (Current == null) return null;
            Advance();
            Current.Draw(grid);
            return (Px[,])grid.Clone();
        }
    }

    public (double r, double g, double b)[] LedFrame()
    {
        var res = new (double r, double g, double b)[KbLayout.LedCount];
        var pic = Snapshot();
        if (pic == null) return res;
        for (int x = 0; x < Game.W; x++) for (int y = 0; y < Game.H; y++) res[y + 6 * x] = (pic[x, y].R, pic[x, y].G, pic[x, y].B);
        return res;
    }

    /// <summary>Called from the keyboard hook. Returns true when the key was a game key and must not reach other programs.</summary>
    public bool Key(int vk, bool down)
    {
        if (Current == null) return false;
        if ((Native.GetAsyncKeyState(0x11) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x12) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x5B) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x5C) & 0x8000) != 0) return false;
        Btn? b = vk switch
        {
            0x26 or 0x57 => Btn.Up, 0x28 or 0x53 => Btn.Down, 0x25 or 0x41 => Btn.Left, 0x27 or 0x44 => Btn.Right, 0x20 or 0x0D => Btn.Action, _ => null
        };
        if (vk == 0x1B) { if (down) System.Windows.Application.Current.Dispatcher.BeginInvoke(Stop); return true; }
        if (b == null) return false;
        if (down) lock (gate) Current?.Press(b.Value);
        return true;
    }
}
