using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Hanairo;

internal static class IconImage
{
    public static BitmapSource Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 64 * 1024 * 1024)
            throw new IOException("请选择小于 64 MB 的图片。");

        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
        var frame = decoder is IconBitmapDecoder
            ? decoder.Frames.MaxBy(candidate => (long)candidate.PixelWidth * candidate.PixelHeight)!
            : decoder.Frames[0];

        if ((long)frame.PixelWidth * frame.PixelHeight > 80_000_000)
            throw new IOException("图片像素过多，请先缩小后导入。");

        // Render before closing the input so an imported image can be moved or deleted immediately.
        return Render(frame, 256);
    }

    public static BitmapSource FromAsset(string name)
    {
        var source = new BitmapImage(new Uri($"pack://application:,,,/Assets/{name}", UriKind.Absolute));
        source.Freeze();
        return source;
    }

    public static byte[] Encode(BitmapSource source)
    {
        int[] sizes = [16, 24, 32, 40, 48, 64, 96, 128, 256];
        var frames = new List<byte[]>(sizes.Length);
        foreach (int size in sizes)
        {
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(Render(source, size)));
            using var frame = new MemoryStream();
            png.Save(frame);
            frames.Add(frame.ToArray());
        }

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)frames.Count);
        int offset = 6 + 16 * frames.Count;
        for (int i = 0; i < sizes.Length; i++)
        {
            writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
            writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(frames[i].Length);
            writer.Write(offset);
            offset += frames[i].Length;
        }

        foreach (var frame in frames)
            writer.Write(frame);

        return output.ToArray();
    }

    private static BitmapSource Render(BitmapSource source, int size)
    {
        double scale = size / (double)Math.Max(source.PixelWidth, source.PixelHeight);
        double width = source.PixelWidth * scale;
        double height = source.PixelHeight * scale;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var drawing = visual.RenderOpen())
            drawing.DrawImage(source, new Rect((size - width) / 2, (size - height) / 2, width, height));

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
