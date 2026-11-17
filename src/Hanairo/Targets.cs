using System.IO;

namespace Hanairo;

internal enum TargetKind { Folder, Shortcut, InternetShortcut, File, Drive, Pinned }

internal sealed record Target(string Path, TargetKind Kind)
{
    public string Name => Kind == TargetKind.Drive
        ? Path[..2]
        : System.IO.Path.GetFileName(Path);

    public string Label => Kind switch
    {
        TargetKind.Folder => "文件夹",
        TargetKind.Shortcut => "快捷方式",
        TargetKind.InternetShortcut => "网络快捷方式",
        TargetKind.File => "文件 → 同目录快捷方式",
        TargetKind.Drive => "盘符",
        TargetKind.Pinned => "任务栏固定项",
        _ => throw new ArgumentOutOfRangeException()
    };

    public string Glyph => Kind switch
    {
        TargetKind.Folder => "\uE8B7",
        TargetKind.Drive => "\uEDA2",
        TargetKind.Pinned => "\uE840",
        TargetKind.File => "\uE8A5",
        _ => "\uE71B"
    };

    public static string PinnedDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

    public static Target FromPath(string input)
    {
        string path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(input));
        if (path.Length == 3 && char.IsAsciiLetter(path[0]) && path[1..] == @":\")
        {
            path = path.ToUpperInvariant();
            var drive = new DriveInfo(path);
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram))
                throw new IOException("请选择本机磁盘或可移动磁盘的盘符。");
            return new Target(path, TargetKind.Drive);
        }

        if (Directory.Exists(path))
            return new Target(path, TargetKind.Folder);

        if (!System.IO.File.Exists(path))
            throw new IOException($"找不到目标：{path}");

        string extension = System.IO.Path.GetExtension(path);
        if (extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
            return new Target(path, TargetKind.InternetShortcut);

        if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            bool pinned = string.Equals(System.IO.Path.GetDirectoryName(path), PinnedDirectory,
                StringComparison.OrdinalIgnoreCase);
            return new Target(path, pinned ? TargetKind.Pinned : TargetKind.Shortcut);
        }

        return new Target(path, TargetKind.File);
    }
}
