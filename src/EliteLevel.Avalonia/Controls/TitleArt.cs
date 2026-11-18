using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace EliteLevel.Avalonia.Controls;

/// <summary>
/// Loads cached title art. A missing or unreadable file falls back to generated art.
/// The icon-only banner is the icon drawn large under a charcoal scrim.
/// </summary>
public static class TitleArt
{
    private static readonly Dictionary<string, Bitmap> Cache = new(StringComparer.Ordinal);

    public static Bitmap? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        var key = path + ":cover";
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        try
        {
            var bitmap = new Bitmap(path);
            Cache[key] = bitmap;
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static Bitmap? LoadBanner(string? bannerPath, string? iconPath, int width, int height)
    {
        var banner = Load(bannerPath);
        if (banner is not null)
        {
            return banner;
        }

        if (string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath) || width <= 0 || height <= 0)
        {
            return null;
        }

        var key = iconPath + ":banner:" + width + "x" + height;
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var icon = Load(iconPath);
        if (icon is null)
        {
            return null;
        }

        try
        {
            var bitmap = Soften(icon, width, height);
            if (bitmap is null)
            {
                return null;
            }

            Cache[key] = bitmap;
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Bitmap? Soften(Bitmap icon, int width, int height)
    {
        var tiny = new RenderTargetBitmap(new PixelSize(40, 24), new Vector(96, 96));
        using (var context = tiny.CreateDrawingContext())
        {
            context.DrawImage(icon, new Rect(0, 0, 40, 24));
        }

        var full = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        using (var context = full.CreateDrawingContext())
        {
            context.DrawImage(tiny, new Rect(0, 0, width, height));
            context.FillRectangle(new SolidColorBrush(Color.Parse("#B3120A0B")), new Rect(0, 0, width, height));
        }

        return full;
    }
}
