using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using System.Diagnostics;
using StreamWID.Models;
using StreamWID.ViewModels;

namespace StreamWID.Views;

public partial class MainWindow : Window
{
    private bool _isFfmpegDialogOpen;

    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.StorageProvider = StorageProvider;
                vm.ExportCompleted -= ViewModel_ExportCompleted;
                vm.ExportCompleted += ViewModel_ExportCompleted;
                vm.FfmpegMissing -= ViewModel_FfmpegMissing;
                vm.FfmpegMissing += ViewModel_FfmpegMissing;
                if (vm.CheckForUpdatesCommand.CanExecute(null))
                    vm.CheckForUpdatesCommand.Execute(null);
                await vm.CheckFfmpegAvailableAsync();
            }
        };
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.Source is Visual visual && visual.GetVisualAncestors().OfType<Button>().Any())
            return;

        BeginMoveDrag(e);
    }

    private void CloseButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private void MinimizeButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private async void SupportProjectButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is not null)
            await launcher.LaunchUriAsync(new Uri("https://www.paypal.com/donate/?hosted_button_id=ZKTLLYY9ADWYQ"));
    }

    private async void LeaveReviewButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is not null)
            await launcher.LaunchUriAsync(new Uri("https://streamwid.com/reviews/new"));
    }

    private async void OpenLatestReleaseButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || string.IsNullOrWhiteSpace(vm.LatestReleaseUrl))
            return;

        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is not null)
            await launcher.LaunchUriAsync(new Uri(vm.LatestReleaseUrl));
    }

    private async void ViewModel_ExportCompleted(string folder)
    {
        await ShowExportCompleteDialogAsync(folder);
    }

    private async void ViewModel_FfmpegMissing()
    {
        await ShowFfmpegMissingDialogAsync();
    }

    private void AddClipsButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            ExecuteIfReady(vm.AddClipsCommand);
    }

    private void ClipsDropArea_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = HasDroppedFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void ClipsDropArea_Drop(object? sender, DragEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            var paths = e.DataTransfer.TryGetFiles()?
                .Select(file => file.Path.LocalPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>();

            if (paths is not null)
                await vm.AddClipPathsAsync(paths);
        }

        e.Handled = true;
    }

    private void AnalyzeSelectedButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            ExecuteIfReady(vm.AnalyzeSelectedCommand);
    }

    private void AnalyzeAllButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            ExecuteIfReady(vm.AnalyzeAllCommand);
    }

    private void ExportCutVideoButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            ExecuteIfReady(vm.ExportCutVideoCommand);
    }

    private void ExportPausesOnlyButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            ExecuteIfReady(vm.ExportPausesOnlyCommand);
    }

    private void ExportEdlButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            ExecuteIfReady(vm.ExportEdlCommand);
    }

    private void ExportCsvButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            ExecuteIfReady(vm.ExportCsvCommand);
    }

    private void RemoveClipMenuItem_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm &&
            sender is MenuItem { Tag: MediaClip clip } &&
            vm.RemoveClipCommand.CanExecute(clip))
        {
            vm.RemoveClipCommand.Execute(clip);
        }
    }

    private void TimelineTrackContainer_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.SetTimelineTrackWidth(e.NewSize.Width);
    }

    private static void ExecuteIfReady(System.Windows.Input.ICommand command)
    {
        if (command.CanExecute(null))
            command.Execute(null);
    }

    private static bool HasDroppedFiles(DragEventArgs e) => e.DataTransfer.TryGetFiles()?.Any() == true;

    private void PlaySegmentButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm &&
            sender is Button { Tag: TimelineSegment segment } &&
            vm.PlaySegmentCommand.CanExecute(segment))
        {
            vm.PlaySegmentCommand.Execute(segment);
        }
    }

    private void TrackSegmentButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm &&
            sender is Button { Tag: TimelineSegmentBlock { Segment: { Kind: SegmentKind.Speech } segment } } &&
            vm.PlaySegmentCommand.CanExecute(segment))
        {
            vm.PlaySegmentCommand.Execute(segment);
        }
    }

    private void DismissToastButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm &&
            sender is Button { Tag: string message })
        {
            vm.DismissToast(message);
        }
    }

    private void SegmentRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.Source is Visual visual && visual.GetVisualAncestors().OfType<Button>().Any())
            return;

        if (sender is Border { Tag: TimelineSegment segment })
        {
            segment.Remove = !segment.Remove;
            e.Handled = true;
        }
    }

    private void ResizeTop_PointerPressed(object? sender, PointerPressedEventArgs e) => BeginResize(WindowEdge.North, e);
    private void ResizeBottom_PointerPressed(object? sender, PointerPressedEventArgs e) => BeginResize(WindowEdge.South, e);
    private void ResizeLeft_PointerPressed(object? sender, PointerPressedEventArgs e) => BeginResize(WindowEdge.West, e);
    private void ResizeRight_PointerPressed(object? sender, PointerPressedEventArgs e) => BeginResize(WindowEdge.East, e);
    private void ResizeTopLeft_PointerPressed(object? sender, PointerPressedEventArgs e) => BeginResize(WindowEdge.NorthWest, e);
    private void ResizeTopRight_PointerPressed(object? sender, PointerPressedEventArgs e) => BeginResize(WindowEdge.NorthEast, e);
    private void ResizeBottomLeft_PointerPressed(object? sender, PointerPressedEventArgs e) => BeginResize(WindowEdge.SouthWest, e);
    private void ResizeBottomRight_PointerPressed(object? sender, PointerPressedEventArgs e) => BeginResize(WindowEdge.SouthEast, e);

    private void BeginResize(WindowEdge edge, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        BeginResizeDrag(edge, e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.ExportCompleted -= ViewModel_ExportCompleted;
            vm.FfmpegMissing -= ViewModel_FfmpegMissing;
            vm.Shutdown();
        }

        base.OnClosed(e);
    }

    private async Task ShowExportCompleteDialogAsync(string folder)
    {
        var dialog = new Window
        {
            Title = "Export Complete",
            Width = 500,
            Height = 330,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Colors.Transparent),
            SystemDecorations = SystemDecorations.None,
            Content = BuildExportDialogContent(folder)
        };

        await dialog.ShowDialog(this);
    }

    private async Task ShowFfmpegMissingDialogAsync()
    {
        if (_isFfmpegDialogOpen)
            return;

        _isFfmpegDialogOpen = true;
        try
        {
            var dialog = new Window
            {
                Title = "FFmpeg Required",
                Width = 460,
                Height = 260,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Colors.Transparent),
                SystemDecorations = SystemDecorations.None,
                Content = BuildFfmpegMissingDialogContent()
            };

            await dialog.ShowDialog(this);
        }
        finally
        {
            _isFfmpegDialogOpen = false;
        }
    }

    private Control BuildFfmpegMissingDialogContent()
    {
        var title = new TextBlock
        {
            Text = "FFmpeg is missing",
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#E5ECF7"))
        };

        var closeButton = new Button
        {
            Content = "x",
            Width = 32,
            Height = 28,
            MinWidth = 32,
            MinHeight = 28,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            Foreground = new SolidColorBrush(Color.Parse("#CBD5E1")),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        closeButton.Click += (_, _) => ((Window)closeButton.GetVisualRoot()!).Close();

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(14, 10),
            Children = { title, closeButton }
        };
        Grid.SetColumn(closeButton, 1);

        var headerBand = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#101722")),
            CornerRadius = new CornerRadius(10, 10, 0, 0),
            Child = header
        };
        headerBand.PointerPressed += (_, e) =>
        {
            if (e.Source is Visual visual && visual.GetVisualAncestors().OfType<Button>().Any())
                return;

            if (e.GetCurrentPoint(headerBand).Properties.IsLeftButtonPressed &&
                headerBand.GetVisualRoot() is Window window)
            {
                window.BeginMoveDrag(e);
            }
        };

        var message = new TextBlock
        {
            Text = "StreamWID needs FFmpeg, FFprobe, and FFplay to analyze, preview, and export clips. Download FFmpeg, make sure it is available in PATH, then restart the app.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#B8C2D6"))
        };

        var downloadButton = new Button
        {
            Content = "Download FFmpeg",
            Width = 180,
            MinHeight = 36,
            Padding = new Thickness(14, 7),
            Background = new SolidColorBrush(Color.Parse("#2A3447")),
            Foreground = new SolidColorBrush(Color.Parse("#E5ECF7")),
            BorderBrush = new SolidColorBrush(Color.Parse("#4B5C76")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        downloadButton.Click += async (_, _) =>
        {
            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            if (launcher is not null)
                await launcher.LaunchUriAsync(new Uri("https://ffmpeg.org/download.html"));
        };

        var body = new StackPanel
        {
            Spacing = 16,
            Margin = new Thickness(18),
            Children = { message, downloadButton }
        };

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#151922")),
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            Child = new StackPanel
            {
                Children = { headerBand, body }
            },
        };
    }

    private Control BuildExportDialogContent(string folder)
    {
        var title = new TextBlock
        {
            Text = "Your export is ready",
            FontSize = 19,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#E5ECF7"))
        };

        var headerCloseButton = new Button
        {
            Content = "×",
            Width = 32,
            Height = 28,
            MinWidth = 32,
            MinHeight = 28,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            Foreground = new SolidColorBrush(Color.Parse("#CBD5E1")),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Cursor = new Cursor(StandardCursorType.Hand),
            FontSize = 16,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        headerCloseButton.Click += (_, _) => ((Window)headerCloseButton.GetVisualRoot()!).Close();

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(18, 12),
            Children = { title, headerCloseButton }
        };
        Grid.SetColumn(headerCloseButton, 1);

        var headerBand = new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#142034"), 0),
                    new GradientStop(Color.Parse("#111821"), 1)
                }
            },
            CornerRadius = new CornerRadius(10, 10, 0, 0),
            Child = header
        };
        headerBand.PointerPressed += (_, e) =>
        {
            if (e.Source is Visual visual && visual.GetVisualAncestors().OfType<Button>().Any())
                return;

            if (e.GetCurrentPoint(headerBand).Properties.IsLeftButtonPressed &&
                headerBand.GetVisualRoot() is Window window)
            {
                window.BeginMoveDrag(e);
            }
        };

        var message = new TextBlock
        {
            Text = "The finished file has been saved successfully.",
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#B8C2D6"))
        };

        var folderLabel = new TextBlock
        {
            Text = folder,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(Color.Parse("#8EA0BA")),
            FontSize = 12
        };

        var folderPill = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#101722")),
            BorderBrush = new SolidColorBrush(Color.Parse("#29364A")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7),
            Margin = new Thickness(0, 8, 0, 0),
            Child = folderLabel
        };

        var copy = new StackPanel
        {
            Spacing = 2,
            Children = { message, folderPill }
        };

        var successBadge = new Border
        {
            Width = 54,
            Height = 54,
            CornerRadius = new CornerRadius(27),
            Background = new SolidColorBrush(Color.Parse("#173525")),
            BorderBrush = new SolidColorBrush(Color.Parse("#2A7B4A")),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = "✓",
                FontSize = 28,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.Parse("#6EE7A1")),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var summary = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 14,
            Children = { successBadge, copy }
        };
        Grid.SetColumn(copy, 1);

        var support = new TextBlock
        {
            Text = "StreamWID is independent. If it saved you editing time, a small contribution helps keep it moving.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#B8C2D6"))
        };

        var openFolderButton = new Button
        {
            Content = "Open Folder",
            MinHeight = 40,
            Padding = new Thickness(16, 8),
            Background = new SolidColorBrush(Color.Parse("#D7E7FF")),
            Foreground = new SolidColorBrush(Color.Parse("#0E1724")),
            BorderBrush = new SolidColorBrush(Color.Parse("#D7E7FF")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        openFolderButton.Click += (_, _) =>
        {
            OpenFolder(folder);
            ((Window)openFolderButton.GetVisualRoot()!).Close();
        };

        var supportButton = new Button
        {
            Content = "♥ Support",
            Width = 110,
            MinHeight = 34,
            Padding = new Thickness(12, 6),
            Background = new SolidColorBrush(Color.Parse("#2D2437")),
            Foreground = new SolidColorBrush(Color.Parse("#F3D9FF")),
            BorderBrush = new SolidColorBrush(Color.Parse("#574166")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        supportButton.Click += async (_, _) =>
        {
            try
            {
                var launcher = TopLevel.GetTopLevel(this)?.Launcher;
                if (launcher is not null)
                    await launcher.LaunchUriAsync(new Uri("https://www.paypal.com/donate/?hosted_button_id=ZKTLLYY9ADWYQ"));
            }
            finally
            {
                ((Window)supportButton.GetVisualRoot()!).Close();
            }
        };

        var supportContent = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 12,
            Children = { support, supportButton }
        };
        Grid.SetColumn(supportButton, 1);

        var supportPanel = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#191D29")),
            BorderBrush = new SolidColorBrush(Color.Parse("#2A3140")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10),
            Child = supportContent
        };

        var body = new StackPanel
        {
            Spacing = 16,
            Margin = new Thickness(22, 20, 22, 22),
            Children = { summary, openFolderButton, supportPanel }
        };

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#141922")),
            BorderBrush = new SolidColorBrush(Color.Parse("#2C3647")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            Child = new StackPanel
            {
                Children = { headerBand, body }
            },
        };
    }

    private static void OpenFolder(string folder)
    {
        if (!Directory.Exists(folder))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true
        });
    }
}
