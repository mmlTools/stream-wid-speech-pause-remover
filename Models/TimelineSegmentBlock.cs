using CommunityToolkit.Mvvm.ComponentModel;

namespace StreamWID.Models;

public partial class TimelineSegmentBlock : ObservableObject
{
    public TimelineSegment Segment { get; init; } = new();
    public string Label => Segment.Remove ? "Cut" : "Keep";
    public string Background => Segment.Remove ? "#D45B5B" : "#5DBB7D";
    public string Accent => Segment.Remove ? "#E25E63" : "#59C97C";
    public string ToolTip => $"{Segment.Label} - {Label}";

    [ObservableProperty] private double width;
}
