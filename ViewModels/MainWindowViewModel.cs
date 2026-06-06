using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StreamWID.Models;
using StreamWID.Services;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace StreamWID.ViewModels;

public sealed record DetectionPreset(string Name, double ThresholdDb, double MinSilenceSeconds, double KeepPaddingSeconds);

public partial class MainWindowViewModel : ObservableObject
{
    private readonly FfmpegService _ffmpeg = new();
    private readonly UpdateChecker _updateChecker = new("mmlTools", "StreamWID");
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly List<TimelineSegment> _watchedSegments = new();
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(125) };
    private readonly List<string> _previewFrames = new();
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _thumbnailCts;
    private Process? _previewAudioProcess;
    private string? _previewFolder;
    private int _previewFrameIndex;
    private double _timelineTrackWidth = 760;
    private static readonly HashSet<string> SupportedVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".mov",
        ".mkv",
        ".webm",
        ".avi"
    };

    public ObservableCollection<MediaClip> Clips { get; } = new();
    public ObservableCollection<string> Toasts { get; } = new();
    public ObservableCollection<TimelineSegmentBlock> TimelineBlocks { get; } = new();
    public List<DetectionPreset> DetectionPresets { get; } = new();

    public event Action<string>? ExportCompleted;
    public event Action? FfmpegMissing;

    [ObservableProperty] private MediaClip? selectedClip;
    [ObservableProperty] private double thresholdDb = -35;
    [ObservableProperty] private double minSilenceSeconds = 0.45;
    [ObservableProperty] private double keepPaddingSeconds = 0.08;
    [ObservableProperty] private double resolveFps = 25;
    [ObservableProperty] private bool useAdaptiveThreshold = true;
    [ObservableProperty] private double adaptiveThresholdDb;
    [ObservableProperty] private DetectionPreset? selectedDetectionPreset;

    private readonly Dictionary<string, (DateTime LastWriteTimeUtc, List<TimelineSegment> Segments, double ThresholdDb, double MinSilenceSeconds, double KeepPaddingSeconds, bool UseAdaptiveThreshold)> _analysisCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _analysisSemaphore = new(2);
    [ObservableProperty] private string status = "Add clips to begin.";
    [ObservableProperty] private bool reencodeExports = true;
    [ObservableProperty] private bool isPreviewOpen;
    [ObservableProperty] private string previewTitle = "";
    [ObservableProperty] private string previewDetails = "";
    [ObservableProperty] private Bitmap? previewFrame;
    [ObservableProperty] private bool isPreviewLoading;
    [ObservableProperty] private bool isUpdateAvailable;
    [ObservableProperty] private string latestVersion = "";
    [ObservableProperty] private string latestReleaseUrl = "";

    public MainWindowViewModel()
    {
        DetectionPresets.Add(new DetectionPreset("Podcast / Voice", -35, 0.45, 0.08));
        DetectionPresets.Add(new DetectionPreset("Interview", -33, 0.35, 0.10));
        DetectionPresets.Add(new DetectionPreset("Lecture / Presentation", -38, 0.55, 0.12));
        DetectionPresets.Add(new DetectionPreset("Stream / Gameplay", -28, 0.80, 0.10));

        SelectedDetectionPreset = DetectionPresets.FirstOrDefault();
        AdaptiveThresholdDb = ThresholdDb;
        _previewTimer.Tick += (_, _) => AdvancePreviewFrame();
    }

    public IStorageProvider? StorageProvider { get; set; }

    public async Task CheckFfmpegAvailableAsync()
    {
        try
        {
            await _ffmpeg.CheckToolsAvailableAsync();
        }
        catch (Exception ex) when (FfmpegService.IsMissingToolException(ex))
        {
            Status = "FFmpeg was not found. Install FFmpeg and make sure it is available in PATH.";
            FfmpegMissing?.Invoke();
        }
    }

    public async Task AddClipPathsAsync(IEnumerable<string> paths)
    {
        var added = 0;
        var newClips = new List<MediaClip>();

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                continue;

            if (!SupportedVideoExtensions.Contains(Path.GetExtension(path)))
                continue;

            if (Clips.Any(x => string.Equals(x.FilePath, path, StringComparison.OrdinalIgnoreCase)))
                continue;

            var clip = new MediaClip
            {
                FilePath = path,
                FileName = Path.GetFileName(path),
                Status = "Waiting",
                DurationSeconds = 0
            };

            Clips.Add(clip);
            newClips.Add(clip);
            added++;
        }

        SelectedClip ??= Clips.FirstOrDefault();

        if (added > 0)
            Status = added == 1 ? "Added 1 clip." : $"Added {added} clips.";

        await Task.WhenAll(newClips.Select(LoadClipThumbnailAsync));
    }

    private async Task LoadClipThumbnailAsync(MediaClip clip)
    {
        try
        {
            var path = await _ffmpeg.ExtractClipThumbnailAsync(clip.FilePath);
            if (!string.IsNullOrWhiteSpace(path))
                clip.Thumbnail = new Bitmap(path);
        }
        catch
        {
            // Thumbnail is optional.
        }
    }

    public void SetTimelineTrackWidth(double width)
    {
        if (width <= 0 || Math.Abs(width - _timelineTrackWidth) < 0.5)
            return;

        _timelineTrackWidth = width;
        RefreshTimelineBlocks();
    }

    public bool AllSectionsSelected
    {
        get
        {
            var sections = SelectedClip?.Segments.ToList();
            return sections?.Count > 0 && sections.All(x => x.Remove);
        }
        set
        {
            if (SelectedClip is null)
                return;

            foreach (var segment in SelectedClip.Segments)
                segment.Remove = value;

            NotifySelectionStateChanged();
        }
    }

    public string ToggleAllSpeechLabel => AreAllSpeechSectionsRemoved ? "Deselect All Speech" : "Select All Speech";
    public string ToggleAllPausesLabel => AreAllPauseSectionsRemoved ? "Deselect All Pauses" : "Select All Pauses";

    private bool AreAllSpeechSectionsRemoved
    {
        get
        {
            var speech = SelectedClip?.Segments.Where(x => x.Kind == SegmentKind.Speech).ToList();
            return speech?.Count > 0 && speech.All(x => x.Remove);
        }
    }

    private bool AreAllPauseSectionsRemoved
    {
        get
        {
            var pauses = SelectedClip?.Segments.Where(x => x.Kind == SegmentKind.Silence).ToList();
            return pauses?.Count > 0 && pauses.All(x => x.Remove);
        }
    }

    partial void OnSelectedClipChanged(MediaClip? value)
    {
        WatchPauseSelection(value);
        RefreshTimelineBlocks();
        _ = LoadTimelineThumbnailsAsync(value);
        NotifySelectionStateChanged();
    }

    partial void OnSelectedDetectionPresetChanged(DetectionPreset? value)
    {
        if (value is null)
            return;

        ThresholdDb = value.ThresholdDb;
        MinSilenceSeconds = value.MinSilenceSeconds;
        KeepPaddingSeconds = value.KeepPaddingSeconds;
    }

    partial void OnUseAdaptiveThresholdChanged(bool value)
    {
        _ = ShowToastAsync(value
            ? $"Adaptive threshold enabled. Suggestions will be applied from audio analysis."
            : "Adaptive threshold disabled. Manual threshold will be used.");
    }

    partial void OnStatusChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            _ = ShowToastAsync(value);
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            _settings.LastKnownVersion = _updateChecker.CurrentVersion.ToString();
            _settings.Save();

            var update = await _updateChecker.CheckLatestReleaseAsync();
            if (update is null)
            {
                IsUpdateAvailable = false;
                return;
            }

            LatestVersion = update.Version;
            LatestReleaseUrl = update.Url;
            IsUpdateAvailable = true;
            Status = $"StreamWID {update.Version} is available.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Update check failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task AddClipsAsync()
    {
        if (StorageProvider is null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            Title = "Select clips",
            FileTypeFilter = new[] { new FilePickerFileType("Video files") { Patterns = new[] { "*.mp4", "*.mov", "*.mkv", "*.webm", "*.avi" } } }
        });

        await AddClipPathsAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    [RelayCommand]
    private async Task AnalyzeSelectedAsync()
    {
        if (SelectedClip is null) return;
        await AnalyzeClip(SelectedClip);
    }

    [RelayCommand]
    private async Task AnalyzeAllAsync()
    {
        var analysisTasks = Clips.Select(async clip =>
        {
            await _analysisSemaphore.WaitAsync();
            try
            {
                await AnalyzeClip(clip);
            }
            finally
            {
                _analysisSemaphore.Release();
            }
        });

        await Task.WhenAll(analysisTasks);
    }

    [RelayCommand]
    private void ToggleAllSpeech()
    {
        if (SelectedClip is null)
            return;

        var remove = !AreAllSpeechSectionsRemoved;
        foreach (var s in SelectedClip.Segments.Where(x => x.Kind == SegmentKind.Speech))
            s.Remove = remove;

        NotifySelectionStateChanged();
    }

    [RelayCommand]
    private void ToggleAllPauses()
    {
        if (SelectedClip is null)
            return;

        var remove = !AreAllPauseSectionsRemoved;
        foreach (var s in SelectedClip.Segments.Where(x => x.Kind == SegmentKind.Silence))
            s.Remove = remove;

        NotifySelectionStateChanged();
    }

    [RelayCommand]
    private void RemoveClip(MediaClip clip)
    {
        if (!Clips.Contains(clip))
            return;

        if (ReferenceEquals(SelectedClip, clip))
            ClosePreview();

        ClearSegmentThumbnails(clip.Segments);
        clip.Thumbnail = null;

        Clips.Remove(clip);
        SelectedClip = Clips.FirstOrDefault();
        Status = $"Removed {clip.FileName} from the list.";
    }

    [RelayCommand]
    private async Task PlaySegmentAsync(TimelineSegment segment)
    {
        if (segment.Kind != SegmentKind.Speech)
            return;

        var clip = Clips.FirstOrDefault(c => c.Segments.Contains(segment));
        if (clip is null)
            return;

        try
        {
            ClosePreview();
            PreviewTitle = clip.FileName;
            PreviewDetails = $"{TimelineSegment.TimeFmt(segment.Start)} - {TimelineSegment.TimeFmt(segment.End)} ({segment.Duration:0.00}s)";
            IsPreviewOpen = true;
            IsPreviewLoading = true;
            Status = $"Playing {TimelineSegment.TimeFmt(segment.Start)} - {TimelineSegment.TimeFmt(segment.End)} from {clip.FileName}.";

            _previewCts = new CancellationTokenSource();
            _previewFolder = Path.Combine(Path.GetTempPath(), "silence-cutter-preview-" + Guid.NewGuid().ToString("N"));
            var frames = await _ffmpeg.ExtractPreviewFramesAsync(clip.FilePath, segment, _previewFolder, ct: _previewCts.Token);

            _previewFrames.Clear();
            _previewFrames.AddRange(frames);
            _previewFrameIndex = 0;

            if (_previewFrames.Count > 0)
            {
                SetPreviewFrame(_previewFrames[0]);
                var audioFile = await _ffmpeg.ExtractSegmentAudioPreviewAsync(clip.FilePath, segment, _previewFolder, _previewCts.Token);
                _previewAudioProcess = _ffmpeg.StartAudioPlayback(audioFile);
                _previewTimer.Start();
            }

            IsPreviewLoading = false;
        }
        catch (Exception ex)
        {
            ClosePreview();

            if (HandleMissingFfmpeg(ex))
                return;

            Status = $"Could not play segment: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ClosePreview()
    {
        _previewTimer.Stop();
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = null;
        StopPreviewAudio();
        _previewFrames.Clear();
        _previewFrameIndex = 0;
        SetPreviewFrame(null);
        IsPreviewOpen = false;
        IsPreviewLoading = false;

        if (_previewFolder is not null)
        {
            try { Directory.Delete(_previewFolder, true); } catch { }
            _previewFolder = null;
        }
    }

    public void Shutdown()
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = null;
        ClosePreview();
    }

    [RelayCommand]
    private async Task ExportCutVideoAsync()
    {
        if (SelectedClip is null || StorageProvider is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export cut video",
            SuggestedFileName = Path.GetFileNameWithoutExtension(SelectedClip.FileName) + "_cut.mp4",
            DefaultExtension = "mp4"
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        Status = "Rendering cut video...";
        try
        {
            await _ffmpeg.ExportCutVideoAsync(SelectedClip.FilePath, SelectedClip.Segments, path, ReencodeExports);
            Status = "Exported cut video.";
            NotifyExportCompleted(path);
        }
        catch (Exception ex)
        {
            if (!HandleMissingFfmpeg(ex))
                Status = $"Could not export cut video: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportPausesOnlyAsync()
    {
        if (SelectedClip is null || StorageProvider is null) return;
        var folder = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose folder for pause clips", AllowMultiple = false });
        var path = folder.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        Status = "Exporting pause clips...";
        try
        {
            await _ffmpeg.ExportPausesOnlyAsync(SelectedClip.FilePath, SelectedClip.Segments, path);
            Status = "Exported pause clips.";
            NotifyExportCompleted(path);
        }
        catch (Exception ex)
        {
            if (!HandleMissingFfmpeg(ex))
                Status = $"Could not export pause clips: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportEdlAsync()
    {
        if (SelectedClip is null || StorageProvider is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export EDL cut list",
            SuggestedFileName = Path.GetFileNameWithoutExtension(SelectedClip.FileName) + "_cutlist.edl",
            DefaultExtension = "edl"
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        await EdlExporter.ExportCutListEdlAsync(path, SelectedClip, ResolveFps);
        Status = "Exported EDL cut list.";
        NotifyExportCompleted(path);
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (SelectedClip is null || StorageProvider is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export CSV cut list",
            SuggestedFileName = Path.GetFileNameWithoutExtension(SelectedClip.FileName) + "_cutlist.csv",
            DefaultExtension = "csv"
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        await EdlExporter.ExportCutListCsvAsync(path, SelectedClip);
        Status = "Exported CSV cut list.";
        NotifyExportCompleted(path);
    }

    private async Task AnalyzeClip(MediaClip clip)
    {
        try
        {
            clip.Status = "Analyzing...";
            Status = $"Analyzing {clip.FileName}...";

            var cacheKey = string.Join("|", clip.FilePath, ThresholdDb, MinSilenceSeconds, KeepPaddingSeconds, UseAdaptiveThreshold);
            var fileWriteTime = File.GetLastWriteTimeUtc(clip.FilePath);
            if (_analysisCache.TryGetValue(cacheKey, out var cached) && cached.LastWriteTimeUtc == fileWriteTime)
            {
                var cachedSegments = cached.Segments.Select(s => new TimelineSegment
                {
                    Kind = s.Kind,
                    Start = s.Start,
                    End = s.End,
                    Remove = s.Remove
                }).ToList();

                ClearSegmentThumbnails(clip.Segments);
                clip.Segments = new ObservableCollection<TimelineSegment>(cachedSegments);
                clip.DurationSeconds = cachedSegments.Sum(s => s.Duration);
                AdaptiveThresholdDb = cached.ThresholdDb;
            }
            else
            {
                var result = await _ffmpeg.DetectSegmentsAsync(clip.FilePath, ThresholdDb, MinSilenceSeconds, KeepPaddingSeconds, UseAdaptiveThreshold);
                AdaptiveThresholdDb = result.AdaptiveThresholdDb;
                var resultSegments = result.Segments.Select(s => new TimelineSegment
                {
                    Kind = s.Kind,
                    Start = s.Start,
                    End = s.End,
                    Remove = s.Remove
                }).ToList();

                ClearSegmentThumbnails(clip.Segments);
                clip.Segments = new ObservableCollection<TimelineSegment>(resultSegments);
                clip.DurationSeconds = resultSegments.Sum(s => s.Duration);
                _analysisCache[cacheKey] = (fileWriteTime, resultSegments, ThresholdDb, MinSilenceSeconds, KeepPaddingSeconds, UseAdaptiveThreshold);
            }

            clip.IsAnalyzed = true;
            clip.Status = $"{clip.Segments.Count(s => s.Kind == SegmentKind.Silence)} pauses found";
            if (ReferenceEquals(clip, SelectedClip))
                WatchPauseSelection(clip);
            if (ReferenceEquals(clip, SelectedClip))
            {
                RefreshTimelineBlocks();
                _ = LoadTimelineThumbnailsAsync(clip);
            }
            NotifySelectionStateChanged();
            Status = clip.Status;
        }
        catch (Exception ex)
        {
            if (HandleMissingFfmpeg(ex))
            {
                clip.Status = "FFmpeg missing";
                return;
            }

            clip.Status = "Error";
            Status = ex.Message;
        }
    }

    private async Task ShowToastAsync(string message)
    {
        if (message.Length > 180)
            message = message[..177] + "...";

        Toasts.Add(message);
        while (Toasts.Count > 3)
            Toasts.RemoveAt(0);

        await Task.Delay(4200);
        await Dispatcher.UIThread.InvokeAsync(() => Toasts.Remove(message));
    }

    public void DismissToast(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
            Toasts.Remove(message);
    }

    private void NotifyExportCompleted(string path)
    {
        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(folder))
            ExportCompleted?.Invoke(folder);
    }

    private bool HandleMissingFfmpeg(Exception ex)
    {
        if (!FfmpegService.IsMissingToolException(ex))
            return false;

        Status = "FFmpeg was not found. Install FFmpeg and make sure it is available in PATH.";
        FfmpegMissing?.Invoke();
        return true;
    }

    private void WatchPauseSelection(MediaClip? clip)
    {
        foreach (var segment in _watchedSegments)
            segment.PropertyChanged -= Segment_PropertyChanged;

        _watchedSegments.Clear();

        if (clip is null)
            return;

        foreach (var segment in clip.Segments)
        {
            segment.PropertyChanged += Segment_PropertyChanged;
            _watchedSegments.Add(segment);
        }
    }

    private void Segment_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TimelineSegment.Remove))
        {
            RefreshTimelineBlocks();
            NotifySelectionStateChanged();
        }
    }

    private void NotifySelectionStateChanged()
    {
        OnPropertyChanged(nameof(AllSectionsSelected));
        OnPropertyChanged(nameof(ToggleAllSpeechLabel));
        OnPropertyChanged(nameof(ToggleAllPausesLabel));
    }

    private void RefreshTimelineBlocks()
    {
        TimelineBlocks.Clear();

        if (SelectedClip is null || SelectedClip.Segments.Count == 0)
            return;

        var duration = SelectedClip.Segments.Sum(x => x.Duration);
        if (duration <= 0)
            return;

        foreach (var segment in SelectedClip.Segments)
        {
            TimelineBlocks.Add(new TimelineSegmentBlock
            {
                Segment = segment,
                Width = Math.Max(8, segment.Duration / duration * _timelineTrackWidth)
            });
        }
    }

    private static void ClearSegmentThumbnails(IEnumerable<TimelineSegment> segments)
    {
        foreach (var segment in segments)
            segment.ClearThumbnails();
    }

    private async Task LoadTimelineThumbnailsAsync(MediaClip? clip)
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = null;

        if (clip is null || clip.Segments.Count == 0)
            return;

        foreach (var segment in clip.Segments)
            segment.ClearThumbnails();

        var cts = new CancellationTokenSource();
        _thumbnailCts = cts;
        var folder = Path.Combine(Path.GetTempPath(), "silence-cutter-track-" + Guid.NewGuid().ToString("N"));

        try
        {
            Status = "Building thumbnail track...";
            var duration = Math.Max(clip.Segments.Sum(x => x.Duration), 0.1);
            var failedThumbnailCount = 0;

            for (var i = 0; i < clip.Segments.Count; i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                var segment = clip.Segments[i];
                var segmentWidth = segment.Duration / duration * _timelineTrackWidth;
                var thumbnailCount = Math.Clamp((int)Math.Ceiling(segmentWidth / 72), 1, 8);
                var segmentFolder = Path.Combine(folder, i.ToString("0000"));

                IReadOnlyList<string> paths;
                try
                {
                    paths = await _ffmpeg.ExtractSegmentThumbnailsAsync(clip.FilePath, segment, segmentFolder, thumbnailCount, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failedThumbnailCount++;
                    Debug.WriteLine($"Thumbnail extraction failed for {clip.FileName} segment {i} ({TimelineSegment.TimeFmt(segment.Start)} - {TimelineSegment.TimeFmt(segment.End)}): {ex}");
                    continue;
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (cts.IsCancellationRequested || !ReferenceEquals(clip, SelectedClip))
                        return;

                    foreach (var path in paths)
                        segment.Thumbnails.Add(new Bitmap(path));
                });
            }

            if (!cts.IsCancellationRequested && ReferenceEquals(clip, SelectedClip))
                Status = failedThumbnailCount == 0
                    ? "Thumbnail track ready."
                    : "Some timeline thumbnails could not be created.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!HandleMissingFfmpeg(ex))
            {
                Debug.WriteLine($"Thumbnail track failed for {clip.FileName}: {ex}");
                Status = "Timeline thumbnails could not be created.";
            }
        }
    }

    private void AdvancePreviewFrame()
    {
        if (_previewFrames.Count == 0)
            return;

        _previewFrameIndex = (_previewFrameIndex + 1) % _previewFrames.Count;
        SetPreviewFrame(_previewFrames[_previewFrameIndex]);
    }

    private void StopPreviewAudio()
    {
        if (_previewAudioProcess is null)
            return;

        try
        {
            if (!_previewAudioProcess.HasExited)
                _previewAudioProcess.Kill(true);
        }
        catch
        {
        }
        finally
        {
            _previewAudioProcess.Dispose();
            _previewAudioProcess = null;
        }
    }

    private void SetPreviewFrame(string? path)
    {
        PreviewFrame = path is null ? null : new Bitmap(path);
    }

    partial void OnPreviewFrameChanged(Bitmap? oldValue, Bitmap? newValue)
    {
        if (!ReferenceEquals(oldValue, newValue))
            oldValue?.Dispose();
    }

    partial void OnLatestVersionChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            _settings.LastSeenUpdateVersion = value;
            _settings.Save();
        }
    }
}
