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
- **Export EDL Cut List** creates a video-only edit decision list that marks selected sections at their original timeline positions for Resolve or similar workflows.
- **Export CSV Cut List** writes section timing and removal state for review or post-processing.
- Re-encoding is recommended for frame-accurate cuts.
- Stream copy mode is faster, but cuts can land near keyframes instead of exact frames.

## Current Scope

The app exports one selected clip at a time. Batch export can be added later by iterating over analyzed clips.

Future detection work can build on the same section removal model, including filler-word or hesitation detection such as "hmm" and "aaa".
