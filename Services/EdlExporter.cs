using StreamWID.Models;
using System.Text;

namespace StreamWID.Services;

public static class EdlExporter
{
    public static async Task ExportCutListEdlAsync(string path, MediaClip clip, double fps = 25)
    {
        var sb = new StringBuilder();
        sb.AppendLine("TITLE: STREAMWID CUT LIST");
        sb.AppendLine("FCM: NON-DROP FRAME");
        sb.AppendLine();

        var cutSegments = BuildMarkedForCutSegments(clip);
        var index = 1;

        foreach (var segment in cutSegments)
        {
            sb.AppendLine($"{index:000}  AX       V     C        {Tc(segment.Start, fps)} {Tc(segment.End, fps)} {Tc(segment.Start, fps)} {Tc(segment.End, fps)}");
            sb.AppendLine($"* FROM CLIP NAME: {clip.FileName}");
            sb.AppendLine($"* MARKED FOR CUT {segment.Kind} {Tc(segment.Start, fps)} -> {Tc(segment.End, fps)}");
            sb.AppendLine();
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

    internal static IReadOnlyList<TimelineSegment> BuildMarkedForCutSegments(MediaClip clip)
    {
        var duration = clip.DurationSeconds > 0
            ? clip.DurationSeconds
            : clip.Segments.Select(s => s.End).DefaultIfEmpty(0).Max();

        if (duration <= 0)
            return [];

        var marked = clip.Segments
            .Where(s => s.Remove)
            .Where(s => s.End > s.Start)
            .Select(s => new TimelineSegment
            {
                Kind = s.Kind,
                Start = Math.Clamp(s.Start, 0, duration),
                End = Math.Clamp(s.End, 0, duration)
            })
            .Where(s => s.End > s.Start)
            .OrderBy(s => s.Start)
            .ToList();

        return MergeMarkedSegments(marked);
    }

    private static IReadOnlyList<TimelineSegment> MergeMarkedSegments(IReadOnlyList<TimelineSegment> marked)
    {
        var merged = new List<TimelineSegment>();

        foreach (var segment in marked)
        {
            var last = merged.LastOrDefault();
            if (last is null || segment.Start > last.End)
            {
                merged.Add(segment);
                continue;
            }

            if (segment.End > last.End)
            {
                merged[^1] = new TimelineSegment
                {
                    Kind = last.Kind == segment.Kind ? last.Kind : SegmentKind.Silence,
                    Start = last.Start,
                    End = segment.End
                };
            }
        }

        return merged;
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
