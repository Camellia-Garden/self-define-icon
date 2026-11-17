using System.IO;
using System.Runtime.InteropServices;
using System.Text;

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
