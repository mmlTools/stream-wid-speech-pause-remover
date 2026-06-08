using CommunityToolkit.Mvvm.ComponentModel;

namespace StreamWID.Models;

public partial class ExportQueueItem : ObservableObject
{
    [ObservableProperty] private string clipName = "";
    [ObservableProperty] private string outputName = "";
    [ObservableProperty] private string kind = "";
    [ObservableProperty] private string status = "Queued";
    [ObservableProperty] private double progress;

    public string ProgressText => $"{Progress:0}%";

    partial void OnProgressChanged(double value)
    {
        OnPropertyChanged(nameof(ProgressText));
    }
}
