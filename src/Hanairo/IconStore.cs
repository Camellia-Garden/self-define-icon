using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Hanairo;

internal sealed record IconState(
    byte[]? Bytes = null,
    FileAttributes IniFlags = 0,
    bool FolderReadOnly = false,
    string? RegistryValue = null,
    RegistryValueKind RegistryKind = RegistryValueKind.None,
    bool RegistryKeyExists = false)
{
    public bool Matches(IconState other) =>
        SameContent(other) && FolderReadOnly == other.FolderReadOnly;

    public bool SameContent(IconState other) =>
        IniFlags == other.IniFlags &&
        RegistryValue == other.RegistryValue &&
        RegistryKind == other.RegistryKind &&
        (Bytes == null ? other.Bytes == null : other.Bytes != null && Bytes.AsSpan().SequenceEqual(other.Bytes));
}

internal sealed record IconEntry(Target Target, string Location, IconState Original, IconState Applied, string Icon);
internal sealed record IconOperation(IconEntry Entry, IconState Before, IconState After, bool Restore);
internal sealed record IconJournal(List<IconEntry> Entries, IconOperation? Pending)
{
    public const int CurrentVersion = 1;
    public int Version { get; init; } = CurrentVersion;
}

internal sealed class IconStore
{
    private const FileAttributes IniFlags = FileAttributes.Hidden | FileAttributes.System;
    private readonly string root;
    private readonly string journalPath;
    private IconJournal journal;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public IReadOnlyList<IconEntry> Entries => journal.Entries;

    public IconStore(string? dataDirectory = null)
    {
        root = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hanairo");
        Directory.CreateDirectory(root);
        journalPath = Path.Combine(root, "history.json");
        journal = File.Exists(journalPath)
            ? JsonSerializer.Deserialize<IconJournal>(File.ReadAllBytes(journalPath), JsonOptions)
                ?? throw new IOException("读不到复原记录。")
            : new([], null);
        if (journal.Version != IconJournal.CurrentVersion)
            throw new IOException("复原记录来自其他版本。请用兼容版本打开。");

        Recover();
    }

    public string SaveIcon(byte[] bytes)
    {
        string directory = Path.Combine(root, "Icons");
        Directory.CreateDirectory(directory);
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        string path = Path.Combine(directory, $"{hash}.ico");
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            WriteAtomic(path, bytes);
        return path;
    }

    public IconEntry Apply(Target target, string icon)
    {
        Recover();
        Target currentTarget = Target.FromPath(target.Path);
        if (currentTarget.Kind != target.Kind)
            throw new IOException("目标类型已变化，请重新添加。");

        var existing = journal.Entries.Find(entry =>
            SameTarget(entry.Target.Path, target.Path) || SameTarget(entry.Location, target.Path));
        if (existing != null)
            target = existing.Target;
        string location = existing?.Location ?? (target.Kind == TargetKind.File ? NewLinkPath(target.Path) : target.Path);
        IconState actual = ReadState(target, location);
        if (existing != null)
            CheckExpected(actual, existing.Applied);
        else if (target.Kind == TargetKind.File && actual.Bytes != null)
            throw new IOException("准备创建快捷方式时发现同名文件，请重试。");

        IconState next = Prepare(target, actual, icon);
        var entry = new IconEntry(target, location, existing?.Original ?? actual, next, icon);
        Execute(new IconOperation(entry, actual, next, false));
        return entry;
    }

    public void Restore(IconEntry entry)
    {
        Recover();
        var current = journal.Entries.Find(candidate => SameTarget(candidate.Target.Path, entry.Target.Path));
        if (current == null)
            return;

        IconState actual = ReadState(current.Target, current.Location);
        if (current.Target.Kind == TargetKind.File && actual.Bytes == null)
        {
            // The user already removed the generated shortcut.
            Finish(new IconOperation(current, actual, current.Original, true));
            return;
        }

        CheckExpected(actual, current.Applied);
        Execute(new IconOperation(current, actual, current.Original, true));
    }

    private void Execute(IconOperation operation)
    {
        IconState actual = ReadState(operation.Entry.Target, operation.Entry.Location);
        CheckExpected(actual, operation.Before);
        Persist(journal with { Pending = operation });
        try
        {
            WriteState(operation.Entry, actual, operation.After);
            CheckExpected(ReadState(operation.Entry.Target, operation.Entry.Location), operation.After);
        }
        catch
        {
            Recover();
            throw;
        }
        Finish(operation);
        Shell.RefreshItem(operation.Entry.Location, operation.Entry.Target.Kind == TargetKind.Drive);
    }

    private void Recover()
    {
        IconOperation? operation = journal.Pending;
        if (operation == null)
            return;

        IconEntry entry = operation.Entry;
        IconState actual = ReadState(entry.Target, entry.Location);
        if (actual.Matches(operation.After))
        {
            Finish(operation);
            Shell.RefreshItem(entry.Location, entry.Target.Kind == TargetKind.Drive);
            return;
        }

        if (!actual.Matches(operation.Before))
        {
            bool knownFolderContent = entry.Target.Kind == TargetKind.Folder &&
                (actual.SameContent(operation.Before) || actual.SameContent(operation.After) ||
                 MatchesAfterWithOldFlags(actual, operation));
            if (!knownFolderContent)
                throw new IOException($"上次操作未完成，且目标的图标设置已改变。已保留复原记录：{entry.Location}");

            WriteState(entry, actual, operation.Before);
            CheckExpected(ReadState(entry.Target, entry.Location), operation.Before);
            Shell.RefreshItem(entry.Location);
        }

        Persist(journal with { Pending = null });
    }

    private static bool MatchesAfterWithOldFlags(IconState actual, IconOperation operation)
    {
        // The INI write and its Hidden/System flags can complete in separate steps.
        var after = operation.After with { IniFlags = operation.Before.IniFlags };
        return actual.SameContent(after);
    }

    private void Finish(IconOperation operation)
    {
        List<IconEntry> entries = journal.Entries
            .Where(entry => !SameTarget(entry.Target.Path, operation.Entry.Target.Path))
            .ToList();
        if (!operation.Restore)
            entries.Insert(0, operation.Entry);

        Persist(new IconJournal(entries, null));
    }

    private void Persist(IconJournal next)
    {
        WriteAtomic(journalPath, JsonSerializer.SerializeToUtf8Bytes(next, JsonOptions));
        journal = next;
    }

    private static IconState ReadState(Target target, string location)
    {
        if (target.Kind == TargetKind.Drive)
        {
            using var key = Shell.DriveIconKey(location);
            object? value = key?.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value != null && value is not string)
                throw new IOException("该盘符已有特殊注册表设置，请先在 Windows 中检查。");

            return new IconState(
                RegistryValue: value as string,
                RegistryKind: value != null ? key!.GetValueKind("") : RegistryValueKind.None,
                RegistryKeyExists: key != null);
        }

        RejectLink(location);
        if (target.Kind == TargetKind.Folder)
        {
            FileAttributes attributes = File.GetAttributes(location);
            if (!attributes.HasFlag(FileAttributes.Directory))
                throw new IOException("路径已不再是文件夹。");

            string ini = Path.Combine(location, "desktop.ini");
            byte[]? bytes = ReadSmallFile(ini, optional: true);
            return new IconState(bytes, bytes == null ? 0 : File.GetAttributes(ini) & IniFlags,
                attributes.HasFlag(FileAttributes.ReadOnly));
        }

        return new IconState(ReadSmallFile(location, optional: target.Kind == TargetKind.File));
    }

    private static IconState Prepare(Target target, IconState state, string icon)
    {
        if (target.Kind == TargetKind.Drive)
            return new IconState(RegistryValue: $"{icon},0", RegistryKind: RegistryValueKind.String,
                RegistryKeyExists: true);

        string extension = target.Kind is TargetKind.Folder or TargetKind.InternetShortcut ? ".ini" : ".lnk";
        string stagingDirectory = target.Kind == TargetKind.File
            ? Path.GetDirectoryName(target.Path)!
            : Path.GetTempPath();
        string temp = Path.Combine(stagingDirectory, $".hanairo-{Guid.NewGuid():N}{extension}");
        try
        {
            if (target.Kind is TargetKind.Folder or TargetKind.InternetShortcut)
            {
                File.WriteAllBytes(temp, IniInput(state.Bytes));
                if (target.Kind == TargetKind.Folder)
                {
                    Shell.WriteIni(temp, ".ShellClassInfo", "IconResource", $"{icon},0");
                    Shell.WriteIni(temp, ".ShellClassInfo", "IconFile", null);
                    Shell.WriteIni(temp, ".ShellClassInfo", "IconIndex", null);
                    return new IconState(File.ReadAllBytes(temp), state.IniFlags | IniFlags, true);
                }

                Shell.WriteIni(temp, "InternetShortcut", "IconFile", icon);
                Shell.WriteIni(temp, "InternetShortcut", "IconIndex", "0");
            }
            else
            {
                if (target.Kind == TargetKind.File && state.Bytes == null)
                    Shell.CreateShortcut(temp, target.Path, icon);
                else
                    File.WriteAllBytes(temp, ShortcutEdit.WithIcon(state.Bytes!, icon));
                if (!SameTarget(Shell.ShortcutIcon(temp).Path, icon))
                    throw new IOException("Windows 未保存快捷方式的图标位置。");
            }

            return new IconState(File.ReadAllBytes(temp));
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static byte[] IniInput(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0)
            return Encoding.Unicode.GetPreamble();
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()))
            return [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3))];
        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.GetPreamble()))
            return [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2))];

        if (bytes.AsSpan().StartsWith(Encoding.Unicode.GetPreamble()))
            return bytes;

        // Win32 INIs without a BOM use the active ANSI code page. Upgrade the
        // staging copy so the new icon path can include Unicode characters.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(Encoding.GetEncoding(0).GetString(bytes))];
    }

    private static void WriteState(IconEntry entry, IconState before, IconState after)
    {
        string location = entry.Location;
        if (entry.Target.Kind == TargetKind.Drive)
        {
            using (var key = Shell.DriveIconKey(location, create: after.RegistryValue != null))
            {
                if (after.RegistryValue != null)
                    key!.SetValue("", after.RegistryValue, after.RegistryKind);
                else
                    key?.DeleteValue("", throwOnMissingValue: false);
            }
            if (!after.RegistryKeyExists)
                Shell.RemoveEmptyDriveIconKey(location);
            return;
        }

        bool folder = entry.Target.Kind == TargetKind.Folder;
        string path = folder ? Path.Combine(location, "desktop.ini") : location;

        if (after.Bytes == null)
        {
            if (before.Bytes != null)
                File.Delete(path);
        }
        else
        {
            if (before.Bytes == null || !before.Bytes.AsSpan().SequenceEqual(after.Bytes))
                WriteAtomic(path, after.Bytes, createOnly: before.Bytes == null, preserveMetadata: true);
            if (folder)
                SetFlags(path, IniFlags, after.IniFlags);
        }

        if (folder)
            SetFlags(location, FileAttributes.ReadOnly, after.FolderReadOnly ? FileAttributes.ReadOnly : 0);
    }

    private static void SetFlags(string path, FileAttributes mask, FileAttributes value)
    {
        FileAttributes current = File.GetAttributes(path);
        FileAttributes updated = (current & ~mask) | value;
        if (current != updated)
            File.SetAttributes(path, updated == 0 ? FileAttributes.Normal : updated);
    }

    private static byte[]? ReadSmallFile(string path, bool optional)
    {
        RejectLink(path);
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > 1024 * 1024)
                throw new IOException("图标配置超过 1 MB，无法安全备份。");
            byte[] bytes = new byte[(int)file.Length];
            file.ReadExactly(bytes);
            return bytes;
        }
        catch (FileNotFoundException) when (optional)
        {
            return null;
        }
        catch (DirectoryNotFoundException) when (optional)
        {
            return null;
        }
    }

    private static void RejectLink(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget != null)
            throw new IOException($"此路径是符号链接或目录联接，请选择实际位置：{path}");
    }

    private static string NewLinkPath(string source)
    {
        string directory = Path.GetDirectoryName(source)!;
        string name = Path.GetFileName(source);
        for (int number = 1; number <= 999; number++)
        {
            string suffix = number == 1 ? "" : $" ({number})";
            string path = Path.Combine(directory, $"{name} · 花色{suffix}.lnk");
            if (!File.Exists(path) && !Directory.Exists(path))
                return path;
        }
        throw new IOException("当前目录有太多同名快捷方式。");
    }

    private static bool SameTarget(string first, string second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private static void CheckExpected(IconState actual, IconState expected)
    {
        if (!actual.Matches(expected))
            throw new IOException("目标在工房之外有过修改。为保留这些修改，已保留当前内容和复原记录。");
    }

    private static void WriteAtomic(string path, byte[] bytes, bool createOnly = false, bool preserveMetadata = false)
    {
        string temp = Path.Combine(Path.GetDirectoryName(path)!, $".hanairo-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            // Apply metadata-preserving replacement to user files. For private
            // data, an atomic rename also works under EFS-encrypted AppData.
            if (preserveMetadata && !createOnly)
                File.Replace(temp, path, null);
            else
                File.Move(temp, path, overwrite: !createOnly);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
