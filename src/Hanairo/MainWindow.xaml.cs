using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Hanairo;

public partial class MainWindow : Window
{
    private readonly IconStore store;
    private readonly ObservableCollection<Target> queue = [];
    private IconComposition? artwork;
    private string[] report = [];
    private bool readingMenuState;

    public MainWindow()
    {
        InitializeComponent();
        store = new IconStore();
        VersionLabel.Text = $"Hanairo  ·  {typeof(MainWindow).Assembly.GetName().Version!.ToString(3)}";
        QueueList.ItemsSource = queue;
        UpdateQueue();
        UpdateHistory();
    }

    internal void RequestTarget(string path)
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();

        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return;
        WorkbenchTab.IsChecked = true;
        ShowPage(WorkbenchPage);
        AddTargets([path]);
    }

    private void ShowPage(Panel page)
    {
        foreach (Panel panel in new[] { WorkbenchPage, HistoryPage, SettingsPage })
            panel.Visibility = panel == page ? Visibility.Visible : Visibility.Collapsed;
        PageScroll.ScrollToTop();
    }

    private void OpenWorkbench_Checked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
            ShowPage(WorkbenchPage);
    }

    private void OpenHistory_Checked(object sender, RoutedEventArgs e)
    {
        UpdateHistory();
        ShowPage(HistoryPage);
    }

    private void OpenSettings_Checked(object sender, RoutedEventArgs e)
    {
        Run(ReadMenuState);
        ShowPage(SettingsPage);
    }

    private void ReadMenuState()
    {
        readingMenuState = true;
        try { ContextMenuCheck.IsChecked = Shell.MenuEnabled(); }
        finally { readingMenuState = false; }
    }

    private void ContextMenu_Changed(object sender, RoutedEventArgs e)
    {
        if (readingMenuState)
            return;
        Run(() =>
        {
            try
            {
                Shell.SetMenu(ContextMenuCheck.IsChecked == true);
                SetStatus(ContextMenuCheck.IsChecked == true ? "已启用资源管理器右键菜单。" : "已移除资源管理器右键菜单。");
            }
            finally
            {
                ReadMenuState();
            }
        });
    }

    private void RefreshShell_Click(object sender, RoutedEventArgs e)
    {
        Shell.RefreshAll();
        foreach (IconEntry entry in store.Entries)
            Shell.RefreshItem(entry.Location);
        SetStatus("已请求 Windows 刷新桌面、资源管理器和任务栏图标。");
    }

    private void Files_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择文件或快捷方式",
            Filter = "所有文件|*.*|Windows 快捷方式|*.lnk;*.url",
            Multiselect = true,
            DereferenceLinks = false
        };
        if (dialog.ShowDialog(this) == true)
            AddTargets(dialog.FileNames);
    }

    private void Folders_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择文件夹", Multiselect = true };
        if (dialog.ShowDialog(this) == true)
            AddTargets(dialog.FolderNames);
    }

    private void Drives_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var choices = DriveInfo.GetDrives()
            .Where(drive => drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Ram)
            .Select(drive => drive.Name)
            .ToArray();
        ChooseTargets("选择盘符", choices, "没有找到本地盘符。");
    });

    private void Pinned_Click(object sender, RoutedEventArgs e) => Run(() =>
        ChooseTargets("选择任务栏固定项", PinnedLinks(),
            "没有找到可以编辑的 Win32 固定快捷方式。"));

    private static string[] PinnedLinks() => Directory.Exists(Target.PinnedDirectory)
        ? Directory.GetFiles(Target.PinnedDirectory, "*.lnk").Order().ToArray() : [];

    private void ChooseTargets(string title, string[] paths, string empty)
    {
        if (paths.Length == 0)
        {
            SetStatus(empty);
            return;
        }

        var choices = paths.Select(path =>
            new Choice(path, path.Length == 3 ? path : Path.GetFileNameWithoutExtension(path))).ToArray();
        var panel = new DockPanel { Margin = new Thickness(18) };
        var tip = new TextBlock { Text = "点击要添加的项目，可选多项。", Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(tip, Dock.Top);
        panel.Children.Add(tip);
        var add = new Button
        {
            Content = "添加选中项", IsDefault = true, IsEnabled = false, Margin = new Thickness(0, 10, 0, 0),
            Style = (Style)FindResource("Primary")
        };
        DockPanel.SetDock(add, Dock.Bottom);
        panel.Children.Add(add);
        var list = new ListBox
        {
            ItemsSource = choices, DisplayMemberPath = nameof(Choice.Name), SelectionMode = SelectionMode.Multiple

        };
        list.SelectionChanged += (_, _) => add.IsEnabled = list.SelectedItems.Count > 0;
        panel.Children.Add(list);
        var picker = Modal(title, panel, 400, 350);
        add.Click += (_, _) => picker.DialogResult = true;
        if (picker.ShowDialog() == true)
            AddTargets(list.SelectedItems.Cast<Choice>().Select(choice => choice.Path));
    }

    private void AddTargets(IEnumerable<string> paths)
    {
        var errors = new List<string>();
        var added = new List<Target>();
        foreach (string path in paths)
        {
            try
            {
                Target target = Target.FromPath(path);
                if (Queue(target))
                    added.Add(target);
            }
            catch (Exception error)
            {
                errors.Add($"{path}{Environment.NewLine}{error.Message}");
            }
        }

        int pinned = 0;
        if (SyncPinCheck.IsChecked == true && added.Any(CanMatchPin))
        {
            try
            {
                var links = PinnedLinks().Select(path => (path, launch: Shell.ShortcutLaunch(path))).ToArray();
                foreach (Target target in added.Where(CanMatchPin))
                {
                    var launch = target.Kind == TargetKind.File ? (target.Path, "") : Shell.ShortcutLaunch(target.Path);
                    if (launch is not { } source)
                        continue;

                    var matching = links.Where(link => link.launch is { } destination &&
                        string.Equals(source.Item1, destination.Path, StringComparison.OrdinalIgnoreCase) &&
                        source.Item2 == destination.Arguments).Take(2).ToArray();
                    if (matching.Length == 1 && Queue(Target.FromPath(matching[0].path)))
                        pinned++;
                }
            }
            catch (Exception error)
            {
                errors.Add($"读取任务栏固定项时遇到问题：{error.Message}");
            }
        }

        UpdateQueue();
        string message = added.Count == 0 ? "没有添加新目标。" : $"已添加 {added.Count} 个目标。";
        if (pinned > 0)
            message += $"已匹配 {pinned} 个任务栏固定项。";
        SetStatus(message, errors);
    }

    private static bool CanMatchPin(Target target) => target.Kind == TargetKind.Shortcut ||
        (target.Kind == TargetKind.File && Path.GetExtension(target.Path).Equals(".exe", StringComparison.OrdinalIgnoreCase));

    private bool Queue(Target target)
    {
        if (queue.Any(item => string.Equals(item.Path, target.Path, StringComparison.OrdinalIgnoreCase)))
            return false;
        queue.Add(target);
        return true;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        queue.Clear();
        UpdateQueue();
        SetStatus("已清空待换装队列。");
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).DataContext is Target target)
            queue.Remove(target);
        UpdateQueue();
    }

    private void UpdateQueue()
    {
        DropHint.Visibility = queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.IsEnabled = queue.Count > 0;
        FileHint.Visibility = queue.Any(target => target.Kind == TargetKind.File) ? Visibility.Visible : Visibility.Collapsed;
        ApplyButton.IsEnabled = queue.Count > 0 && artwork != null;
        ApplyButton.Content = queue.Count > 0 ? $"应用到 {queue.Count} 个目标" : "应用图标";
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入图标图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.ico;*.bmp;*.gif;*.tif;*.tiff;*.webp|所有文件|*.*"
        };
        if (dialog.ShowDialog(this) == true)
            ImportImage(dialog.FileName);
    }

    private void ImportImage(string path) => Run(() =>
        EditImage(new IconComposition(IconImage.LoadSource(path)), Path.GetFileName(path)));

    private void EditImage_Click(object sender, RoutedEventArgs e)
    {
        if (artwork is { } composition)
            Run(() => EditImage(composition, ImageName.Text));
    }

    private void EditImage(IconComposition composition, string name)
    {
        var editor = new IconEditor(composition, name) { Owner = this };
        if (editor.ShowDialog() == true)
            SetArtwork(editor.Composition, name);
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        string key = (string)button.Tag;
        Run(() => SetArtwork(new IconComposition(IconImage.FromAsset($"{key}.png")), (string)button.ToolTip));
    }

    private void SetArtwork(IconComposition composition, string name)
    {
        IconPreview.Source = IconImage.Render(composition, 96);
        artwork = composition;
        ImageHint.Visibility = Visibility.Collapsed;
        EditImageButton.Visibility = Visibility.Visible;
        ImageName.Text = name;
        ImageName.ToolTip = name;
        UpdateQueue();
        SetStatus("图片已就绪。");
    }

    private static string[] DroppedPaths(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];

    private void File_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Targets_Drop(object sender, DragEventArgs e)
    {
        AddTargets(DroppedPaths(e));
        e.Handled = true;
    }

    private void Image_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DroppedPaths(e) is [var path])
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => ImportImage(path));
        else
            SetStatus("请一次拖入一张图片。");
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (artwork == null || queue.Count == 0)
            return;

        Run(() =>
        {
            string icon = store.SaveIcon(IconImage.Encode(artwork));
            Target[] batch = queue.ToArray();
            int done = ApplyBatch(batch, target =>
            {
                store.Apply(target, icon);
                queue.Remove(target);
            }, target => target.Path, "已换装");
            UpdateQueue();
            UpdateHistory();
            if (done > 0 && batch.Any(target => target.Kind == TargetKind.Pinned))
                Shell.RefreshAll();
        });
    }

    private void RestoreOne_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).DataContext is HistoryRow row)
            Restore([row.Entry]);
    }

    private void RestoreAll_Click(object sender, RoutedEventArgs e)
    {
        int count = store.Entries.Count;
        if (count > 0 && MessageBox.Show(this, $"将这 {count} 项复原到第一次换装前的图标？", "全部复原",
                MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK)
            Restore(store.Entries.ToArray());
    }

    private void Restore(IconEntry[] batch)
    {
        int done = ApplyBatch(batch, store.Restore, entry => entry.Location, "已复原");
        UpdateHistory();
        if (done > 0 && batch.Any(entry => entry.Target.Kind == TargetKind.Pinned))
            Shell.RefreshAll();
    }

    private int ApplyBatch<T>(T[] items, Action<T> action, Func<T, string> path, string verb)
    {
        var errors = new List<string>();
        int done = 0;
        foreach (T item in items)
        {
            try
            {
                action(item);
                done++;
            }
            catch (Exception error)
            {
                errors.Add($"{path(item)}{Environment.NewLine}{error.Message}");
            }
        }
        string message = $"{verb} {done} / {items.Length} 项。";
        SetStatus(message, errors);
        return done;
    }

    private void UpdateHistory()
    {
        HistoryList.ItemsSource = store.Entries.Select(entry => new HistoryRow(entry, ReadPreview(entry.Icon))).ToArray();
        HistoryHint.Visibility = store.Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RestoreAllButton.IsEnabled = store.Entries.Count > 0;
    }

    private static BitmapSource? ReadPreview(string path)
    {
        try { return IconImage.Load(path); }
        catch (Exception) { return null; }
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception error) { SetStatus("操作未完成。", [error.Message]); }
    }

    private void SetStatus(string message, IEnumerable<string>? details = null)
    {
        report = details?.ToArray() ?? [];
        Status.Text = report.Length == 0 ? message : $"{message} 有 {report.Length} 项需要查看。";
        DetailsButton.Visibility = report.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        var details = new TextBox
        {
            Text = string.Join(Environment.NewLine + Environment.NewLine, report),
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16), Padding = new Thickness(10), VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Modal("操作结果", details, 570, 380).ShowDialog();
    }

    private Window Modal(string title, UIElement content, double width, double height) => new()
    {
        Title = title, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Width = width, Height = height, Content = content, ShowInTaskbar = false,
        Background = (Brush)FindResource("Paper"), FontFamily = FontFamily, FontSize = FontSize
    };

    private sealed record Choice(string Path, string Name);
    private sealed record HistoryRow(IconEntry Entry, BitmapSource? Preview);
}
