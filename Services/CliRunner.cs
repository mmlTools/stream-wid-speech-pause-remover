using StreamWID.Models;
using System.Globalization;

namespace StreamWID.Services;

public static class CliRunner
{
    public static bool ShouldRunCli(string[] args) =>
        args.Length > 0 && !args.Contains("--gui", StringComparer.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct = default)
    {
        try
        {
            var options = CliOptions.Parse(args);
            if (options.ShowHelp)
            {
                await output.WriteLineAsync(GetHelpText());
                await output.FlushAsync();
                return 0;
            }

            if (options.InstallCli)
            {
                CliIntegrationService.InstallCli();
                await output.WriteLineAsync($"Installed swid launcher: {CliIntegrationService.ShimPath}");
                await output.FlushAsync();
                return 0;
            }

            if (options.InstallContextMenu)
            {
                CliIntegrationService.InstallWindowsContextMenu();
                await output.WriteLineAsync("Installed StreamWID media file context menu entries.");
                await output.FlushAsync();
                return 0;
            }

            if (string.IsNullOrWhiteSpace(options.InputFile))
            {
                await error.WriteLineAsync("Missing clip path.");
                await error.WriteLineAsync("Run: swid --help");
                await error.FlushAsync();
                return 2;
            }

            if (!File.Exists(options.InputFile))
            {
                await error.WriteLineAsync($"Clip not found: {options.InputFile}");
                await error.FlushAsync();
                return 2;
            }

            var settings = AppSettings.Load();
            ApplyOverrides(settings, options);
            settings.Save();

            var ffmpeg = new FfmpegService();
            if (!await ffmpeg.EnsureToolsAvailableAsync(ct, message => output.WriteLine(message)))
                throw new InvalidOperationException("FFmpeg was not found and could not be installed automatically. Install FFmpeg and make sure it is available in PATH.");

            await output.WriteLineAsync($"Analyzing {Path.GetFileName(options.InputFile)}...");
            await output.FlushAsync();
            var result = await ffmpeg.DetectSegmentsAsync(
                options.InputFile,
                settings.ThresholdDb,
                settings.MinSilenceSeconds,
                settings.KeepPaddingSeconds,
                settings.UseAdaptiveThreshold,
                ct,
                (stage, progress) => PrintProgress(output, stage, progress));

            await output.WriteLineAsync();
            await output.WriteLineAsync($"{result.Segments.Count(s => s.Kind == SegmentKind.Silence)} pauses found. Threshold used: {result.AdaptiveThresholdDb:0.##} dB");
            await output.FlushAsync();

            if (options.ExportMode == CliExportMode.Csv)
            {
                var outputFile = options.OutputPath ?? BuildOutputPath(options.InputFile, "_cutlist.csv");
                await EdlExporter.ExportCutListCsvAsync(outputFile, BuildClip(options.InputFile, result.Segments));
                await output.WriteLineAsync($"CSV written: {outputFile}");
                await output.FlushAsync();
                return 0;
            }

            if (options.ExportMode == CliExportMode.Edl)
            {
                var outputFile = options.OutputPath ?? BuildOutputPath(options.InputFile, "_cutlist.edl");
                await EdlExporter.ExportCutListEdlAsync(outputFile, BuildClip(options.InputFile, result.Segments), settings.ResolveFps);
                await output.WriteLineAsync($"EDL written: {outputFile}");
                await output.FlushAsync();
                return 0;
            }

            if (options.ExportMode == CliExportMode.PausesOnly)
            {
                var outputFolder = options.OutputPath ?? BuildOutputFolder(options.InputFile, "_pauses");
                await ffmpeg.ExportPausesOnlyAsync(options.InputFile, result.Segments, outputFolder, ct, p => PrintProgress(output, "Exporting", p));
                await output.WriteLineAsync();
                await output.WriteLineAsync($"Pause clips written: {outputFolder}");
                await output.FlushAsync();
                return 0;
            }

            var cutFile = options.OutputPath ?? BuildOutputPath(options.InputFile, "_cut.mp4");
            await ffmpeg.ExportCutVideoAsync(options.InputFile, result.Segments, cutFile, settings.ReencodeExports, ct, p => PrintProgress(output, "Exporting", p));
            await output.WriteLineAsync();
            await output.WriteLineAsync($"Cut video written: {cutFile}");
            await output.FlushAsync();
            return 0;
        }
        catch (Exception ex)
        {
            await error.WriteLineAsync(ex.Message);
            await error.FlushAsync();
            return 1;
        }
    }

    public static string GetHelpText() =>
        """
        StreamWID CLI

        Usage:
          swid [options] <clip-path>
          swid --install-cli
          swid --install-context-menu

        Options:
          --threshold <db>       Silence threshold, for example -35
          --min-pause <seconds>  Minimum pause length, for example 0.45
          --padding <seconds>    Padding kept around speech, for example 0.08
          --adaptive             Use adaptive threshold suggestions
          --no-adaptive          Use the manual threshold exactly
          --reencode             Export frame-accurate re-encoded cuts
          --stream-copy          Export faster keyframe-based cuts
          --output <path>        Output file or folder
          --pauses-only          Export removed pauses as separate clips
          --edl                  Export an EDL cut list
          --csv                  Export a CSV cut list
          --help                 Show this help

        The GUI and CLI share saved settings in the user's StreamWID app data.
        """;

    private static void ApplyOverrides(AppSettings settings, CliOptions options)
    {
        if (options.ThresholdDb.HasValue) settings.ThresholdDb = options.ThresholdDb.Value;
        if (options.MinSilenceSeconds.HasValue) settings.MinSilenceSeconds = options.MinSilenceSeconds.Value;
        if (options.KeepPaddingSeconds.HasValue) settings.KeepPaddingSeconds = options.KeepPaddingSeconds.Value;
        if (options.UseAdaptiveThreshold.HasValue) settings.UseAdaptiveThreshold = options.UseAdaptiveThreshold.Value;
        if (options.ReencodeExports.HasValue) settings.ReencodeExports = options.ReencodeExports.Value;
    }

    private static MediaClip BuildClip(string inputFile, IReadOnlyList<TimelineSegment> segments) => new()
    {
        FilePath = inputFile,
        FileName = Path.GetFileName(inputFile),
        DurationSeconds = segments.Sum(s => s.Duration),
        Segments = new System.Collections.ObjectModel.ObservableCollection<TimelineSegment>(segments)
    };

    private static string BuildOutputPath(string inputFile, string suffix)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(inputFile)) ?? Environment.CurrentDirectory;
        return Path.Combine(folder, Path.GetFileNameWithoutExtension(inputFile) + suffix);
    }

    private static string BuildOutputFolder(string inputFile, string suffix)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(inputFile)) ?? Environment.CurrentDirectory;
        return Path.Combine(folder, Path.GetFileNameWithoutExtension(inputFile) + suffix);
    }

    private static void PrintProgress(TextWriter output, string label, double progress)
    {
        output.Write($"\r{label}... {Math.Clamp(progress, 0, 100):0}%");
        output.Flush();
    }

    private enum CliExportMode
    {
        CutVideo,
        PausesOnly,
        Edl,
        Csv
    }

    private sealed class CliOptions
    {
        public string? InputFile { get; init; }
        public string? OutputPath { get; init; }
        public double? ThresholdDb { get; init; }
        public double? MinSilenceSeconds { get; init; }
        public double? KeepPaddingSeconds { get; init; }
        public bool? UseAdaptiveThreshold { get; init; }
        public bool? ReencodeExports { get; init; }
        public bool ShowHelp { get; init; }
        public bool InstallCli { get; init; }
        public bool InstallContextMenu { get; init; }
        public CliExportMode ExportMode { get; init; }

        public static CliOptions Parse(string[] args)
        {
            var inputFiles = new List<string>();
            var options = new CliOptionsBuilder();

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg.ToLowerInvariant())
                {
                    case "--help":
                    case "-h":
                    case "/?":
                        options.ShowHelp = true;
                        break;
                    case "--install-cli":
                    case "--setup-cli":
                        options.InstallCli = true;
                        break;
                    case "--install-context-menu":
                    case "--setup-context-menu":
                        options.InstallContextMenu = true;
                        break;
                    case "--auto":
                        break;
                    case "--threshold":
                    case "-t":
                        options.ThresholdDb = ReadDouble(args, ref i, arg);
                        break;
                    case "--min-pause":
                    case "--min-silence":
                    case "-m":
                        options.MinSilenceSeconds = ReadDouble(args, ref i, arg);
                        break;
                    case "--padding":
                    case "-p":
                        options.KeepPaddingSeconds = ReadDouble(args, ref i, arg);
                        break;
                    case "--adaptive":
                        options.UseAdaptiveThreshold = true;
                        break;
                    case "--no-adaptive":
                        options.UseAdaptiveThreshold = false;
                        break;
                    case "--reencode":
                        options.ReencodeExports = true;
                        break;
                    case "--stream-copy":
                        options.ReencodeExports = false;
                        break;
                    case "--output":
                    case "-o":
                        options.OutputPath = ReadValue(args, ref i, arg);
                        break;
                    case "--pauses-only":
                        options.ExportMode = CliExportMode.PausesOnly;
                        break;
                    case "--edl":
                        options.ExportMode = CliExportMode.Edl;
                        break;
                    case "--csv":
                        options.ExportMode = CliExportMode.Csv;
                        break;
                    default:
                        if (arg.StartsWith("-", StringComparison.Ordinal))
                            throw new ArgumentException($"Unknown option: {arg}");
                        inputFiles.Add(arg);
                        break;
                }
            }

            if (inputFiles.Count > 1)
                throw new ArgumentException("Only one clip path can be processed per CLI run.");

            options.InputFile = inputFiles.FirstOrDefault();
            return options.Build();
        }

        private static string ReadValue(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length)
                throw new ArgumentException($"{option} needs a value.");

            return args[++index];
        }

        private static double ReadDouble(string[] args, ref int index, string option)
        {
            var value = ReadValue(args, ref index, option);
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                throw new ArgumentException($"{option} needs a numeric value.");

            return parsed;
        }
    }

    private sealed class CliOptionsBuilder
    {
        public string? InputFile { get; set; }
        public string? OutputPath { get; set; }
        public double? ThresholdDb { get; set; }
        public double? MinSilenceSeconds { get; set; }
        public double? KeepPaddingSeconds { get; set; }
        public bool? UseAdaptiveThreshold { get; set; }
        public bool? ReencodeExports { get; set; }
        public bool ShowHelp { get; set; }
        public bool InstallCli { get; set; }
        public bool InstallContextMenu { get; set; }
        public CliExportMode ExportMode { get; set; }

        public CliOptions Build() => new()
        {
            InputFile = InputFile,
            OutputPath = OutputPath,
            ThresholdDb = ThresholdDb,
            MinSilenceSeconds = MinSilenceSeconds,
            KeepPaddingSeconds = KeepPaddingSeconds,
            UseAdaptiveThreshold = UseAdaptiveThreshold,
            ReencodeExports = ReencodeExports,
            ShowHelp = ShowHelp,
            InstallCli = InstallCli,
            InstallContextMenu = InstallContextMenu,
            ExportMode = ExportMode
        };
    }
}
