using System.Windows;
using System.Windows.Media.Imaging;

namespace Hanairo;

internal sealed record IconComposition(BitmapSource Source, double Zoom = 1, double X = 0, double Y = 0)
{
    public const double MinZoom = 0.25;
    public double FillZoom => (double)Math.Max(Source.PixelWidth, Source.PixelHeight) /
        Math.Min(Source.PixelWidth, Source.PixelHeight);
    public double MaxZoom => Math.Max(8, 4 * FillZoom);

    // Position is the image center offset in units of the square icon canvas.
    public Rect Bounds(double size)
    {
        double scale = Zoom * size / Math.Max(Source.PixelWidth, Source.PixelHeight);
        double width = Source.PixelWidth * scale;
        double height = Source.PixelHeight * scale;
        return new Rect((0.5 + X) * size - width / 2, (0.5 + Y) * size - height / 2, width, height);
    }

    public IconComposition WithZoom(double zoom, Point anchor)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        double ratio = zoom / Zoom;
        return Place(zoom, X + (anchor.X - 0.5 - X) * (1 - ratio),
            Y + (anchor.Y - 0.5 - Y) * (1 - ratio));
    }

    public IconComposition Move(double x, double y) => Place(Zoom, X + x, Y + y);

    private IconComposition Place(double zoom, double x, double y)
    {
        var next = this with { Zoom = zoom };
        Rect bounds = next.Bounds(1);
        double limitX = Math.Abs(bounds.Width - 1) / 2;
        double limitY = Math.Abs(bounds.Height - 1) / 2;
        return next with { X = Math.Clamp(x, -limitX, limitX), Y = Math.Clamp(y, -limitY, limitY) };
    }
}
