using StreamWID.Models;
using StreamWID.Services;
using System.Collections.ObjectModel;

namespace StreamWID.Tests;

public sealed class EdlExporterTests
{
    [Fact]
    public async Task ExportsMarkedPauseAsTimelineSplitRange()
    {
        var edl = await ExportAsync(4, Pause(1, 2, remove: true));

        var events = ParseEvents(edl);

        Assert.Single(events);
        Assert.Equal(("00:00:01:00", "00:00:02:00", "00:00:01:00", "00:00:02:00"), events[0]);
        Assert.Contains("* FROM CLIP NAME: input.mp4", edl);
        Assert.Contains("* MARKED FOR CUT Silence 00:00:01:00 -> 00:00:02:00", edl);
    }

    [Fact]
    public async Task ExportsMarkedSpeechAsTimelineSplitRange()
    {
        var edl = await ExportAsync(4, Speech(0, 1, remove: true));

        var events = ParseEvents(edl);

        Assert.Single(events);
        Assert.Equal(("00:00:00:00", "00:00:01:00", "00:00:00:00", "00:00:01:00"), events[0]);
        Assert.Contains("* MARKED FOR CUT Speech 00:00:00:00 -> 00:00:01:00", edl);
    }

    [Fact]
    public async Task IgnoresUnmarkedSpeechAndPauses()
    {
        var edl = await ExportAsync(
            4,
            Speech(0, 1, remove: false),
            Pause(1, 2, remove: false));

        var events = ParseEvents(edl);

        Assert.Empty(events);
        Assert.DoesNotContain("* MARKED FOR CUT", edl);
    }

    [Fact]
    public async Task ExportsAllMarkedActionsInTimelineOrder()
    {
        var edl = await ExportAsync(
            10,
            Pause(6, 7, remove: true),
            Speech(2, 3, remove: true),
            Pause(4, 5, remove: false));

        var events = ParseEvents(edl);

        Assert.Equal(2, events.Count);
        Assert.Equal(("00:00:02:00", "00:00:03:00", "00:00:02:00", "00:00:03:00"), events[0]);
        Assert.Equal(("00:00:06:00", "00:00:07:00", "00:00:06:00", "00:00:07:00"), events[1]);
    }

    [Fact]
    public async Task MergesAdjacentAndOverlappingMarkedRangesBeforeExport()
    {
        var edl = await ExportAsync(
            10,
            Pause(1, 3, remove: true),
            Pause(3, 4, remove: true),
            Speech(3.5, 6, remove: true));

        var events = ParseEvents(edl);

        Assert.Single(events);
        Assert.Equal(("00:00:01:00", "00:00:06:00", "00:00:01:00", "00:00:06:00"), events[0]);
    }

    [Fact]
    public async Task ClampsMarkedRangesToClipDuration()
    {
        var edl = await ExportAsync(4, Pause(-1, 5, remove: true));

        var events = ParseEvents(edl);

        Assert.Single(events);
        Assert.Equal(("00:00:00:00", "00:00:04:00", "00:00:00:00", "00:00:04:00"), events[0]);
    }

    [Fact]
    public async Task WritesVideoOnlyEvents()
    {
        var edl = await ExportAsync(4, Pause(1, 2, remove: true));

        var eventLines = edl.Split(Environment.NewLine)
            .Where(line => line.Length > 0 && char.IsDigit(line[0]))
            .ToList();

        Assert.All(eventLines, line =>
        {
            Assert.Contains(" V     C ", line);
            Assert.DoesNotContain("AA/V", line);
        });
    }

    private static async Task<string> ExportAsync(double duration, params TimelineSegment[] segments)
    {
        var path = Path.Combine(Path.GetTempPath(), $"streamwid-edl-test-{Guid.NewGuid():N}.edl");
        try
        {
            var clip = new MediaClip
            {
                FileName = "input.mp4",
                DurationSeconds = duration,
                Segments = new ObservableCollection<TimelineSegment>(segments)
            };

            await EdlExporter.ExportCutListEdlAsync(path, clip, 25);
            return await File.ReadAllTextAsync(path);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static TimelineSegment Speech(double start, double end, bool remove) => Segment(SegmentKind.Speech, start, end, remove);

    private static TimelineSegment Pause(double start, double end, bool remove) => Segment(SegmentKind.Silence, start, end, remove);

    private static TimelineSegment Segment(SegmentKind kind, double start, double end, bool remove) => new()
    {
        Kind = kind,
        Start = start,
        End = end,
        Remove = remove
    };

    private static List<(string SourceIn, string SourceOut, string RecordIn, string RecordOut)> ParseEvents(string edl)
    {
        return edl.Split(Environment.NewLine)
            .Where(line => line.Length > 0 && char.IsDigit(line[0]))
            .Select(line =>
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return (parts[4], parts[5], parts[6], parts[7]);
            })
            .ToList();
    }
}
