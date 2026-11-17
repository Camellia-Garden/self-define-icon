using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Hanairo;

internal static class SelfTest
{
    public static int Run(string scratch, string? drive)
    {
        Directory.CreateDirectory(scratch);
        using var log = File.CreateText(Path.Combine(scratch, "self-test.txt"));
        try
        {
            RunChecks(scratch, drive, log);
            return 0;
        }
        catch (Exception error)
        {
            log.WriteLine(error);
            return 1;
        }
    }

    private static void RunChecks(string scratch, string? drive, TextWriter log)
    {
        string root = Path.Combine(scratch, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string data = Path.Combine(root, "data");
        var store = new IconStore(data);
        string star = store.SaveIcon(IconImage.Encode(IconImage.FromAsset("star.png")));
        string cat = store.SaveIcon(IconImage.Encode(IconImage.FromAsset("cat.png")));
        Check(IconImage.Load(cat).PixelWidth == 256, "应用生成的多尺寸 ICO 无法重新导入。");
        log.WriteLine("PASS ICO encoding and reimport");
        CheckImageEditing(root);
        log.WriteLine("PASS PNG import, transparent fit, zoom and drag crop, exact preview-to-ICO pixels");

        string folder = Directory.CreateDirectory(Path.Combine(root, "换装测试文件夹")).FullName;
        string ini = Path.Combine(folder, "desktop.ini");
        var folderTarget = Target.FromPath(folder);
        IconEntry folderEntry = store.Apply(folderTarget, star);
        Check(File.GetAttributes(folder).HasFlag(FileAttributes.ReadOnly), "文件夹自定义标志未设置。");
        string? folderIcon = ExplorerIconPath(folder);
        Check(folderIcon?.Contains(star, StringComparison.OrdinalIgnoreCase) == true,
            $"Windows 未报告新的文件夹图标位置，返回值为 {folderIcon ?? "(null)"}。");
        store.Restore(folderEntry);
        Check(!File.Exists(ini) && !File.GetAttributes(folder).HasFlag(FileAttributes.ReadOnly),
            "新建 desktop.ini 或文件夹属性未复原。");

        string originalIni = "[.ShellClassInfo]\r\nInfoTip=原始备注\r\nIconFile=old.ico\r\nIconIndex=0\r\n";
        File.WriteAllText(ini, originalIni, Encoding.Unicode);
        File.SetAttributes(ini, File.GetAttributes(ini) | FileAttributes.Hidden | FileAttributes.System);
        byte[] iniBefore = File.ReadAllBytes(ini);
        FileAttributes beforeFlags = File.GetAttributes(ini);
        store.Apply(folderTarget, star);
        var folderAgain = store.Apply(folderTarget, cat);
        Check(File.ReadAllText(ini).Contains("InfoTip=原始备注"), "自定义图标破坏了原有信息提示。");
        store = new IconStore(data);
        store.Restore(folderAgain);
        Check(File.ReadAllBytes(ini).SequenceEqual(iniBefore) && File.GetAttributes(ini) == beforeFlags,
            "原有 desktop.ini 未按原字节和属性还原。");
        log.WriteLine("PASS Windows folder icon discovery, existing config, repeat apply and restart restore");

        string original = Path.Combine(root, "原始文件.txt");
        File.WriteAllText(original, "Keep this file.");
        var originalTarget = Target.FromPath(original);
        var generated = store.Apply(originalTarget, star);
        Check(Shell.ShortcutLaunch(generated.Location)?.Path == original, "生成的快捷方式目标错误。");
        var updated = store.Apply(originalTarget, cat);
        Check(generated.Location == updated.Location && Shell.ShortcutIcon(updated.Location).Path == cat,
            "重新换装后生成了重复的快捷方式。");
        store.Restore(updated);
        Check(!File.Exists(updated.Location) && File.ReadAllText(original) == "Keep this file.",
            "普通文件换装或还原影响了原文件。");
        log.WriteLine("PASS file shortcut creation, retargeted icon, and safe removal");

        string shortcut = Path.Combine(root, "样例.lnk");
        Shell.CreateShortcut(shortcut, original, star);
        byte[] shortcutBefore = File.ReadAllBytes(shortcut);
        var shortcutTarget = Target.FromPath(shortcut);
        var shortcutEntry = store.Apply(shortcutTarget, cat);
        Check(Shell.ShortcutIcon(shortcut).Path == cat &&
            Shell.ShortcutLaunch(shortcut)?.Path == original, "Windows 未读取到预期的快捷方式图标或目标。");
        byte[] changed = File.ReadAllBytes(shortcut);
        File.WriteAllBytes(shortcut, shortcutBefore);
        try
        {
            store.Restore(shortcutEntry);
            throw new InvalidOperationException("未发现外部修改冲突。");
        }
        catch (IOException)
        {
            Check(File.ReadAllBytes(shortcut).SequenceEqual(shortcutBefore), "复原覆盖了外部修改。");
        }
        File.WriteAllBytes(shortcut, changed);
        store.Restore(shortcutEntry);
        Check(File.ReadAllBytes(shortcut).SequenceEqual(shortcutBefore), "快捷方式未按原字节复原。");
        log.WriteLine("PASS local Shell Link editing, conflict detection and exact restore");

        string internet = Path.Combine(root, "样例.url");
        File.WriteAllText(internet, "[InternetShortcut]\r\nURL=https://example.com/\r\n");
        byte[] urlBefore = File.ReadAllBytes(internet);
        var urlEntry = store.Apply(Target.FromPath(internet), cat);
        Check(File.ReadAllText(internet).Contains("URL=https://example.com/") &&
            File.ReadAllText(internet).Contains(cat), "网络快捷方式的地址或图标设置错误。");
        store.Restore(urlEntry);
        Check(File.ReadAllBytes(internet).SequenceEqual(urlBefore), "网络快捷方式未按原字节复原。");
        log.WriteLine("PASS Internet shortcut apply and restore");

        if (drive != null)
        {
            var driveTarget = Target.FromPath(drive);
            var driveEntry = store.Apply(driveTarget, star);
            try
            {
                using var key = Shell.DriveIconKey(driveTarget.Path);
                Check(Equals(key?.GetValue(""), $"{star},0"), "盘符注册表图标值错误。");
                string? icon = ExplorerIconPath(driveTarget.Path);
                Check(icon?.Contains(star, StringComparison.OrdinalIgnoreCase) == true,
                    $"Windows 未报告新的盘符图标位置，返回值为 {icon ?? "(null)"}。");
            }
            finally
            {
                store.Restore(driveEntry);
            }

            using var restored = Shell.DriveIconKey(driveTarget.Path);
            Check(Equals(restored?.GetValue(""), driveEntry.Original.RegistryValue), "盘符图标注册表值未复原。");
            log.WriteLine("PASS Windows drive icon discovery and registry restore");
        }
        Check(new IconStore(data).Entries.Count == 0, "复原后仍有活动记录。");
    }

    private static void CheckImageEditing(string scratch)
    {
        const int width = 480;
        const int height = 240;
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = (y * width + x) * 4;
                pixels[index] = x < width / 2 ? (byte)0 : (byte)255;
                pixels[index + 2] = x < width / 2 ? (byte)255 : (byte)0;
                pixels[index + 3] = 255;
            }
        }

        string path = Path.Combine(scratch, "裁剪样例.png");
        var encoder = new PngBitmapEncoder();
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path))
            encoder.Save(stream);

        byte[] input = File.ReadAllBytes(path);
        BitmapSource source = IconImage.LoadSource(path);
        Check(source.PixelWidth == width && source.PixelHeight == height, "图片导入后丢失原始比例或裁剪分辨率。");
        var fit = new IconComposition(source);
        BitmapSource fitted = IconImage.Render(fit, 32);
        Check(Pixel(fitted, 0, 0) == Color.FromArgb(0, 0, 0, 0) &&
            Pixel(fitted, 5, 16) == Colors.Red, "适应画布时应保留完整图片和透明留白。");

        var center = new Point(0.5, 0.5);
        var filled = new IconComposition(source, fit.FillZoom);
        var left = filled.Move(10, 4);
        var right = filled.Move(-10, -4);
        Check(Pixel(IconImage.Render(left, 32), 10, 10) == Colors.Red &&
            Pixel(IconImage.Render(right, 32), 10, 10) == Colors.Blue &&
            left.Y == 0 && right.Y == 0, "拖动裁剪应选择不同区域，并且填满时不能露出边缘。");
        Check(filled.WithZoom(0.001, center).Zoom == IconComposition.MinZoom &&
            fit.WithZoom(900, center).Zoom == fit.MaxZoom, "缩放超出有效范围。");

        var anchor = new Point(0.55, 0.45);
        Rect before = filled.Bounds(1);
        Rect after = filled.WithZoom(4, anchor).Bounds(1);
        Check(Math.Abs((anchor.X - before.X) / before.Width - (anchor.X - after.X) / after.Width) < 1e-9 &&
            Math.Abs((anchor.Y - before.Y) / before.Height - (anchor.Y - after.Y) / after.Height) < 1e-9,
            "滚轮缩放时光标对应的图片位置发生偏移。");

        byte[] ico = IconImage.Encode(right);
        foreach (int size in new[] { 16, 32, 48, 96, 256 })
        {
            int frames = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4));
            int entry = Enumerable.Range(0, frames).Select(index => 6 + index * 16)
                .First(index => ico[index] == (size == 256 ? 0 : size));
            int count = BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(entry + 8));
            int offset = BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(entry + 12));
            using var frameStream = new MemoryStream(ico, offset, count);
            var decoder = new PngBitmapDecoder(frameStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Pbgra32, null, 0);
            byte[] preview = new byte[size * size * 4];
            byte[] output = new byte[preview.Length];
            IconImage.Render(right, size).CopyPixels(preview, size * 4, 0);
            frame.CopyPixels(output, size * 4, 0);
            Check(output.SequenceEqual(preview), $"{size} 像素成品与导出的 ICO 不一致。");
        }

        Check(File.ReadAllBytes(path).SequenceEqual(input), "编辑操作写回了原始图片。");
        File.Delete(path);
    }

    private static Color Pixel(BitmapSource bitmap, int x, int y)
    {
        byte[] pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static string? ExplorerIconPath(string path) =>
        SHGetFileInfo(path, 0, out ShellFileInfo info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x1000) != IntPtr.Zero
            ? info.DisplayName : null;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);
}
