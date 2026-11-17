using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

namespace Hanairo;

internal static class Shell
{
    private static readonly string[] MenuClasses = ["*", "Directory", "Drive"];
    private const string Verb = "CamelliaGarden.Hanairo.ChangeIcon";
    private const string DriveIcons = @"Software\Classes\Applications\Explorer.exe\Drives";

    public static RegistryKey? DriveIconKey(string drive, bool create = false)
    {
        if (drive.Length != 3 || !char.IsAsciiLetter(drive[0]) || drive[1..] != @":\")
            throw new ArgumentException("无效的盘符。", nameof(drive));

        string key = $@"{DriveIcons}\{char.ToUpperInvariant(drive[0])}\DefaultIcon";
        return create
            ? Registry.CurrentUser.CreateSubKey(key)
            : Registry.CurrentUser.OpenSubKey(key, writable: true);
    }

    public static void RemoveEmptyDriveIconKey(string drive)
    {
        string path = $@"{DriveIcons}\{char.ToUpperInvariant(drive[0])}";
        using var parent = Registry.CurrentUser.OpenSubKey(path, writable: true);
        using (var key = parent?.OpenSubKey("DefaultIcon"))
        {
            if (key == null || key.ValueCount != 0 || key.SubKeyCount != 0)
                return;
        }
        parent?.DeleteSubKey("DefaultIcon", throwOnMissingSubKey: false);
    }

    public static bool MenuEnabled() => MenuClasses.All(entry =>
    {
        using var command = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{entry}\shell\{Verb}\command");
        return Equals(command?.GetValue(""), MenuCommand(entry));
    });

    public static void SetMenu(bool enable)
    {
        foreach (string entry in MenuClasses)
        {
            string parentPath = $@"Software\Classes\{entry}\shell";
            if (!enable)
            {
                using var parent = Registry.CurrentUser.OpenSubKey(parentPath, writable: true);
                parent?.DeleteSubKeyTree(Verb, throwOnMissingSubKey: false);
                continue;
            }

            using var verb = Registry.CurrentUser.CreateSubKey($@"{parentPath}\{Verb}");
            verb.SetValue("", "更换图标 · 花色图标工房");
            verb.SetValue("Icon", $"\"{Environment.ProcessPath}\",0");
            verb.SetValue("MultiSelectModel", "Single");
            using var command = verb.CreateSubKey("command");
            command.SetValue("", MenuCommand(entry));
        }
        RefreshAll();
    }

    private static string MenuCommand(string entry)
    {
        // A root path ends in a backslash, which would escape the closing quote.
        string argument = entry == "Drive" ? "%1." : "%1";
        return $"\"{Environment.ProcessPath}\" --target \"{argument}\"";
    }

    public static void CreateShortcut(string file, string target, string icon)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            Marshal.ThrowExceptionForHR(link.SetPath(target));
            Marshal.ThrowExceptionForHR(link.SetWorkingDirectory(Path.GetDirectoryName(target)!));
            Marshal.ThrowExceptionForHR(link.SetIconLocation(icon, 0));
            ((IPersistFile)link).Save(file, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    public static void WriteIni(string file, string section, string key, string? value)
    {
        if (!WritePrivateProfileString(section, key, value, file))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public static (string Path, int Index) ShortcutIcon(string file)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(file, 0);
            var path = new StringBuilder(32768);
            Marshal.ThrowExceptionForHR(link.GetIconLocation(path, path.Capacity, out int index));
            return (path.ToString(), index);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    public static (string Path, string Arguments)? ShortcutLaunch(string file)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(file, 0);
            var path = new StringBuilder(32768);
            var arguments = new StringBuilder(32768);
            Marshal.ThrowExceptionForHR(link.GetPath(path, path.Capacity, IntPtr.Zero, 0x4)); // SLGP_RAWPATH
            Marshal.ThrowExceptionForHR(link.GetArguments(arguments, arguments.Capacity));
            string fullPath = Environment.ExpandEnvironmentVariables(path.ToString());
            return Path.IsPathFullyQualified(fullPath) ? (fullPath, arguments.ToString()) : null;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    public static void RefreshItem(string item, bool drive = false)
    {
        const uint unicodePathAsync = 0x2005;
        SHChangeNotify(0x2000, unicodePathAsync, item, null); // SHCNE_UPDATEITEM
        string? parent = Path.GetDirectoryName(item);
        if (parent is not null)
            SHChangeNotify(0x1000, unicodePathAsync, parent, null); // SHCNE_UPDATEDIR
        if (drive)
            RefreshAll();
    }

    public static void RefreshAll() => SHChangeNotify(0x08000000, 0x2000, null, null);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern void SHChangeNotify(int eventId, uint flags,
        [MarshalAs(UnmanagedType.LPWStr)] string? item1,
        [MarshalAs(UnmanagedType.LPWStr)] string? item2);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WritePrivateProfileString(string section, string key, string? value, string file);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr data, uint flags);
        [PreserveSig] int GetIDList(out IntPtr pidl);
        [PreserveSig] int SetIDList(IntPtr pidl);
        [PreserveSig] int GetDescription(IntPtr description, int max);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        [PreserveSig] int GetWorkingDirectory(IntPtr directory, int max);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        [PreserveSig] int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int max);
        [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        [PreserveSig] int GetHotkey(out short hotkey);
        [PreserveSig] int SetHotkey(short hotkey);
        [PreserveSig] int GetShowCmd(out int command);
        [PreserveSig] int SetShowCmd(int command);
        [PreserveSig] int GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int max, out int index);
        [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        [PreserveSig] int Resolve(IntPtr hwnd, uint flags);
        [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
