using StreamWID.Models;
using StreamWID.Services;
using System.Collections.ObjectModel;
using System.Globalization;

namespace StreamWID.Tests;

public sealed class FfmpegParsingTests
{
    [Fact]
    public void ParsesSilencePairsFromTheFilterLog()
    {
        const string log = """
            [silencedetect @ 000001] silence_start: 3.818667
            [silencedetect @ 000001] silence_end: 6.016208 | silence_duration: 2.197542
            [silencedetect @ 000001] silence_start: 9.813313
            [silencedetect @ 000001] silence_end: 12.010667 | silence_duration: 2.197354
            """;

        Assert.Equal(
            [new TimeRange(3.818667, 6.016208), new TimeRange(9.813313, 12.010667)],
            FfmpegService.ParseSilences(log));
    }

    [Fact]
    public void ClampsASlightlyNegativeSilenceStartToZero()
    {
        // FFmpeg reports a start just below zero when a clip opens with silence. It must still count.
        const string log = """
            [silencedetect @ 000001] silence_start: -0.00133333
            [silencedetect @ 000001] silence_end: 2.5 | silence_duration: 2.50133
            """;

        Assert.Equal([new TimeRange(0, 2.5)], FfmpegService.ParseSilences(log));
    }

    [Fact]
    public void ParsesExponentNotation()
    {
        const string log = """
            [silencedetect @ 000001] silence_start: 2.08333e-05
            [silencedetect @ 000001] silence_end: 1.5 | silence_duration: 1.49998
            """;

        var silence = Assert.Single(FfmpegService.ParseSilences(log));
        Assert.Equal(0.0000208333, silence.Start, 9);
        Assert.Equal(1.5, silence.End, 9);
    }

    [Fact]
    public void LeavesATrailingSilenceOpenEnded()
    {
        var silence = Assert.Single(FfmpegService.ParseSilences("[silencedetect @ 000001] silence_start: 57.25"));

        Assert.Equal(57.25, silence.Start);
        Assert.True(double.IsPositiveInfinity(silence.End));
    }

    [Fact]
    public void ParsesKeyframePacketsOnly()
    {
        const string listing = "0.000000,K__\n0.033333,___\n0.066667,___\n8.333333,K__\nN/A,K__\n16.666667,K_\n";

        Assert.Equal([0, 8.333333, 16.666667], FfmpegService.ParseKeyframeTimes(listing));
    }

    [Fact]
    public void KeyframeTimesAreRelativeToTheContainerStart()
    {
        var times = FfmpegService.ParseKeyframeTimes("1.400000,K__\n3.400000,K__\n", startTime: 1.4);

        Assert.Equal(2, times.Count);
        Assert.Equal(0, times[0], 6);
        Assert.Equal(2, times[1], 6);
    }

    [Fact]
    public void ProbeReadsConstantFrameRateFootage()
    {
        const string json = """
            {"streams":[
              {"codec_type":"video","r_frame_rate":"30000/1001","avg_frame_rate":"30000/1001","start_time":"0.033000","disposition":{"attached_pic":0}},
              {"codec_type":"audio","r_frame_rate":"0/0","avg_frame_rate":"0/0","start_time":"0.000000","disposition":{"attached_pic":0}}],
             "format":{"start_time":"0.000000","duration":"120.500000"}}
            """;

        var info = FfmpegService.ParseProbe(json);

        Assert.True(info.HasVideo);
        Assert.True(info.HasAudio);
        Assert.True(info.IsConstantFrameRate);
        Assert.Equal(29.97003, info.FrameRate, 5);
        Assert.Equal(120.5, info.DurationSeconds, 6);
        Assert.Equal(0.033, info.FirstVideoFrameTime, 6);
    }

    [Fact]
    public void ProbeFlagsVariableFrameRateFootage()
    {
        const string json = """
            {"streams":[{"codec_type":"video","r_frame_rate":"1000/1","avg_frame_rate":"2997/101","start_time":"0.000000"}],
             "format":{"start_time":"0.000000","duration":"10.0"}}
            """;

        var info = FfmpegService.ParseProbe(json);

        Assert.False(info.IsConstantFrameRate);
        Assert.False(info.HasAudio);
    }

    [Fact]
    public void ProbeSkipsCoverArtWhenLookingForTheVideoStream()
    {
        const string json = """
            {"streams":[
              {"codec_type":"video","r_frame_rate":"90000/1","avg_frame_rate":"0/0","disposition":{"attached_pic":1}},
              {"codec_type":"video","r_frame_rate":"25/1","avg_frame_rate":"25/1","start_time":"0.000000","disposition":{"attached_pic":0}}],
             "format":{"duration":"5.0"}}
            """;

        var info = FfmpegService.ParseProbe(json);

        Assert.True(info.IsConstantFrameRate);
        Assert.Equal(25, info.FrameRate, 6);
    }

    [Fact]
    public void ProbeToleratesMissingDurationAndBrokenOutput()
    {
        var noDuration = FfmpegService.ParseProbe("""{"streams":[{"codec_type":"audio"}],"format":{"duration":"N/A"}}""");

        Assert.Equal(0, noDuration.DurationSeconds);
        Assert.True(noDuration.HasAudio);
        Assert.Equal(MediaInfo.Unknown, FfmpegService.ParseProbe("not json"));
    }

    [Theory]
    [InlineData(-25.0, -3.0, -39.0)]   // typical speech: 14 dB under the mean level
    [InlineData(-10.0, -1.0, -24.0)]
    [InlineData(-50.0, -30.0, -55.0)]  // never suggests lower than -55 dB
    [InlineData(-2.0, -0.5, -18.0)]    // never suggests higher than -18 dB
    public void SuggestsThresholdFromMeasuredLoudness(double mean, double max, double expected)
    {
        Assert.Equal(expected, FfmpegService.SuggestThresholdDb(mean, max), 6);
    }

    [Fact]
    public async Task CsvUsesDotDecimalsInCommaDecimalCultures()
    {
        var path = Path.Combine(Path.GetTempPath(), $"streamwid-csv-test-{Guid.NewGuid():N}.csv");
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("it-IT");
        try
        {
            var clip = new MediaClip
            {
                FileName = "take \"2\".mp4",
                Segments = new ObservableCollection<TimelineSegment>
                {
                    new() { Kind = SegmentKind.Silence, Start = 1.25, End = 3.5, Remove = true }
                }
            };

            await EdlExporter.ExportCutListCsvAsync(path, clip);
            var lines = await File.ReadAllLinesAsync(path);

            Assert.Equal("file,kind,start,end,duration,remove", lines[0]);
            Assert.Equal("\"take \"\"2\"\".mp4\",Silence,1.25,3.5,2.25,True", lines[1]);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
