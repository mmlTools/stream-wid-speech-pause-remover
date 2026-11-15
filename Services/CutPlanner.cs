using StreamWID.Models;
using System.Globalization;
using System.Text;

namespace StreamWID.Services;

/// <summary>A span of source time, in seconds.</summary>
public readonly record struct TimeRange(double Start, double End)
{
    public double Duration => End - Start;
}

/// <summary>
/// One kept span expressed as the trim points handed to FFmpeg. Video and audio carry separate
/// points because the video trim is offset by half a frame so that timestamp rounding can never
/// pull in or drop a neighbouring frame.
/// </summary>
public readonly record struct CutSpan(double VideoStart, double VideoEnd, double AudioStart, double AudioEnd)
{
    public double Duration => AudioEnd - AudioStart;
}

/// <summary>Pure planning logic for detection results and exports. No FFmpeg calls happen here.</summary>
public static class CutPlanner
{
    /// <summary>Length of the audio fade applied on each side of a join to remove clicks.</summary>
    public const double DefaultFadeSeconds = 0.010;

    // Kept sections closer together than this are treated as touching.
    private const double JoinTolerance = 0.0005;

    // A silence that begins or ends this close to the clip edge is treated as reaching the edge.
    private const double EdgeTolerance = 0.05;

    /// <summary>
    /// Turns raw detected silences into the full speech/pause timeline, shrinking each pause by the
    /// padding that is kept around speech.
    /// </summary>
    public static List<TimelineSegment> BuildTimeline(
        double duration,
        IEnumerable<TimeRange> rawSilences,
        double keepPaddingSeconds)
    {
        var padding = Math.Max(0, keepPaddingSeconds);
        var pauses = new List<TimeRange>();

        foreach (var silence in rawSilences.OrderBy(s => s.Start))
        {
            // Padding protects speech next to a pause. A pause that touches the start or end of the
            // clip has no speech on that side, so padding there would only leave a few stray frames.
            var start = silence.Start <= EdgeTolerance ? 0 : silence.Start + padding;
            var end = silence.End >= duration - EdgeTolerance ? duration : silence.End - padding;

            start = Math.Max(0, start);
            end = Math.Min(duration, end);
            if (end > start)
                pauses.Add(new TimeRange(start, end));
        }

        var timeline = new List<TimelineSegment>();
        var cursor = 0d;
        foreach (var pause in pauses)
        {
            if (pause.End <= cursor)
                continue;

            var start = Math.Max(pause.Start, cursor);
            if (start > cursor)
                timeline.Add(new TimelineSegment { Kind = SegmentKind.Speech, Start = cursor, End = start, Remove = false });

            timeline.Add(new TimelineSegment { Kind = SegmentKind.Silence, Start = start, End = pause.End, Remove = true });
            cursor = pause.End;
        }

        if (cursor < duration)
            timeline.Add(new TimelineSegment { Kind = SegmentKind.Speech, Start = cursor, End = duration, Remove = false });

        return timeline;
    }

    /// <summary>
    /// Collapses the sections that are not marked for removal into the smallest set of spans.
    /// Neighbouring kept sections become one span, so the export does not cut where nothing is removed.
    /// </summary>
    public static List<TimeRange> MergeKeptRanges(IEnumerable<TimelineSegment> segments)
    {
        var merged = new List<TimeRange>();

        foreach (var segment in segments.Where(s => !s.Remove && s.End > s.Start).OrderBy(s => s.Start))
        {
            var start = Math.Max(0, segment.Start);
            if (segment.End <= start)
                continue;

            if (merged.Count > 0 && start <= merged[^1].End + JoinTolerance)
            {
                if (segment.End > merged[^1].End)
                    merged[^1] = merged[^1] with { End = segment.End };
                continue;
            }

            merged.Add(new TimeRange(start, segment.End));
        }

        return merged;
    }

    /// <summary>
    /// Moves every cut onto a video frame boundary so the picture and the sound of each kept span
    /// cover exactly the same time. Edges move outward (start earlier, end later), so nothing that
    /// was marked to keep is lost. Pass a frame rate of zero for variable frame rate sources to keep
    /// the requested times as they are.
    /// </summary>
    public static List<CutSpan> PlanFrameAccurateCuts(
        IReadOnlyList<TimeRange> kept,
        double framesPerSecond,
        double firstFrameTime = 0)
    {
        if (!(framesPerSecond > 0) || double.IsInfinity(framesPerSecond))
            return kept.Select(r => new CutSpan(r.Start, r.End, r.Start, r.End)).ToList();

        const double frameEpsilon = 1e-6;
        var origin = Math.Max(0, firstFrameTime);
        var frames = new List<(long First, long End)>();

        foreach (var range in kept)
        {
            var first = Math.Max(0, (long)Math.Floor((range.Start - origin) * framesPerSecond + frameEpsilon));
            var end = (long)Math.Ceiling((range.End - origin) * framesPerSecond - frameEpsilon);
            if (end <= first)
                continue;

            if (frames.Count > 0 && first <= frames[^1].End)
            {
                frames[^1] = (frames[^1].First, Math.Max(frames[^1].End, end));
                continue;
            }

            frames.Add((first, end));
        }

        var frameDuration = 1 / framesPerSecond;
        var halfFrame = frameDuration / 2;
        return frames
            .Select(f =>
            {
                var start = origin + f.First * frameDuration;
                var end = origin + f.End * frameDuration;
                return new CutSpan(Math.Max(0, start - halfFrame), end - halfFrame, start, end);
            })
            .ToList();
    }

    /// <summary>
    /// Adjusts kept spans for stream copy, where a span can only begin on a keyframe. Each span is
    /// pulled back to the keyframe at or before its start. If that keyframe lies inside the previous
    /// span, the pause between the two cannot be removed without repeating footage, so the spans are
    /// joined and the pause stays in.
    /// </summary>
    public static List<TimeRange> AlignToKeyframes(IReadOnlyList<TimeRange> kept, IReadOnlyList<double> keyframeTimes)
    {
        if (keyframeTimes.Count == 0)
            return kept.ToList();

        const double tolerance = 0.001;
        var aligned = new List<TimeRange>();

        foreach (var range in kept)
        {
            var start = KeyframeAtOrBefore(keyframeTimes, range.Start + tolerance) ?? range.Start;
            start = Math.Max(0, Math.Min(start, range.Start));

            if (aligned.Count > 0 && start <= aligned[^1].End + tolerance)
            {
                if (range.End > aligned[^1].End)
                    aligned[^1] = aligned[^1] with { End = range.End };
                continue;
            }

            aligned.Add(new TimeRange(start, range.End));
        }

        return aligned;
    }

    /// <summary>
    /// Builds the FFmpeg filter graph that trims every span and joins them. A short fade is applied to
    /// the audio wherever footage was removed, because cutting a waveform mid-cycle is heard as a
    /// click. The untouched start and end of the source are left as they are; pass the source
    /// duration so the end can be recognised.
    /// </summary>
    public static string BuildFilterGraph(
        IReadOnlyList<CutSpan> spans,
        double fadeSeconds = DefaultFadeSeconds,
        double sourceDuration = 0)
    {
        const double edgeTolerance = 0.02;
        var filter = new StringBuilder();

        for (var i = 0; i < spans.Count; i++)
        {
            var span = spans[i];
            filter.Append($"[0:v:0]trim=start={F(span.VideoStart)}:end={F(span.VideoEnd)},setpts=PTS-STARTPTS[v{i}];\n");
            filter.Append($"[0:a:0]atrim=start={F(span.AudioStart)}:end={F(span.AudioEnd)},asetpts=PTS-STARTPTS");

            var fade = Math.Min(Math.Max(0, fadeSeconds), span.Duration / 2);
            if (fade >= 0.001)
            {
                var removedBefore = i > 0 || span.AudioStart > edgeTolerance;
                var removedAfter = i < spans.Count - 1 || (sourceDuration > 0 && span.AudioEnd < sourceDuration - edgeTolerance);

                if (removedBefore)
                    filter.Append($",afade=t=in:st=0:d={F(fade)}");
                if (removedAfter)
                    filter.Append($",afade=t=out:st={F(span.Duration - fade)}:d={F(fade)}");
            }

            filter.Append($"[a{i}];\n");
        }

        for (var i = 0; i < spans.Count; i++)
            filter.Append($"[v{i}][a{i}]");

        filter.Append($"concat=n={spans.Count}:v=1:a=1[v][a]\n");
        return filter.ToString();
    }

    private static double? KeyframeAtOrBefore(IReadOnlyList<double> sortedTimes, double time)
    {
        var low = 0;
        var high = sortedTimes.Count - 1;
        double? found = null;

        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (sortedTimes[middle] <= time)
            {
                found = sortedTimes[middle];
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found;
    }

    private static string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
