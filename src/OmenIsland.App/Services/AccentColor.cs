using System.Windows.Media;

namespace OmenIsland.App.Services;

public static class AccentColor
{
    public static Color ForSurface(Color surface, Color wallpaperAccent)
    {
        var (hue, saturation, _) = ToHsl(wallpaperAccent);
        if (saturation < 0.35)
        {
            saturation = 0.62;
        }

        saturation = Math.Clamp(saturation, 0.5, 0.92);
        Color best = wallpaperAccent;
        var bestContrast = 0d;
        for (var lightness = 0.22; lightness <= 0.84; lightness += 0.03)
        {
            var candidate = FromHsl(hue, saturation, lightness);
            var contrast = Contrast(candidate, surface);
            if (contrast > bestContrast)
            {
                bestContrast = contrast;
                best = candidate;
            }
        }

        return bestContrast >= 3
            ? best
            : RelativeLuminance(surface) < 0.4
                ? Colors.White
                : Color.FromRgb(20, 20, 20);
    }

    public static double Contrast(Color first, Color second)
    {
        var lighter = Math.Max(RelativeLuminance(first), RelativeLuminance(second));
        var darker = Math.Min(RelativeLuminance(first), RelativeLuminance(second));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color)
    {
        double Channel(byte component)
        {
            var value = component / 255d;
            return value <= 0.03928
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) +
               (0.7152 * Channel(color.G)) +
               (0.0722 * Channel(color.B));
    }

    public static (double Hue, double Saturation, double Lightness) ToHsl(Color color)
    {
        var red = color.R / 255d;
        var green = color.G / 255d;
        var blue = color.B / 255d;
        var max = Math.Max(red, Math.Max(green, blue));
        var min = Math.Min(red, Math.Min(green, blue));
        var lightness = (max + min) / 2d;
        if (Math.Abs(max - min) < 0.0001)
        {
            return (0, 0, lightness);
        }

        var delta = max - min;
        var saturation = lightness > 0.5
            ? delta / (2d - max - min)
            : delta / (max + min);
        double hue;
        if (Math.Abs(max - red) < 0.0001)
        {
            hue = ((green - blue) / delta) % 6d;
        }
        else if (Math.Abs(max - green) < 0.0001)
        {
            hue = ((blue - red) / delta) + 2d;
        }
        else
        {
            hue = ((red - green) / delta) + 4d;
        }

        hue *= 60d;
        if (hue < 0)
        {
            hue += 360d;
        }

        return (hue, saturation, lightness);
    }

    public static Color FromHsl(double hue, double saturation, double lightness, byte alpha = 255)
    {
        double Channel(double amount)
        {
            var sector = (amount + (hue / 30d)) % 12d;
            var chroma = (1d - Math.Abs((2d * lightness) - 1d)) * saturation;
            return lightness - (chroma * Math.Max(-1d, Math.Min(sector - 3d, Math.Min(9d - sector, 1d))));
        }

        return Color.FromArgb(
            alpha,
            (byte)Math.Clamp(Channel(0) * 255d, 0, 255),
            (byte)Math.Clamp(Channel(8) * 255d, 0, 255),
            (byte)Math.Clamp(Channel(4) * 255d, 0, 255));
    }
}
