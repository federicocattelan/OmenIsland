namespace OmenIsland.App.Presentation;

/// <summary>Physical-pixel rectangle (left, top, right, bottom), as Win32 reports it.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public override string ToString() => $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}";
}

public static class IdlePlacement
{
    // The idle bar is 7 DIP tall; even at 300% scaling it stays well under this.
    private const int MaxIdleHeight = 24;

    /// <summary>
    /// Returns why the idle bar is not where it belongs (top edge of the primary monitor,
    /// fully inside it), or null when the placement is fine.
    /// </summary>
    public static string? FindProblem(PixelRect window, PixelRect screen)
    {
        if (window.Width <= 0 || window.Height <= 0)
        {
            return $"empty bounds {window}";
        }

        if (window.Height > MaxIdleHeight)
        {
            return $"idle bar too tall {window}";
        }

        if (window.Top != screen.Top)
        {
            return $"not on the top edge {window} screen {screen}";
        }

        if (window.Left < screen.Left || window.Right > screen.Right)
        {
            return $"outside the primary monitor {window} screen {screen}";
        }

        return null;
    }
}
