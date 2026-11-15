using StreamWID.Models;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StreamWID.Services;

/// <summary>What FFprobe reports about a clip. Times are in seconds.</summary>
public sealed record MediaInfo(
    double DurationSeconds,
    double StartTime,
    bool HasVideo,
    bool HasAudio,
    double FrameRate,
    bool IsConstantFrameRate,
    double FirstVideoFrameTime)
{
    public static MediaInfo Unknown { get; } = new(0, 0, false, false, 0, false, 0);
}

/// <summary>
/// Outcome of a cut-video export. <see cref="ExportedSeconds"/> is longer than
/// <see cref="RequestedSeconds"/> when a stream copy export had to leave pauses in because they did
/// not start on a keyframe.
/// </summary>
public sealed record CutExportResult(double RequestedSeconds, double ExportedSeconds)
{
    public double ExtraSeconds => Math.Max(0, ExportedSeconds - RequestedSeconds);
}

public sealed class FfmpegService
{
    private const string NumberPattern = @"-?[0-9]+(?:\.[0-9]+)?(?:[eE][-+]?[0-9]+)?";
    private static readonly Regex SilenceStartRegex = new($@"silence_start:\s*(?<v>{NumberPattern})", RegexOptions.Compiled);
    private static readonly Regex SilenceEndRegex = new($@"silence_end:\s*(?<v>{NumberPattern})", RegexOptions.Compiled);
    private static readonly Regex MeanVolumeRegex = new(@"mean_volume:\s*(?<v>-?\d+(\.\d+)?) dB", RegexOptions.Compiled);
    private static readonly Regex MaxVolumeRegex = new(@"max_volume:\s*(?<v>-?\d+(\.\d+)?) dB", RegexOptions.Compiled);
    private static readonly Regex ProgressTimeRegex = new(@"^out_time=(?<h>\d+):(?<m>\d{2}):(?<s>\d{2}(\.\d+)?)$", RegexOptions.Compiled);

    // Seeking a hair past a keyframe makes FFmpeg land on that keyframe instead of the one before it.
    private const double KeyframeSeekNudge = 0.002;

    // FFmpeg 7 renamed the option that reads a filter graph from a file. Set once the old name is needed.
    private bool _useLegacyFilterScriptOption;

    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
    public string FfplayPath { get; set; } = "ffplay";

    public async Task CheckToolsAvailableAsync(CancellationToken ct = default)
    {
        await ProcessRunner.RunAsync(FfmpegPath, "-version", ct);
        await ProcessRunner.RunAsync(FfprobePath, "-version", ct);
        await ProcessRunner.RunAsync(FfplayPath, "-version", ct);
    }

    public async Task<bool> EnsureToolsAvailableAsync(CancellationToken ct = default, Action<string>? status = null)
    {
        try
        {
            await CheckToolsAvailableAsync(ct);
            return true;
        }
        catch (Exception ex) when (IsMissingToolException(ex))
        {
            status?.Invoke("FFmpeg was not found. Trying to install it automatically...");
        }

        if (!await TryInstallFfmpegAsync(ct, status))
            return false;

        await CheckToolsAvailableAsync(ct);
        status?.Invoke("FFmpeg is ready.");
        return true;
    }

    public static bool IsMissingToolException(Exception ex) =>
        ex is Win32Exception { NativeErrorCode: 2 or 3 } ||
        ex is FileNotFoundException ||
        ex.InnerException is not null && IsMissingToolException(ex.InnerException);

    private static async Task<bool> TryInstallFfmpegAsync(CancellationToken ct, Action<string>? status)
    {
        foreach (var command in GetInstallCommands())
        {
            if (!IsCommandAvailable(command.Exe))
                continue;

            status?.Invoke($"Installing FFmpeg with {command.Name}...");
            try
            {
                var result = await ProcessRunner.RunAsync(command.Exe, command.Args, ct);
                if (result.ExitCode == 0)
                    return true;

                Debug.WriteLine($"{command.Name} FFmpeg install failed: {result.StdErr}");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Debug.WriteLine($"{command.Name} FFmpeg install failed: {ex.Message}");
            }
        }

        status?.Invoke("Automatic FFmpeg install was not available on this system.");
        return false;
    }

    private static IEnumerable<(string Name, string Exe, string Args)> GetInstallCommands()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return ("winget", "winget", "install --id Gyan.FFmpeg --exact --silent --accept-package-agreements --accept-source-agreements");
            yield return ("Chocolatey", "choco", "install ffmpeg -y");
            yield break;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return ("Homebrew", "brew", "install ffmpeg");
            yield break;
        }

        yield return ("APT", "apt-get", "install -y ffmpeg");
        yield return ("DNF", "dnf", "install -y ffmpeg");
        yield return ("Pacman", "pacman", "-S --noconfirm ffmpeg");
        yield return ("Zypper", "zypper", "--non-interactive install ffmpeg");
    }

    private static bool IsCommandAvailable(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];

        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(folder.Trim('"'), command + extension);
                if (File.Exists(candidate))
                    return true;
            }
        }

        return false;
    }

    /// <summary>Reads duration, frame rate, and stream layout with a single FFprobe call.</summary>
    public async Task<MediaInfo> ProbeAsync(string file, CancellationToken ct = default)
    {
        var result = await ProcessRunner.RunAsync(FfprobePath,
            $"-v error -show_entries format=duration,start_time:stream=codec_type,avg_frame_rate,r_frame_rate,start_time:stream_disposition=attached_pic -of json {Q(file)}", ct);

        return ParseProbe(result.StdOut);
    }

    public static MediaInfo ParseProbe(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var duration = 0d;
            var formatStart = 0d;
            if (root.TryGetProperty("format", out var format))
            {
                duration = ReadDouble(format, "duration") ?? 0;
                formatStart = ReadDouble(format, "start_time") ?? 0;
            }

            var hasVideo = false;
            var hasAudio = false;
            var frameRate = 0d;
            var isConstant = false;
            var firstFrameTime = 0d;

            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var type = ReadString(stream, "codec_type");
                    if (type == "audio")
                    {
                        hasAudio = true;
                    }
                    else if (type == "video" && !hasVideo && !IsAttachedPicture(stream))
                    {
                        hasVideo = true;
                        var realRate = ParseRate(ReadString(stream, "r_frame_rate"));
                        var averageRate = ParseRate(ReadString(stream, "avg_frame_rate"));

                        // The two rates agree on constant frame rate footage and drift apart on variable frame rate footage.
                        isConstant = realRate > 0 && averageRate > 0 && Math.Abs(realRate - averageRate) <= averageRate * 0.005;
                        frameRate = isConstant ? realRate : averageRate > 0 ? averageRate : realRate;

                        // FFmpeg shifts every stream so the earliest one starts at zero, so the first frame sits at this offset.
                        var streamStart = ReadDouble(stream, "start_time");
                        if (streamStart.HasValue)
                            firstFrameTime = Math.Max(0, streamStart.Value - formatStart);
                    }
                }
            }

            return new MediaInfo(Math.Max(0, duration), formatStart, hasVideo, hasAudio, frameRate, isConstant, firstFrameTime);
        }
        catch (JsonException)
        {
            return MediaInfo.Unknown;
        }
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
        var info = await ProbeAsync(file, ct);
        if (info.HasVideo && !info.HasAudio)
            throw new InvalidOperationException("This clip has no audio track, so there are no pauses to detect.");

        // One audio-only pass finds the pauses at the manual threshold and, in adaptive mode, measures
        // loudness at the same time. A second pass is only needed when the suggestion ends up above
        // the manual threshold.
        var duration = info.DurationSeconds;
        var stage = "Finding pauses";
        var scan = await ScanAudioAsync(file, thresholdDb, minSilenceSeconds, useAdaptiveThreshold, duration, ct,
            value => progress?.Invoke(stage, value));

        // Some recordings (browser-made WebM, for example) do not store a duration. The scan has just read to the end.
        if (duration <= 0)
            duration = scan.EndTime;
        if (duration <= 0)
            throw new InvalidOperationException("Could not read clip duration. Make sure FFmpeg/FFprobe is installed and available in PATH.");

        var threshold = thresholdDb;
        if (useAdaptiveThreshold)
        {
            threshold = Math.Max(thresholdDb, SuggestThresholdDb(scan.MeanVolumeDb, scan.MaxVolumeDb));
            if (threshold > thresholdDb + 0.001)
            {
                stage = "Refining pauses";
                scan = await ScanAudioAsync(file, threshold, minSilenceSeconds, measureVolume: false, duration, ct,
                    value => progress?.Invoke(stage, value));
            }
        }

        progress?.Invoke(stage, 100);
        return (CutPlanner.BuildTimeline(duration, scan.Silences, keepPaddingSeconds), threshold);
    }

    /// <summary>Suggests a silence threshold from the measured loudness of a recording.</summary>
    public static double SuggestThresholdDb(double? meanVolumeDb, double? maxVolumeDb)
    {
        var meanValue = meanVolumeDb ?? -35;
        var maxValue = maxVolumeDb ?? -1;

        var suggested = Math.Clamp(meanValue - 14, -55, -18);
        suggested = Math.Min(suggested, maxValue - 6);
        return Math.Clamp(suggested, -55, -18);
    }

    /// <summary>
    /// Reads the silences FFmpeg's silencedetect filter reported. A silence still open at the end of
    /// the log runs to the end of the clip and is returned with an infinite end.
    /// </summary>
    public static List<TimeRange> ParseSilences(string log)
    {
        var silences = new List<TimeRange>();
        double? pendingStart = null;

        foreach (var line in log.Split('\n'))
        {
            var start = SilenceStartRegex.Match(line);
            if (start.Success)
                pendingStart = Math.Max(0, Parse(start.Groups["v"].Value));

            var end = SilenceEndRegex.Match(line);
            if (end.Success && pendingStart.HasValue)
            {
                silences.Add(new TimeRange(pendingStart.Value, Parse(end.Groups["v"].Value)));
                pendingStart = null;
            }
        }

        if (pendingStart.HasValue)
            silences.Add(new TimeRange(pendingStart.Value, double.PositiveInfinity));

        return silences;
    }

    private sealed record AudioScan(List<TimeRange> Silences, double? MeanVolumeDb, double? MaxVolumeDb, double EndTime);

    private async Task<AudioScan> ScanAudioAsync(
        string file,
        double thresholdDb,
        double minSilenceSeconds,
        bool measureVolume,
        double duration,
        CancellationToken ct,
        Action<double>? progress)
    {
        var filter = $"silencedetect=noise={F(thresholdDb)}dB:d={F(minSilenceSeconds)}";
        if (measureVolume)
            filter = "volumedetect," + filter;

        // Mapping only the audio stream keeps FFmpeg from decoding the picture, which is where
        // nearly all of the analysis time used to go.
        var args = $"-nostdin -hide_banner -nostats -i {Q(file)} -map 0:a:0 -af {filter} -progress pipe:1 -f null -";
        var endTime = 0d;
        var result = await ProcessRunner.RunAsync(FfmpegPath, args, ct, onStdOutLine: line =>
        {
            var seconds = TryParseProgressSeconds(line);
            if (!seconds.HasValue)
                return;

            endTime = Math.Max(endTime, seconds.Value);
            if (duration > 0)
                progress?.Invoke(Math.Clamp(seconds.Value / duration * 100, 0, 99));
        });

        var silences = ParseSilences(result.StdErr);
        if (result.ExitCode != 0 && endTime <= 0 && silences.Count == 0)
            throw new InvalidOperationException(ErrorTail(result.StdErr));

        var mean = MeanVolumeRegex.Match(result.StdErr);
        var max = MaxVolumeRegex.Match(result.StdErr);
        return new AudioScan(
            silences,
            mean.Success ? Parse(mean.Groups["v"].Value) : null,
            max.Success ? Parse(max.Groups["v"].Value) : null,
            endTime);
    }

    public async Task<string?> ExtractClipThumbnailAsync(string inputFile, CancellationToken ct = default)
    {
        var tempFolder = Path.Combine(Path.GetTempPath(), "silence-cutter-thumbnails");
        Directory.CreateDirectory(tempFolder);

        // The path hash keeps two clips with the same file name in different folders from sharing a thumbnail.
        var pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(inputFile))), 0, 6);
        var outputFile = Path.Combine(tempFolder, $"{Path.GetFileNameWithoutExtension(inputFile)}_{pathHash}_thumb.jpg");

        // Two seconds in usually skips a black first frame; clips shorter than that fall back to the first frame.
        foreach (var seek in new[] { "2", "0" })
        {
            try { File.Delete(outputFile); } catch { }
            var args = $"-nostdin -y -hide_banner -loglevel error -ss {seek} -i {Q(inputFile)} -an -sn -dn -frames:v 1 -vf scale=220:-2 {Q(outputFile)}";
            var result = await ProcessRunner.RunAsync(FfmpegPath, args, ct);
            if (result.ExitCode == 0 && File.Exists(outputFile))
                return outputFile;
        }

        return null;
    }

    /// <summary>
    /// Renders the clip without the sections marked for removal. With <paramref name="reencode"/> the
    /// cuts are frame-accurate; without it the streams are copied untouched and cuts can only start on
    /// keyframes.
    /// </summary>
    public async Task<CutExportResult> ExportCutVideoAsync(
        string inputFile,
        IEnumerable<TimelineSegment> segments,
        string outputFile,
        bool reencode,
        CancellationToken ct = default,
        Action<double>? progress = null)
    {
        var kept = CutPlanner.MergeKeptRanges(segments);
        if (kept.Count == 0) throw new InvalidOperationException("No segments left to export.");

        // Render next to the destination and rename at the end, so a failed or cancelled export never
        // leaves a broken file under the chosen name or destroys a file that was already there.
        EnsureCanReplace(outputFile);
        var workingFile = WorkingPathFor(outputFile);
        double exportedSeconds;
        try
        {
            exportedSeconds = reencode
                ? await ExportCutVideoWithFilterAsync(inputFile, kept, workingFile, ct, progress)
                : await ExportCutVideoWithStreamCopyAsync(inputFile, kept, workingFile, ct, progress);
        }
        catch
        {
            TryDeleteFile(workingFile);
            throw;
        }

        try
        {
            File.Move(workingFile, outputFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The render is complete, so keep it rather than throwing the work away.
            throw new InvalidOperationException(
                $"The export finished, but \"{Path.GetFileName(outputFile)}\" could not be replaced. The video was saved as \"{Path.GetFileName(workingFile)}\" in the same folder.", ex);
        }

        progress?.Invoke(100);
        return new CutExportResult(kept.Sum(r => r.Duration), exportedSeconds);
    }

    /// <summary>Fails before a long render starts if the existing destination file is locked.</summary>
    private static void EnsureCanReplace(string outputFile)
    {
        if (!File.Exists(outputFile))
            return;

        try
        {
            using var stream = new FileStream(outputFile, FileMode.Open, FileAccess.Write, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"\"{Path.GetFileName(outputFile)}\" is open in another app or cannot be written. Close it or choose a different name.", ex);
        }
    }

    private async Task<double> ExportCutVideoWithFilterAsync(
        string inputFile,
        IReadOnlyList<TimeRange> kept,
        string outputFile,
        CancellationToken ct,
        Action<double>? progress)
    {
        // Constant frame rate footage gets its cuts moved onto frame boundaries. Variable frame rate
        // footage has no fixed grid to snap to, so its cuts keep the requested times.
        var info = await ProbeAsync(inputFile, ct);
        var spans = CutPlanner.PlanFrameAccurateCuts(kept, info.IsConstantFrameRate ? info.FrameRate : 0, info.FirstVideoFrameTime);
        if (spans.Count == 0) throw new InvalidOperationException("No segments left to export.");

        var temp = CreateTempFolder();
        try
        {
            var filterFile = Path.Combine(temp, "cut-filter.txt");
            await File.WriteAllTextAsync(filterFile, CutPlanner.BuildFilterGraph(spans, CutPlanner.DefaultFadeSeconds, info.DurationSeconds), ct);

            var totalDuration = Math.Max(spans.Sum(s => s.Duration), 0.1);
            var result = await RunWithFilterGraphFileAsync(
                filterFile,
                filterOption => $"-nostdin -y -hide_banner -loglevel error -i {Q(inputFile)} {filterOption} -map \"[v]\" -map \"[a]\" {EncodeArgs(outputFile)} -progress pipe:1 {Q(outputFile)}",
                ct,
                line =>
                {
                    var seconds = TryParseProgressSeconds(line);
                    if (seconds.HasValue)
                        progress?.Invoke(Math.Clamp(seconds.Value / totalDuration * 100, 0, 99));
                });

            if (result.ExitCode != 0) throw new InvalidOperationException(ErrorTail(result.StdErr));
            return totalDuration;
        }
        finally { TryDeleteFolder(temp); }
    }

    private async Task<ProcessResult> RunWithFilterGraphFileAsync(
        string filterFile,
        Func<string, string> buildArgs,
        CancellationToken ct,
        Action<string> onStdOutLine)
    {
        if (!_useLegacyFilterScriptOption)
        {
            var result = await ProcessRunner.RunAsync(FfmpegPath, buildArgs($"-/filter_complex {Q(filterFile)}"), ct, onStdOutLine: onStdOutLine);
            if (result.ExitCode == 0 || !result.StdErr.Contains("/filter_complex", StringComparison.Ordinal))
                return result;

            // FFmpeg older than 7.0 rejects the option by name; it only knows the previous spelling.
            _useLegacyFilterScriptOption = true;
        }

        return await ProcessRunner.RunAsync(FfmpegPath, buildArgs($"-filter_complex_script {Q(filterFile)}"), ct, onStdOutLine: onStdOutLine);
    }

    private async Task<double> ExportCutVideoWithStreamCopyAsync(
        string inputFile,
        IReadOnlyList<TimeRange> kept,
        string outputFile,
        CancellationToken ct,
        Action<double>? progress)
    {
        // A copied stream can only start on a keyframe. Starting anywhere else makes FFmpeg back up to
        // the previous keyframe, which replays footage that was already written.
        var keyframes = await GetKeyframeTimesAsync(inputFile, ct);
        if (keyframes.Count == 0)
            throw new InvalidOperationException("Could not read the keyframe positions that stream copy needs. Enable re-encoding to export this clip.");

        var ranges = CutPlanner.AlignToKeyframes(kept, keyframes);
        var extension = Path.GetExtension(outputFile);
        if (string.IsNullOrEmpty(extension))
            extension = ".mp4";

        var temp = CreateTempFolder();
        try
        {
            var parts = new List<string>();
            for (var i = 0; i < ranges.Count; i++)
            {
                var part = Path.Combine(temp, $"part_{i:0000}{extension}");
                parts.Add(part);

                var seek = ranges[i].Start + KeyframeSeekNudge;
                var length = Math.Max(ranges[i].End - seek, 0.001);
                var result = await ProcessRunner.RunAsync(FfmpegPath,
                    $"-nostdin -y -hide_banner -loglevel error -ss {F(seek)} -i {Q(inputFile)} -t {F(length)} -map 0 -map -0:d -map -0:t -c copy -avoid_negative_ts make_zero {Q(part)}", ct);
                if (result.ExitCode != 0) throw new InvalidOperationException(ErrorTail(result.StdErr));
                progress?.Invoke((i + 1) / (double)(ranges.Count + 1) * 100);
            }

            var listFile = Path.Combine(temp, "concat.txt");
            await File.WriteAllLinesAsync(listFile, parts.Select(p => $"file '{p.Replace("'", "'\\''")}'"), ct);
            var fastStart = IsMp4Family(outputFile) ? " -movflags +faststart" : "";
            var concat = await ProcessRunner.RunAsync(FfmpegPath,
                $"-nostdin -y -hide_banner -loglevel error -f concat -safe 0 -i {Q(listFile)} -map 0 -c copy{fastStart} {Q(outputFile)}", ct);
            if (concat.ExitCode != 0) throw new InvalidOperationException(ErrorTail(concat.StdErr));

            return ranges.Sum(r => r.Duration);
        }
        finally { TryDeleteFolder(temp); }
    }

    /// <summary>Lists the keyframe timestamps of the main video stream without decoding it.</summary>
    public async Task<IReadOnlyList<double>> GetKeyframeTimesAsync(string file, CancellationToken ct = default)
    {
        var info = await ProbeAsync(file, ct);
        var result = await ProcessRunner.RunAsync(FfprobePath,
            $"-v error -select_streams V:0 -show_entries packet=pts_time,flags -of csv=p=0 {Q(file)}", ct);

        return ParseKeyframeTimes(result.StdOut, info.StartTime);
    }

    /// <summary>
    /// Reads FFprobe's "pts_time,flags" packet listing. <paramref name="startTime"/> is the container
    /// start time, which FFmpeg subtracts from every timestamp when it seeks and cuts.
    /// </summary>
    public static List<double> ParseKeyframeTimes(string packetListing, double startTime = 0)
    {
        var times = new SortedSet<double>();

        foreach (var line in packetListing.Split('\n'))
        {
            var fields = line.Trim().Split(',');
            if (fields.Length < 2 || !fields[1].Contains('K'))
                continue;

            if (double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var time))
                times.Add(Math.Max(0, time - startTime));
        }

        return times.ToList();
    }

    /// <summary>
    /// Exports every removed pause as its own file. With <paramref name="reencode"/> each clip starts
    /// and ends on the exact frame; without it the streams are copied and clips start on a keyframe.
    /// </summary>
    public async Task ExportPausesOnlyAsync(
        string inputFile,
        IEnumerable<TimelineSegment> segments,
        string outputFolder,
        bool reencode,
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
            var codecArgs = reencode ? $"-map 0:v:0 -map 0:a:0? {EncodeArgs(outFile)}" : "-c copy";
            var result = await ProcessRunner.RunAsync(FfmpegPath,
                $"-nostdin -y -hide_banner -loglevel error -ss {F(s.Start)} -i {Q(inputFile)} -t {F(s.Duration)} {codecArgs} {Q(outFile)}", ct);
            if (result.ExitCode != 0) throw new InvalidOperationException(ErrorTail(result.StdErr));
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

        // -ss ahead of -i jumps straight to the segment. After -i, FFmpeg would decode the whole clip up to that point first.
        var outputPattern = Path.Combine(outputFolder, "frame_%04d.jpg");
        var args = $"-nostdin -y -hide_banner -loglevel error -ss {F(segment.Start)} -t {F(segment.Duration)} -i {Q(inputFile)} -an -sn -dn -vf fps={fps},scale=960:-2 -q:v 3 {Q(outputPattern)}";
        var result = await ProcessRunner.RunAsync(FfmpegPath, args, ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(ErrorTail(result.StdErr));

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

        // Seeking before -i jumps straight to the segment, and skipping frames nothing else depends on
        // cuts the decoding work by about a quarter without changing which picture is shown.
        var args = $"-nostdin -y -hide_banner -loglevel error -skip_frame noref -ss {F(segment.Start)} -t {F(segment.Duration)} -i {Q(inputFile)} -an -sn -dn -vf fps={F(fps)},scale=120:68:force_original_aspect_ratio=increase,crop=120:68 -frames:v {count} -q:v 5 {Q(outputPattern)}";
        var result = await ProcessRunner.RunAsync(FfmpegPath, args, ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(ErrorTail(result.StdErr));

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
        var args = $"-nostdin -y -hide_banner -loglevel error -ss {F(segment.Start)} -t {F(segment.Duration)} -i {Q(inputFile)} -vn -sn -dn -ac 2 -ar 48000 -c:a pcm_s16le {Q(outputFile)}";
        var result = await ProcessRunner.RunAsync(FfmpegPath, args, ct);
        if (result.ExitCode != 0 || !File.Exists(outputFile))
            throw new InvalidOperationException(ErrorTail(result.StdErr));

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

    private static string EncodeArgs(string outputFile)
    {
        const string codecs = "-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p -c:a aac -b:a 192k";
        return IsMp4Family(outputFile) ? codecs + " -movflags +faststart" : codecs;
    }

    private static bool IsMp4Family(string file) =>
        Path.GetExtension(file).ToLowerInvariant() is ".mp4" or ".m4v" or ".mov";

    private static string WorkingPathFor(string outputFile)
    {
        // The extension stays last because FFmpeg picks the container from it.
        var folder = Path.GetDirectoryName(Path.GetFullPath(outputFile)) ?? "";
        var name = Path.GetFileNameWithoutExtension(outputFile);
        var tag = Guid.NewGuid().ToString("N")[..8];
        return Path.Combine(folder, $"{name}.partial-{tag}{Path.GetExtension(outputFile)}");
    }

    private static string CreateTempFolder()
    {
        var temp = Path.Combine(Path.GetTempPath(), "silence-cutter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        return temp;
    }

    private static void TryDeleteFolder(string folder)
    {
        try { Directory.Delete(folder, true); } catch { }
    }

    private static void TryDeleteFile(string file)
    {
        try { File.Delete(file); } catch { }
    }

    /// <summary>The last few lines FFmpeg wrote, which is where it explains a failure.</summary>
    private static string ErrorTail(string stdErr)
    {
        var lines = stdErr
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0 && line != "Conversion failed!")
            .TakeLast(3)
            .ToList();

        return lines.Count == 0 ? "FFmpeg failed without an error message." : string.Join(" ", lines);
    }

    private static bool IsAttachedPicture(JsonElement stream) =>
        stream.TryGetProperty("disposition", out var disposition) &&
        disposition.ValueKind == JsonValueKind.Object &&
        disposition.TryGetProperty("attached_pic", out var attached) &&
        attached.ValueKind == JsonValueKind.Number &&
        attached.GetInt32() != 0;

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? ReadDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number)
            return value.GetDouble();

        return value.ValueKind == JsonValueKind.String &&
               double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
               double.IsFinite(parsed)
            ? parsed
            : null;
    }

    private static double ParseRate(string? rate)
    {
        if (string.IsNullOrWhiteSpace(rate))
            return 0;

        var parts = rate.Split('/');
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator))
            return 0;

        if (parts.Length == 1)
            return numerator;

        return double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) && denominator > 0
            ? numerator / denominator
            : 0;
    }

    private static double Parse(string v) => double.Parse(v, CultureInfo.InvariantCulture);

    private static double? TryParseProgressSeconds(string line)
    {
        var match = ProgressTimeRegex.Match(line);
        if (!match.Success)
            return null;

        return Parse(match.Groups["h"].Value) * 3600 + Parse(match.Groups["m"].Value) * 60 + Parse(match.Groups["s"].Value);
    }

    private static string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    private static string Q(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";
}
