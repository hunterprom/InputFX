using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace InputFX;

/// <summary>Shows the running keyboard game as a big 14 x 5 pixel screen.</summary>
public sealed class GameView : FrameworkElement
{
    public GameRunner? Runner { get; set; }
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(40) };

    public GameView()
    {
        Height = 200; MinWidth = 300;
        timer.Tick += (_, _) => InvalidateVisual();
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) timer.Start(); else timer.Stop(); };
    }

    protected override Size MeasureOverride(Size a) => new(double.IsInfinity(a.Width) ? 520 : a.Width, Height);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(14, 16, 20)), null, new Rect(0, 0, ActualWidth, ActualHeight), 8, 8);
        var pic = Runner?.Snapshot();
        double cell = Math.Min((ActualWidth - 16) / Game.W, (ActualHeight - 16) / Game.H);
        double ox = (ActualWidth - cell * Game.W) / 2, oy = (ActualHeight - cell * Game.H) / 2;
        for (int x = 0; x < Game.W; x++)
            for (int y = 0; y < Game.H; y++)
            {
                var p = pic != null ? pic[x, y] : new Px(.08, .08, .1);
                var rect = new Rect(ox + x * cell + 2, oy + y * cell + 2, cell - 4, cell - 4);
                byte R = (byte)(Math.Clamp(p.R, 0, 1) * 255), G = (byte)(Math.Clamp(p.G, 0, 1) * 255), B = (byte)(Math.Clamp(p.B, 0, 1) * 255);
                double glow = Math.Max(p.R, Math.Max(p.G, p.B));
                if (glow > .5) dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb((byte)(50 * glow), R, G, B)), null, Rect.Inflate(rect, 3, 3), 6, 6);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb((byte)Math.Max(R, (byte)22), (byte)Math.Max(G, (byte)24), (byte)Math.Max(B, (byte)28))), null, rect, 4, 4);
            }
    }
}
