using StreamWID.Models;
using System.Text;

namespace StreamWID.Services;

public static class EdlExporter
{
    public static async Task ExportPauseMarkersAsync(string path, MediaClip clip, double fps = 25)
    {
        var sb = new StringBuilder();
        sb.AppendLine("TITLE: STREAMWID CUT LIST");
        sb.AppendLine("FCM: NON-DROP FRAME");
        sb.AppendLine();

        var keepSegments = BuildKeepSegments(clip);
        var recordCursor = 0d;
        var index = 1;

        foreach (var s in keepSegments)
        {
            var recordStart = recordCursor;
            var recordEnd = recordCursor + s.Duration;
            sb.AppendLine($"{index:000}  AX       V     C        {Tc(s.Start, fps)} {Tc(s.End, fps)} {Tc(recordStart, fps)} {Tc(recordEnd, fps)}");
            sb.AppendLine($"* FROM CLIP NAME: {clip.FileName}");
            sb.AppendLine($"* KEEP speech {Tc(s.Start, fps)} -> {Tc(s.End, fps)}");
            sb.AppendLine();
            recordCursor = recordEnd;
            index++;
        }

        await File.WriteAllTextAsync(path, sb.ToString());
    }

    public static async Task ExportCutListCsvAsync(string path, MediaClip clip)
    {
        var lines = new List<string> { "file,kind,start,end,duration,remove" };
        lines.AddRange(clip.Segments.Select(s => $"\"{clip.FileName}\",{s.Kind},{s.Start:0.###},{s.End:0.###},{s.Duration:0.###},{s.Remove}"));
        await File.WriteAllLinesAsync(path, lines);
    }

    private static IReadOnlyList<TimelineSegment> BuildKeepSegments(MediaClip clip)
    {
        var duration = clip.DurationSeconds > 0 ? clip.DurationSeconds : clip.Segments.Max(s => s.End);
        var removed = clip.Segments
            .Where(s => s.Kind == SegmentKind.Silence && s.Remove)
            .OrderBy(s => s.Start)
            .ToList();

        var keep = new List<TimelineSegment>();
        var cursor = 0d;

        foreach (var segment in removed)
        {
            if (segment.Start > cursor)
                keep.Add(new TimelineSegment { Kind = SegmentKind.Speech, Start = cursor, End = segment.Start });

            cursor = Math.Max(cursor, segment.End);
        }

        if (cursor < duration)
            keep.Add(new TimelineSegment { Kind = SegmentKind.Speech, Start = cursor, End = duration });

        return keep.Where(s => s.End > s.Start).ToList();
    }

    private static string Tc(double seconds, double fps)
    {
        var framesTotal = (long)Math.Round(seconds * fps);
        var frames = framesTotal % (long)fps;
        var totalSeconds = framesTotal / (long)fps;
        var s = totalSeconds % 60;
        var m = (totalSeconds / 60) % 60;
        var h = totalSeconds / 3600;
        return $"{h:00}:{m:00}:{s:00}:{frames:00}";
    }
}
