using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace MidiKey;

public sealed class ParticleField : FrameworkElement
{
    private const double Spacing = 30;
    private const double Radius = 110;
    private readonly DispatcherTimer timer;
    private readonly Stopwatch clock = new();
    private readonly SolidColorBrush quiet = new(Color.FromArgb(28, 89, 216, 222));
    private readonly SolidColorBrush bright = new(Color.FromArgb(115, 110, 245, 255));
    private double[] positions = Array.Empty<double>();
    private double[] velocities = Array.Empty<double>();
    private int columns, rows;
    private Point? pointer;
    private long previous;

    public ParticleField()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        quiet.Freeze();
        bright.Freeze();
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, Tick, Dispatcher);
        timer.Stop();
        SizeChanged += (_, _) => Rebuild();
        Unloaded += (_, _) => timer.Stop();
    }

    internal void Move(Point location)
    {
        pointer = location;
        Start();
    }

    internal void Leave()
    {
        pointer = null;
        Start();
    }

    private void Start()
    {
        if (timer.IsEnabled) return;
        clock.Restart();
        previous = 0;
        timer.Start();
    }

    private void Rebuild()
    {
        columns = (int)Math.Ceiling(ActualWidth / Spacing) + 1;
        rows = (int)Math.Ceiling(ActualHeight / Spacing) + 1;
        positions = new double[columns * rows];
        velocities = new double[positions.Length];
        InvalidateVisual();
    }

    private void Tick(object? sender, EventArgs e)
    {
        long now = clock.ElapsedMilliseconds;
        double step = Math.Clamp((now - previous) / 16.67, 0.3, 2);
        previous = now;
        bool moving = false;
        for (int i = 0; i < positions.Length; i++)
        {
            double x = (i % columns) * Spacing;
            double y = (i / columns) * Spacing;
            double target = 0;
            if (pointer is Point cursor)
            {
                double dx = x - cursor.X, dy = y - cursor.Y;
                double distance = Math.Sqrt(dx * dx + dy * dy);
                double influence = Math.Max(0, 1 - distance / Radius);
                target = 27 * influence * influence;
            }
            velocities[i] += (target - positions[i]) * 0.12 * step;
            velocities[i] *= Math.Pow(0.78, step);
            positions[i] += velocities[i] * step;
            moving |= Math.Abs(target - positions[i]) > 0.08 || Math.Abs(velocities[i]) > 0.05;
        }
        InvalidateVisual();
        if (!moving) timer.Stop();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        for (int i = 0; i < positions.Length; i++)
        {
            double x = (i % columns) * Spacing;
            double y = (i / columns) * Spacing + positions[i];
            double amount = positions[i];
            dc.DrawEllipse(amount > 3 ? bright : quiet, null, new Point(x, y), amount > 3 ? 1.35 : 0.85, amount > 3 ? 1.35 : 0.85);
        }
    }
}
