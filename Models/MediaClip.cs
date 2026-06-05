using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace StreamWID.Models;

public partial class MediaClip : ObservableObject
{
    [ObservableProperty] private string filePath = "";
    [ObservableProperty] private string fileName = "";
    [ObservableProperty] private double durationSeconds;
    [ObservableProperty] private ObservableCollection<TimelineSegment> segments = new();
    [ObservableProperty] private bool isAnalyzed;
    [ObservableProperty] private string status = "Waiting";
    [ObservableProperty] private Bitmap? thumbnail;

    partial void OnThumbnailChanged(Bitmap? oldValue, Bitmap? newValue)
    {
        if (!ReferenceEquals(oldValue, newValue))
            oldValue?.Dispose();
    }
}
