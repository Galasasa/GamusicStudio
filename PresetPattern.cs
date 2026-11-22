using System.Windows;
using System.Windows.Media;

namespace MidiKey;

internal static class PresetPattern
{
    private static readonly Color[] Palette =
    {
        Color.FromRgb(63, 144, 255), Color.FromRgb(255, 72, 102),
        Color.FromRgb(57, 237, 182), Color.FromRgb(189, 104, 255),
        Color.FromRgb(255, 162, 63), Color.FromRgb(255, 88, 202),
        Color.FromRgb(56, 223, 245)
    };

    internal static Brush Create(Guid id)
    {
        var random = new Random(BitConverter.ToInt32(id.ToByteArray(), 0));
        Color color = Palette[random.Next(Palette.Length)];
        var bounds = new Rect(0, 0, 246, 208);
        var drawing = new DrawingGroup { ClipGeometry = new RectangleGeometry(bounds) };
        using (DrawingContext context = drawing.Open())
        {
            Color shade = Color.FromRgb((byte)(8 + color.R / 8), (byte)(7 + color.G / 8), (byte)(17 + color.B / 9));
            var surface = new LinearGradientBrush(shade, Color.FromRgb(12, 9, 21), 60);
            context.DrawRectangle(surface, null, bounds);
            var strong = new Pen(new SolidColorBrush(Color.FromArgb(210, color.R, color.G, color.B)), 2.4);
            var soft = new Pen(new SolidColorBrush(Color.FromArgb(102, color.R, color.G, color.B)), 1.1);
            int gap = random.Next(12, 21);
            var center = new Point(bounds.Width * (0.39 + random.NextDouble() * 0.22),
                bounds.Height * (0.39 + random.NextDouble() * 0.22));
            switch (random.Next(3))
            {
                case 0:
                    double slope = random.Next(2) == 0 ? bounds.Height * -1.10 : bounds.Height * 1.10;
                    int stripe = 0;
                    for (double x = -bounds.Height * 1.15; x < bounds.Width + bounds.Height * 1.15; x += gap)
                        context.DrawLine(stripe++ % 4 == 0 ? strong : soft, new Point(x, 0), new Point(x + slope, bounds.Height));
                    break;
                case 1:
                    for (int i = 1; i <= 13; i++)
                    {
                        double radius = i * gap;
                        context.DrawRectangle(null, i % 3 == 1 ? strong : soft,
                            new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2));
                    }
                    break;
                default:
                    for (int i = 1; i <= 14; i++)
                    {
                        double radius = i * gap;
                        context.DrawEllipse(null, i % 3 == 1 ? strong : soft, center, radius, radius);
                    }
                    break;
            }
        }
        var brush = new DrawingBrush(drawing) { Stretch = Stretch.UniformToFill };
        brush.Freeze();
        return brush;
    }
}
