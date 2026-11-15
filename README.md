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

Analysis decodes only the first audio track and never the picture, so long recordings are analyzed quickly. With the adaptive threshold on, loudness is measured in the same pass; a second pass only runs when the suggested threshold ends up above the manual one.

Silence at the very start or end of a clip is cut right up to the edge. Padding is only kept on the side of a pause that touches speech.

## Export Notes

- **Export Cut Video** renders the selected clip without sections marked for removal.
- **Export Pauses Only** exports removed pause sections as separate files. It follows the re-encode setting: re-encoded pause clips start and end on the exact frame, stream-copied ones start on a keyframe.
- Cut-video and pause-only exports are added to the export queue, so the selected clip's cut decisions are captured and rendered in the background.
- **Export EDL Cut List** creates a video-only edit decision list that marks selected sections at their original timeline positions for Resolve or similar workflows.
- **Export CSV Cut List** writes section timing and removal state for review or post-processing. Numbers always use a dot as the decimal separator.
- A cut video is rendered to a `.partial` file next to the destination and renamed when it is complete, so a failed or cancelled export never replaces an existing file with a broken one.

### Re-encode (recommended)

- Cuts land on exact frame boundaries. On constant frame rate footage each cut is moved outward to the nearest frame, so picture and sound cover exactly the same time and stay in sync through every cut. This keeps at most one extra frame on each side of a cut.
- A 10 ms audio fade is applied on both sides of every cut, which removes the click that a hard cut in the middle of a waveform produces.
- Kept sections that touch each other are rendered as one piece, so un-marking a pause leaves no cut behind.
- Video is encoded with x264 (`veryfast`, CRF 18) and audio with AAC at 192 kb/s. The first video and first audio track are exported.

### Stream copy

- Nothing is re-encoded, so there is no quality loss and the export is many times faster.
- A copied video can only start on a keyframe. Each kept section therefore starts on the keyframe before it, and a pause is only removed when a keyframe falls inside it. Pauses that cannot be removed without repeating footage stay in, and the app reports how many seconds that was.
- How much gets removed depends on the recording: footage with a keyframe every second or two cuts well, footage with a keyframe every 8-10 seconds keeps most of its pauses. Use re-encode when the cuts matter.
- Each join can show a few extra frames and a very short audio gap.

## Current Scope

The app queues long media exports one job at a time while keeping the editor available for the next clip.

Future detection work can build on the same section removal model, including filler-word or hesitation detection such as "hmm" and "aaa".
