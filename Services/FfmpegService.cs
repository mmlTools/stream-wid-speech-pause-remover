using StreamWID.Models;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace StreamWID.Services;

public sealed class FfmpegService
{
    private static readonly Regex DurationRegex = new(@"Duration:\s(?<h>\d+):(?<m>\d+):(?<s>\d+(\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex SilenceStartRegex = new(@"silence_start:\s(?<v>[0-9\.]+)", RegexOptions.Compiled);
    private static readonly Regex SilenceEndRegex = new(@"silence_end:\s(?<v>[0-9\.]+)", RegexOptions.Compiled);
    private static readonly Regex MeanVolumeRegex = new(@"mean_volume:\s*(?<v>-?\d+(\.\d+)?) dB", RegexOptions.Compiled);
    private static readonly Regex MaxVolumeRegex = new(@"max_volume:\s*(?<v>-?\d+(\.\d+)?) dB", RegexOptions.Compiled);
    private static readonly Regex ProgressTimeRegex = new(@"^out_time=(?<v>\d{2}:\d{2}:\d{2}(\.\d+)?)$", RegexOptions.Compiled);

    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
    public string FfplayPath { get; set; } = "ffplay";

    public async Task CheckToolsAvailableAsync(CancellationToken ct = default)
    {
        await ProcessRunner.RunAsync(FfmpegPath, "-version", ct);
        await ProcessRunner.RunAsync(FfprobePath, "-version", ct);
        await ProcessRunner.RunAsync(FfplayPath, "-version", ct);
    }

    public static bool IsMissingToolException(Exception ex) =>
        ex is Win32Exception { NativeErrorCode: 2 or 3 } ||
        ex is FileNotFoundException ||
        ex.InnerException is not null && IsMissingToolException(ex.InnerException);

    public async Task<double> GetDurationAsync(string file, CancellationToken ct = default)
    {
        var result = await ProcessRunner.RunAsync(FfprobePath,
            $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 {Q(file)}", ct);

        if (double.TryParse(result.StdOut.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sec))
            return sec;

        var fallback = await ProcessRunner.RunAsync(FfmpegPath, $"-i {Q(file)} -f null -", ct);
        var m = DurationRegex.Match(fallback.StdErr);
        if (!m.Success) throw new InvalidOperationException("Could not read clip duration. Make sure FFmpeg/FFprobe is installed and available in PATH.");

        return int.Parse(m.Groups["h"].Value) * 3600 + int.Parse(m.Groups["m"].Value) * 60 + double.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture);
    }

    public async Task<(List<TimelineSegment> Segments, double AdaptiveThresholdDb)> DetectSegmentsAsync(
        string file,
        double thresholdDb,
        double minSilenceSeconds,
        double keepPaddingSeconds,
        bool useAdaptiveThreshold,
        CancellationToken ct = default,
        Action<string, double>? progress = null)
    {
        var duration = await GetDurationAsync(file, ct);
        var adaptiveThreshold = thresholdDb;
        if (useAdaptiveThreshold)
        {
            adaptiveThreshold = await GetAdaptiveThresholdDbAsync(file, ct, value =>
                progress?.Invoke("Analyzing audio profile", value * 0.5));
            adaptiveThreshold = Math.Max(thresholdDb, adaptiveThreshold);
        }

        var baseProgress = useAdaptiveThreshold ? 50 : 0;
        var scaleProgress = useAdaptiveThreshold ? 0.5 : 1;
        var args = $"-hide_banner -i {Q(file)} -af silencedetect=noise={adaptiveThreshold.ToString(CultureInfo.InvariantCulture)}dB:d={minSilenceSeconds.ToString(CultureInfo.InvariantCulture)} -progress pipe:1 -f null -";
        var result = await ProcessRunner.RunAsync(FfmpegPath, args, ct, onStdOutLine: line =>
        {
            var seconds = TryParseProgressSeconds(line);
            if (seconds.HasValue)
                progress?.Invoke("Finding pauses", baseProgress + Math.Clamp(seconds.Value / Math.Max(duration, 0.1) * 100, 0, 99) * scaleProgress);
        });

        var silenceRanges = new List<(double Start, double End)>();
        double? pendingStart = null;

        foreach (var line in result.StdErr.Split('\n'))
        {
            var ss = SilenceStartRegex.Match(line);
            if (ss.Success) pendingStart = Parse(ss.Groups["v"].Value);

            var se = SilenceEndRegex.Match(line);
            if (se.Success && pendingStart.HasValue)
            {
                var end = Parse(se.Groups["v"].Value);
                silenceRanges.Add((Math.Max(0, pendingStart.Value + keepPaddingSeconds), Math.Min(duration, end - keepPaddingSeconds)));
                pendingStart = null;
            }
        }

        if (pendingStart.HasValue)
            silenceRanges.Add((Math.Max(0, pendingStart.Value + keepPaddingSeconds), duration));

        silenceRanges = silenceRanges.Where(x => x.End > x.Start).OrderBy(x => x.Start).ToList();
        progress?.Invoke("Finding pauses", 100);
        return (BuildFullTimeline(duration, silenceRanges), adaptiveThreshold);
    }

    public async Task<double> GetAdaptiveThresholdDbAsync(string file, CancellationToken ct = default, Action<double>? progress = null)
    {
        var duration = progress is null ? 0 : await GetDurationAsync(file, ct);
        var result = await ProcessRunner.RunAsync(FfmpegPath, $"-hide_banner -i {Q(file)} -af volumedetect -progress pipe:1 -f null -", ct, onStdOutLine: line =>
        {
            if (duration <= 0)
                return;

            var seconds = TryParseProgressSeconds(line);
            if (seconds.HasValue)
                progress?.Invoke(Math.Clamp(seconds.Value / Math.Max(duration, 0.1) * 100, 0, 99));
        });
        var mean = MeanVolumeRegex.Match(result.StdErr);
        var max = MaxVolumeRegex.Match(result.StdErr);

        var meanValue = mean.Success ? Parse(mean.Groups["v"].Value) : -35;
        var maxValue = max.Success ? Parse(max.Groups["v"].Value) : -1;

        var suggested = Math.Clamp(meanValue - 14, -55, -18);
        suggested = Math.Min(suggested, maxValue - 6);
        progress?.Invoke(100);
        return Math.Clamp(suggested, -55, -18);
    }

    public async Task<string?> ExtractClipThumbnailAsync(string inputFile, CancellationToken ct = default)
    {
        var tempFolder = Path.Combine(Path.GetTempPath(), "silence-cutter-thumbnails");
        Directory.CreateDirectory(tempFolder);
        var outputFile = Path.Combine(tempFolder, Path.GetFileNameWithoutExtension(inputFile) + "_thumb.jpg");
        var args = $"-y -hide_banner -ss 2 -i {Q(inputFile)} -frames:v 1 -vf scale=220:-2 {Q(outputFile)}";
        var result = await ProcessRunner.RunAsync(FfmpegPath, args, ct);
        if (result.ExitCode != 0 || !File.Exists(outputFile))
            return null;

        return outputFile;
    }

    public async Task ExportCutVideoAsync(
        string inputFile,
        IEnumerable<TimelineSegment> segments,
        string outputFile,
        bool reencode,
        CancellationToken ct = default,
        Action<double>? progress = null)
    {
        var keep = segments.Where(s => !s.Remove).OrderBy(s => s.Start).ToList();
        if (keep.Count == 0) throw new InvalidOperationException("No segments left to export.");

        if (reencode)
        {
            await ExportCutVideoWithFilterAsync(inputFile, keep, outputFile, ct, progress);
            return;
        }

        await ExportCutVideoWithSegmentConcatAsync(inputFile, keep, outputFile, ct, progress);
    }

    private async Task ExportCutVideoWithFilterAsync(
        string inputFile,
        IReadOnlyList<TimelineSegment> keep,
        string outputFile,
        CancellationToken ct,
        Action<double>? progress)
    {
        var temp = Path.Combine(Path.GetTempPath(), "silence-cutter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            var filterFile = Path.Combine(temp, "cut-filter.txt");
            var filter = new StringBuilder();

            for (var i = 0; i < keep.Count; i++)
            {
                var start = F(keep[i].Start);
                var end = F(keep[i].End);
                filter.AppendLine($"[0:v:0]trim=start={start}:end={end},setpts=PTS-STARTPTS[v{i}];");
                filter.AppendLine($"[0:a:0]atrim=start={start}:end={end},asetpts=PTS-STARTPTS[a{i}];");
            }

            for (var i = 0; i < keep.Count; i++)
                filter.Append($"[v{i}][a{i}]");

            filter.AppendLine($"concat=n={keep.Count}:v=1:a=1[v][a]");
            await File.WriteAllTextAsync(filterFile, filter.ToString(), ct);

            var totalDuration = Math.Max(keep.Sum(s => s.Duration), 0.1);
            var args = $"-y -i {Q(inputFile)} -filter_complex_script {Q(filterFile)} -map \"[v]\" -map \"[a]\" -c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p -c:a aac -b:a 192k -movflags +faststart -progress pipe:1 {Q(outputFile)}";
            var result = await ProcessRunner.RunAsync(FfmpegPath, args, ct, onStdOutLine: line =>
            {
                var seconds = TryParseProgressSeconds(line);
                if (seconds.HasValue)
                    progress?.Invoke(Math.Clamp(seconds.Value / totalDuration * 100, 0, 99));
            });
            if (result.ExitCode != 0) throw new InvalidOperationException(result.StdErr);
            progress?.Invoke(100);
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
    }

    private async Task ExportCutVideoWithSegmentConcatAsync(
        string inputFile,
        IReadOnlyList<TimelineSegment> keep,
        string outputFile,
        CancellationToken ct,
        Action<double>? progress)
    {
        var temp = Path.Combine(Path.GetTempPath(), "silence-cutter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            var parts = new List<string>();
            for (var i = 0; i < keep.Count; i++)
            {
                var part = Path.Combine(temp, $"part_{i:0000}.mp4");
                parts.Add(part);
                var seek = F(keep[i].Start);
                var dur = F(keep[i].Duration);
                var result = await ProcessRunner.RunAsync(FfmpegPath, $"-y -ss {seek} -i {Q(inputFile)} -t {dur} -map 0 -c copy -avoid_negative_ts make_zero {Q(part)}", ct);
                if (result.ExitCode != 0) throw new InvalidOperationException(result.StdErr);
                progress?.Invoke((i + 1) / (double)(keep.Count + 1) * 100);
            }

            var listFile = Path.Combine(temp, "concat.txt");
            await File.WriteAllLinesAsync(listFile, parts.Select(p => $"file '{p.Replace("'", "'\\''")}'"), ct);
            var concat = await ProcessRunner.RunAsync(FfmpegPath, $"-y -f concat -safe 0 -i {Q(listFile)} -c copy {Q(outputFile)}", ct);
            if (concat.ExitCode != 0) throw new InvalidOperationException(concat.StdErr);
            progress?.Invoke(100);
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
    }

    public async Task ExportPausesOnlyAsync(
        string inputFile,
        IEnumerable<TimelineSegment> segments,
        string outputFolder,
        CancellationToken ct = default,
        Action<double>? progress = null)
    {
        Directory.CreateDirectory(outputFolder);
        var pauses = segments.Where(s => s.Kind == SegmentKind.Silence && s.Remove).OrderBy(s => s.Start).ToList();
        if (pauses.Count == 0)
        {
            progress?.Invoke(100);
            return;
        }

        for (var i = 0; i < pauses.Count; i++)
        {
            var s = pauses[i];
            var outFile = Path.Combine(outputFolder, $"pause_{i + 1:000}_{TimelineSegment.TimeFmt(s.Start).Replace(':','-')}.mp4");
            var result = await ProcessRunner.RunAsync(FfmpegPath,
                $"-y -ss {s.Start.ToString(CultureInfo.InvariantCulture)} -i {Q(inputFile)} -t {s.Duration.ToString(CultureInfo.InvariantCulture)} -c copy {Q(outFile)}", ct);
            if (result.ExitCode != 0) throw new InvalidOperationException(result.StdErr);
            progress?.Invoke((i + 1) / (double)pauses.Count * 100);
        }
    }

    public async Task<IReadOnlyList<string>> ExtractPreviewFramesAsync(
        string inputFile,
        TimelineSegment segment,
        string outputFolder,
        int fps = 8,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputFolder);

        var outputPattern = Path.Combine(outputFolder, "frame_%04d.jpg");
        var args = $"-y -i {Q(inputFile)} -ss {F(segment.Start)} -t {F(segment.Duration)} -vf fps={fps},scale=960:-2 -q:v 3 {Q(outputPattern)}";
        var result = await ProcessRunner.RunAsync(FfmpegPath, args, ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(result.StdErr);

        return Directory.GetFiles(outputFolder, "frame_*.jpg").OrderBy(x => x).ToList();
    }

    public async Task<IReadOnlyList<string>> ExtractSegmentThumbnailsAsync(
        string inputFile,
        TimelineSegment segment,
        string outputFolder,
        int count,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputFolder);

        count = Math.Clamp(count, 1, 10);
        var fps = Math.Clamp(count / Math.Max(segment.Duration, 0.1), 0.15, 6);
        var outputPattern = Path.Combine(outputFolder, "thumb_%03d.jpg");
        var args = $"-y -hide_banner -i {Q(inputFile)} -ss {F(segment.Start)} -t {F(segment.Duration)} -vf fps={F(fps)},scale=120:68:force_original_aspect_ratio=increase,crop=120:68 -frames:v {count} -q:v 5 {Q(outputPattern)}";
        var result = await ProcessRunner.RunAsync(FfmpegPath, args, ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(result.StdErr);

        return Directory.GetFiles(outputFolder, "thumb_*.jpg").OrderBy(x => x).ToList();
    }

    public async Task<string> ExtractSegmentAudioPreviewAsync(
        string inputFile,
        TimelineSegment segment,
        string outputFolder,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputFolder);

        var outputFile = Path.Combine(outputFolder, "preview-audio.wav");
        var filter = $"atrim=start={F(segment.Start)}:end={F(segment.End)},asetpts=PTS-STARTPTS";
        var args = $"-y -hide_banner -i {Q(inputFile)} -vn -af {Q(filter)} -ac 2 -ar 48000 -c:a pcm_s16le {Q(outputFile)}";
        var result = await ProcessRunner.RunAsync(FfmpegPath, args, ct);
        if (result.ExitCode != 0 || !File.Exists(outputFile))
            throw new InvalidOperationException(result.StdErr);

        return outputFile;
    }

    public Process StartAudioPlayback(string audioFile)
    {
        var args = $"-nodisp -autoexit -loglevel quiet {Q(audioFile)}";
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = FfplayPath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        return process ?? throw new InvalidOperationException("Could not start preview audio.");
    }

    private static List<TimelineSegment> BuildFullTimeline(double duration, List<(double Start, double End)> silences)
    {
        var list = new List<TimelineSegment>();
        var cursor = 0d;
        foreach (var s in silences)
        {
            if (s.Start > cursor) list.Add(new TimelineSegment { Kind = SegmentKind.Speech, Start = cursor, End = s.Start, Remove = false });
            list.Add(new TimelineSegment { Kind = SegmentKind.Silence, Start = s.Start, End = s.End, Remove = true });
            cursor = s.End;
        }
        if (cursor < duration) list.Add(new TimelineSegment { Kind = SegmentKind.Speech, Start = cursor, End = duration, Remove = false });
        return list;
    }

    private static double Parse(string v) => double.Parse(v, CultureInfo.InvariantCulture);
    private static double? TryParseProgressSeconds(string line)
    {
        var match = ProgressTimeRegex.Match(line);
        if (!match.Success || !TimeSpan.TryParse(match.Groups["v"].Value, CultureInfo.InvariantCulture, out var time))
            return null;

        return time.TotalSeconds;
    }

    private static string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    private static string Q(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";
}
