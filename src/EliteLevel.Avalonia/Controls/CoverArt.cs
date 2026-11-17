using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace EliteLevel.Avalonia.Controls;

/// <summary>
/// Deterministic abstract covers. The palette is the design-system oxblood, gold and charcoal ramp.
/// No game art, no console logos. Bitmaps are cached by seed and size.
/// </summary>
public static class CoverArt
{
    private static readonly Dictionary<string, Bitmap> Cache = new();
    private static readonly Color Charcoal = Color.Parse("#120A0B");
    private static readonly Color Oxblood = Color.Parse("#8A2A35");
    private static readonly Color OxbloodDeep = Color.Parse("#3A1117");
    private static readonly Color Gold = Color.Parse("#D4AF37");
    private static readonly Color GoldWarm = Color.Parse("#B9862E");
    private static readonly Color Rose = Color.Parse("#C4454F");

    public static Bitmap? Render(string? seed, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var key = (seed ?? "disc") + ":" + width + "x" + height;
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        try
        {
            var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
            using (var context = bitmap.CreateDrawingContext())
            {
                Draw(context, seed ?? "disc", width, height);
            }

            Cache[key] = bitmap;
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Draw(DrawingContext context, string seed, int width, int height)
    {
        var hash = Hash(seed);
        var rect = new Rect(0, 0, width, height);
        context.FillRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Charcoal, 0),
                new GradientStop(OxbloodDeep, 0.55),
                new GradientStop(Color.Parse("#5E1C24"), 1)
            }
        }, rect);

        var motif = hash % 4;
        var cx = width * (0.62 + (hash % 17) / 100.0);
        var cy = height * (0.42 + ((hash / 17) % 13) / 100.0);
        if (motif == 0)
        {
            DrawRings(context, cx, cy, Math.Min(width, height) * 0.42, hash);
        }
        else if (motif == 1)
        {
            DrawTriangles(context, width, height, hash);
        }
        else if (motif == 2)
        {
            context.DrawEllipse(new SolidColorBrush(Color.FromArgb(180, Gold.R, Gold.G, Gold.B)), null, new Point(cx, cy), width * 0.16, width * 0.16);
            context.DrawEllipse(null, new Pen(new SolidColorBrush(Rose), 3), new Point(width * 0.3, height * 0.7), width * 0.22, width * 0.22);
        }
        else
        {
            DrawGrid(context, width, height);
            context.DrawEllipse(new SolidColorBrush(Color.FromArgb(210, 90, 160, 140)), null, new Point(cx, height * 0.38), width * 0.12, width * 0.12);
        }

        context.FillRectangle(new RadialGradientBrush
        {
            Center = new RelativePoint(0.72, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.72, 0.5, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.55, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.55, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(90, Gold.R, Gold.G, Gold.B), 0),
                new GradientStop(Color.FromArgb(0, Oxblood.R, Oxblood.G, Oxblood.B), 1)
            }
        }, rect);
    }

    private static void DrawRings(DrawingContext context, double cx, double cy, double radius, int hash)
    {
        var gold = new Pen(new SolidColorBrush(GoldWarm), 2);
        var rose = new Pen(new SolidColorBrush(Color.FromArgb(160, Rose.R, Rose.G, Rose.B)), 1.5);
        context.DrawEllipse(new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), null, new Point(cx, cy), radius * 0.28, radius * 0.28);
        for (var i = 1; i <= 4; i++)
        {
            context.DrawEllipse(null, i % 2 == 0 ? gold : rose, new Point(cx, cy), radius * i / 4.0, radius * i / 4.2);
        }

        if ((hash & 1) == 0)
        {
            context.DrawLine(gold, new Point(cx - radius, cy), new Point(cx + radius, cy));
        }
    }

    private static void DrawTriangles(DrawingContext context, int width, int height, int hash)
    {
        var brush = new SolidColorBrush(Color.FromArgb(170, GoldWarm.R, GoldWarm.G, GoldWarm.B));
        var alt = new SolidColorBrush(Color.FromArgb(150, Oxblood.R, Oxblood.G, Oxblood.B));
        for (var i = 0; i < 3; i++)
        {
            var shift = ((hash >> (i * 3)) & 31) / 31.0;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                var x = width * (0.25 + 0.22 * i);
                var y = height * (0.25 + shift * 0.2);
                var s = Math.Min(width, height) * (0.18 + 0.06 * i);
                g.BeginFigure(new Point(x, y), true);
                g.LineTo(new Point(x + s, y + s * 1.15));
                g.LineTo(new Point(x - s * 0.7, y + s));
                g.EndFigure(true);
            }

            context.DrawGeometry(i == 1 ? brush : alt, null, geometry);
        }
    }

    private static void DrawGrid(DrawingContext context, int width, int height)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(90, Gold.R, Gold.G, Gold.B)), 1);
        var horizon = height * 0.55;
        for (var i = 0; i <= 8; i++)
        {
            var x = width * i / 8.0;
            context.DrawLine(pen, new Point(width / 2.0, horizon), new Point(x, height));
        }

        for (var i = 0; i < 5; i++)
        {
            var y = horizon + (height - horizon) * i / 5.0;
            context.DrawLine(pen, new Point(0, y), new Point(width, y));
        }
    }

    private static int Hash(string seed)
    {
        unchecked
        {
            var hash = 17;
            foreach (var c in seed)
            {
                hash = hash * 31 + c;
            }

            return hash & 0x7fffffff;
        }
    }
}
