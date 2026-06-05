using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Media.Imaging;
using System.Collections.ObjectModel;

namespace StreamWID.Models;

public enum SegmentKind { Speech, Silence }

public partial class TimelineSegment : ObservableObject
{
    public SegmentKind Kind { get; init; }
    public double Start { get; init; }
    public double End { get; init; }
    public double Duration => End - Start;
    public string Label => $"{Kind}  {TimeFmt(Start)} → {TimeFmt(End)}  ({Duration:0.00}s)";
    public bool IsSpeech => Kind == SegmentKind.Speech;
    public bool IsSilence => Kind == SegmentKind.Silence;
    public bool IsRemoved => Remove;
    public bool IsKeptSpeech => IsSpeech && !Remove;
    public bool IsKeptPause => IsSilence && !Remove;
    public ObservableCollection<Bitmap> Thumbnails { get; } = new();

    [ObservableProperty] private bool remove;

    partial void OnRemoveChanged(bool value)
    {
        OnPropertyChanged(nameof(IsRemoved));
        OnPropertyChanged(nameof(IsKeptSpeech));
        OnPropertyChanged(nameof(IsKeptPause));
    }

    public void ClearThumbnails()
    {
        foreach (var thumbnail in Thumbnails)
            thumbnail.Dispose();

        Thumbnails.Clear();
    }

    public static string TimeFmt(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}";
    }
}
