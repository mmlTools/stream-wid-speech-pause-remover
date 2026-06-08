# StreamWID

StreamWID is a desktop video utility for finding pauses in speech recordings, reviewing the result in an editor-style timeline, and exporting a cleaner cut.

It uses FFmpeg for analysis, preview generation, and export. The app is built with Avalonia and .NET 8.

## Features

- Add one or more video clips by browsing or drag and drop.
- Analyze selected clips or all loaded clips.
- Detect pause sections with FFmpeg `silencedetect`.
- Use adaptive threshold suggestions or manual detection settings.
- Review detected sections with video thumbnails in the timeline rail and section list.
- Preview speech sections with synced frame preview and audio playback.
- Click any section to mark it for removal or keep it.
- Select or deselect all speech sections.
- Select or deselect all pause sections.
- Remove uploaded clips from the list with the clip context menu.
- Queue long cut-video and pause-only exports while continuing to review other clips.
- Save the last detection/export settings in user data so the GUI, CLI, and context menu use the same defaults.
- Add a `swid` command to the user `PATH` from the top bar or with `swid --install-cli`.
- Add a media file right-click context menu entry on Windows with the top-bar menu button or `swid --install-context-menu`.
- Export from a compact export menu:
  - cut video
  - pause-only clips
  - EDL cut list
  - CSV cut list

## Requirements

- .NET 8 SDK
- FFmpeg, FFprobe, and FFplay installed and available in `PATH`

Check your FFmpeg tools:

```bash
ffmpeg -version
ffprobe -version
ffplay -version
```

## Run

```bash
dotnet restore
dotnet run
```

## CLI

After adding the CLI to `PATH`, open a new terminal and run:

```bash
swid [options] "path/to/clip.mp4"
```

By default, the CLI analyzes the clip with your saved settings and exports `clip_cut.mp4` next to the source file.

Fine-tuning options:

```bash
swid --threshold -35 --min-pause 0.45 --padding 0.08 "path/to/clip.mp4"
swid --no-adaptive --stream-copy --output "path/to/output.mp4" "path/to/clip.mp4"
swid --pauses-only "path/to/clip.mp4"
swid --edl "path/to/clip.mp4"
swid --csv "path/to/clip.mp4"
```

Setup commands:

```bash
swid --install-cli
swid --install-context-menu
```

On Windows, the context menu command is added for common media extensions and runs:

```bash
swid --auto "path/to/clip.mp4"
```

The CLI and GUI both read and write the same settings file in the user's StreamWID app data folder.

## Workflow

1. Add video clips with **Browse Clips** or drag files into the clip list.
2. Pick a detection preset or adjust threshold, minimum pause, and padding.
3. Click **Analyze Selected** or **Analyze All**.
4. Review the preview, thumbnail timeline, and section list.
5. Click section rows to toggle whether they will be cut.
6. Use **Select All Speech** or **Select All Pauses** to quickly mark groups.
7. Open **Export** and choose the output you need.

## Detection Settings

Good starting values for voice recordings:

- Threshold: `-35 dB`
- Minimum pause: `0.45s`
- Padding: `0.08s`

If quiet words are being removed, reduce sensitivity with a less negative threshold such as `-30 dB`, or increase minimum pause length.

If pauses are missed, increase sensitivity with a more negative threshold such as `-40 dB`, or reduce minimum pause length.

## Export Notes

- **Export Cut Video** renders the selected clip without sections marked for removal.
- **Export Pauses Only** exports removed pause sections as separate files.
- Cut-video and pause-only exports are added to the export queue, so the selected clip's cut decisions are captured and rendered in the background.
- **Export EDL Cut List** creates a video-only edit decision list that marks selected sections at their original timeline positions for Resolve or similar workflows.
- **Export CSV Cut List** writes section timing and removal state for review or post-processing.
- Re-encoding is recommended for frame-accurate cuts.
- Stream copy mode is faster, but cuts can land near keyframes instead of exact frames.

## Current Scope

The app queues long media exports one job at a time while keeping the editor available for the next clip.

Future detection work can build on the same section removal model, including filler-word or hesitation detection such as "hmm" and "aaa".
