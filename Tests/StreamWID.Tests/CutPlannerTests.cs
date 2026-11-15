using StreamWID.Models;
using StreamWID.Services;

namespace StreamWID.Tests;

public sealed class CutPlannerTests
{
    [Fact]
    public void TimelineShrinksPausesByThePaddingKeptAroundSpeech()
    {
        var timeline = CutPlanner.BuildTimeline(10, [new TimeRange(2, 4)], 0.1);

        Assert.Collection(
            timeline,
            s => AssertSegment(s, SegmentKind.Speech, 0, 2.1, remove: false),
            s => AssertSegment(s, SegmentKind.Silence, 2.1, 3.9, remove: true),
            s => AssertSegment(s, SegmentKind.Speech, 3.9, 10, remove: false));
    }

    [Fact]
    public void TimelineDoesNotLeaveASliverOfSpeechBeforeLeadingSilence()
    {
        var timeline = CutPlanner.BuildTimeline(10, [new TimeRange(0, 3)], 0.08);

        Assert.Collection(
            timeline,
            s => AssertSegment(s, SegmentKind.Silence, 0, 2.92, remove: true),
            s => AssertSegment(s, SegmentKind.Speech, 2.92, 10, remove: false));
    }

    [Fact]
    public void TimelineDoesNotLeaveASliverOfSpeechAfterTrailingSilence()
    {
        var timeline = CutPlanner.BuildTimeline(10, [new TimeRange(7, 9.99)], 0.08);

        Assert.Collection(
            timeline,
            s => AssertSegment(s, SegmentKind.Speech, 0, 7.08, remove: false),
            s => AssertSegment(s, SegmentKind.Silence, 7.08, 10, remove: true));
    }

    [Fact]
    public void TimelineTreatsAnOpenEndedSilenceAsRunningToTheEnd()
    {
        var timeline = CutPlanner.BuildTimeline(10, [new TimeRange(6, double.PositiveInfinity)], 0.08);

        Assert.Equal(2, timeline.Count);
        AssertSegment(timeline[1], SegmentKind.Silence, 6.08, 10, remove: true);
    }

    [Fact]
    public void TimelineDropsPausesThatPaddingUsesUp()
    {
        var timeline = CutPlanner.BuildTimeline(10, [new TimeRange(4, 4.15)], 0.08);

        var only = Assert.Single(timeline);
        AssertSegment(only, SegmentKind.Speech, 0, 10, remove: false);
    }

    [Fact]
    public void MergesNeighbouringKeptSectionsIntoOneSpan()
    {
        var kept = CutPlanner.MergeKeptRanges(
        [
            Speech(0, 2, remove: false),
            Pause(2, 3, remove: false),
            Speech(3, 5, remove: false),
            Pause(5, 6, remove: true),
            Speech(6, 8, remove: false)
        ]);

        Assert.Equal([new TimeRange(0, 5), new TimeRange(6, 8)], kept);
    }

    [Fact]
    public void MergeIgnoresRemovedAndEmptySections()
    {
        var kept = CutPlanner.MergeKeptRanges(
        [
            Speech(4, 6, remove: false),
            Speech(0, 2, remove: true),
            Speech(3, 3, remove: false)
        ]);

        Assert.Equal([new TimeRange(4, 6)], kept);
    }

    [Fact]
    public void FrameAccurateCutsMoveOutwardToFrameBoundaries()
    {
        var spans = CutPlanner.PlanFrameAccurateCuts([new TimeRange(1.01, 2.01)], framesPerSecond: 25);

        var span = Assert.Single(spans);
        Assert.Equal(1.00, span.AudioStart, 6);
        Assert.Equal(2.04, span.AudioEnd, 6);
    }

    [Fact]
    public void FrameAccurateCutsGiveVideoAndAudioTheSameLength()
    {
        var spans = CutPlanner.PlanFrameAccurateCuts(
            [new TimeRange(0.277, 2.477), new TimeRange(3.2907, 5.5118)],
            framesPerSecond: 30000d / 1001);

        Assert.All(spans, span =>
        {
            Assert.Equal(span.AudioEnd - span.AudioStart, span.VideoEnd - span.VideoStart, 9);

            // The audio length is a whole number of frames.
            var frames = span.Duration * 30000d / 1001;
            Assert.Equal(Math.Round(frames), frames, 6);
        });
    }

    [Fact]
    public void VideoTrimSitsHalfAFrameBeforeTheFrameItSelects()
    {
        var spans = CutPlanner.PlanFrameAccurateCuts([new TimeRange(1, 2)], framesPerSecond: 25);

        var span = Assert.Single(spans);
        Assert.Equal(0.98, span.VideoStart, 6);
        Assert.Equal(1.98, span.VideoEnd, 6);
    }

    [Fact]
    public void FrameAccurateCutsKeepExactFrameTimesUnchanged()
    {
        var spans = CutPlanner.PlanFrameAccurateCuts([new TimeRange(1.2, 2.4)], framesPerSecond: 30);

        var span = Assert.Single(spans);
        Assert.Equal(1.2, span.AudioStart, 6);
        Assert.Equal(2.4, span.AudioEnd, 6);
    }

    [Fact]
    public void FrameAccurateCutsJoinSpansThatMeetAfterSnapping()
    {
        // The removed gap is shorter than a frame, so no cut is left once both edges snap outward.
        var spans = CutPlanner.PlanFrameAccurateCuts(
            [new TimeRange(0, 1.01), new TimeRange(1.03, 2)],
            framesPerSecond: 25);

        var span = Assert.Single(spans);
        Assert.Equal(0, span.AudioStart, 6);
        Assert.Equal(2, span.AudioEnd, 6);
    }

    [Fact]
    public void FrameAccurateCutsFollowTheFirstFrameOffset()
    {
        var spans = CutPlanner.PlanFrameAccurateCuts([new TimeRange(1.0, 2.0)], framesPerSecond: 25, firstFrameTime: 0.02);

        var span = Assert.Single(spans);
        Assert.Equal(0.98, span.AudioStart, 6);
        Assert.Equal(2.02, span.AudioEnd, 6);
    }

    [Fact]
    public void UnknownFrameRateLeavesRequestedTimesAlone()
    {
        var spans = CutPlanner.PlanFrameAccurateCuts([new TimeRange(1.013, 2.027)], framesPerSecond: 0);

        Assert.Equal([new CutSpan(1.013, 2.027, 1.013, 2.027)], spans);
    }

    [Fact]
    public void KeyframeAlignmentStartsEachSpanOnTheKeyframeBeforeIt()
    {
        var aligned = CutPlanner.AlignToKeyframes(
            [new TimeRange(0, 4), new TimeRange(9, 12)],
            [0, 8, 16]);

        Assert.Equal([new TimeRange(0, 4), new TimeRange(8, 12)], aligned);
    }

    [Fact]
    public void KeyframeAlignmentKeepsThePauseWhenCuttingWouldRepeatFootage()
    {
        // The keyframe before 5.9 is at 0, inside the first span. Starting there would replay 0-3.9.
        var aligned = CutPlanner.AlignToKeyframes(
            [new TimeRange(0, 3.9), new TimeRange(5.9, 9.9), new TimeRange(11.9, 15.9)],
            [0, 8.33, 16.66]);

        Assert.Equal([new TimeRange(0, 15.9)], aligned);
    }

    [Fact]
    public void KeyframeAlignmentNeverOverlapsSpans()
    {
        double[] keyframes = [0, 2, 4, 6, 8, 10, 12];
        var aligned = CutPlanner.AlignToKeyframes(
            [new TimeRange(0.5, 1.5), new TimeRange(2.5, 3.2), new TimeRange(3.4, 5), new TimeRange(9.1, 11)],
            keyframes);

        Assert.Equal([new TimeRange(0, 1.5), new TimeRange(2, 5), new TimeRange(8, 11)], aligned);
        for (var i = 1; i < aligned.Count; i++)
            Assert.True(aligned[i].Start > aligned[i - 1].End);
    }

    [Fact]
    public void KeyframeAlignmentWithoutKeyframesLeavesSpansAlone()
    {
        TimeRange[] kept = [new TimeRange(1, 2), new TimeRange(3, 4)];

        Assert.Equal(kept, CutPlanner.AlignToKeyframes(kept, []));
    }

    [Fact]
    public void FilterGraphFadesAudioOnlyAtJoins()
    {
        var graph = CutPlanner.BuildFilterGraph(
        [
            new CutSpan(0, 1.98, 0, 2),
            new CutSpan(2.98, 4.98, 3, 5),
            new CutSpan(5.98, 7.98, 6, 8)
        ], fadeSeconds: 0.01);

        var lines = graph.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("[0:v:0]trim=start=0:end=1.98,setpts=PTS-STARTPTS[v0];", lines[0]);
        Assert.Equal("[0:a:0]atrim=start=0:end=2,asetpts=PTS-STARTPTS,afade=t=out:st=1.99:d=0.01[a0];", lines[1]);
        Assert.Equal("[0:a:0]atrim=start=3:end=5,asetpts=PTS-STARTPTS,afade=t=in:st=0:d=0.01,afade=t=out:st=1.99:d=0.01[a1];", lines[3]);
        Assert.Equal("[0:a:0]atrim=start=6:end=8,asetpts=PTS-STARTPTS,afade=t=in:st=0:d=0.01[a2];", lines[5]);
        Assert.Equal("[v0][a0][v1][a1][v2][a2]concat=n=3:v=1:a=1[v][a]", lines[6]);
    }

    [Fact]
    public void FilterGraphLeavesAnUncutClipUnfaded()
    {
        var graph = CutPlanner.BuildFilterGraph([new CutSpan(0, 5, 0, 5)], fadeSeconds: 0.01, sourceDuration: 5);

        Assert.DoesNotContain("afade", graph);
        Assert.Contains("concat=n=1:v=1:a=1[v][a]", graph);
    }

    [Fact]
    public void FilterGraphFadesTheOuterEdgesWhenTheClipWasTrimmed()
    {
        // Leading and trailing silence were removed, so the output starts and ends mid-recording.
        var graph = CutPlanner.BuildFilterGraph([new CutSpan(1.98, 3.98, 2, 4)], fadeSeconds: 0.01, sourceDuration: 10);

        Assert.Contains("afade=t=in:st=0:d=0.01,afade=t=out:st=1.99:d=0.01[a0]", graph);
    }

    [Fact]
    public void FilterGraphShortensFadesThatWouldNotFit()
    {
        var graph = CutPlanner.BuildFilterGraph(
        [
            new CutSpan(0, 1, 0, 1),
            new CutSpan(2, 2.012, 2, 2.012),
            new CutSpan(3, 4, 3, 4)
        ], fadeSeconds: 0.01);

        Assert.Contains("afade=t=in:st=0:d=0.006,afade=t=out:st=0.006:d=0.006[a1]", graph);
    }

    [Fact]
    public void FilterGraphUsesDotDecimalsInAnyCulture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            var graph = CutPlanner.BuildFilterGraph([new CutSpan(0.5, 1.5, 0.52, 1.52), new CutSpan(2.5, 3.5, 2.52, 3.52)]);

            Assert.Contains("trim=start=0.5:end=1.5", graph);
            Assert.DoesNotContain("0,5", graph);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    private static void AssertSegment(TimelineSegment segment, SegmentKind kind, double start, double end, bool remove)
    {
        Assert.Equal(kind, segment.Kind);
        Assert.Equal(start, segment.Start, 6);
        Assert.Equal(end, segment.End, 6);
        Assert.Equal(remove, segment.Remove);
    }

    private static TimelineSegment Speech(double start, double end, bool remove) =>
        new() { Kind = SegmentKind.Speech, Start = start, End = end, Remove = remove };

    private static TimelineSegment Pause(double start, double end, bool remove) =>
        new() { Kind = SegmentKind.Silence, Start = start, End = end, Remove = remove };
}
