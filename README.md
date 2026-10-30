# UploadFit

**Fit a video under an upload limit, on your own computer.**

UploadFit is a Windows desktop utility for sharing game clips in Discord and other apps. Add videos, choose a file size, trim the part you want, and export a queue of MP4 files. The encoder checks the result before marking it ready.

Version 0.1.0 is an experimental preview. [Tagged releases](https://github.com/informent/UploadFit/releases) contain verified Windows packages and their validation reports. Automated media and in-process app checks cover the main workflows; physical desktop input, accessibility, and display-scaling acceptance remain unfinished. This repository contains the local application and encoding code. FFmpeg is installed separately as described below.

## What it does

- Custom limits from 1 to 500 MB, with 10, 20 and 50 MB shortcuts. MB means 1,000,000 bytes.
- Two-pass H.264 encoding with an audio budget, container overhead reserve, and up to three size-fitting attempts.
- Frame previews, precise trim times, and a separate playback window for the selected section.
- A queue of up to 100 clips with individual trims, shared export settings, cancellation, failure details and retry.
- Automatic resolution based on the available bitrate, optional resolution caps, 30/60 fps caps, and three encoding effort levels.
- Optional audio removal, AAC stereo output, portrait/rotation handling, non-square pixel correction, and HDR-to-SDR conversion.
- Finished-file size, duration, H.264/AAC codecs, 8-bit SDR pixels, frame rate, rotation, pixel shape and dimension checks. A file that fails verification is not offered as a finished export.
- New output names and atomic promotion from scratch files. Existing files and source videos are never overwritten.
- Show finished files in Explorer or copy a file to paste into Discord.

## Privacy and engine setup

Videos are processed locally. There are no accounts, bot tokens, analytics, upload endpoints or automatic updates. Clip paths and queue history are not saved between sessions. Export preferences and the output folder are saved in `%LOCALAPPDATA%\UploadFit\preferences.json`.

On first use, choose **Download video engine**. This downloads the pinned FFmpeg 9.0.2 essentials build directly from [Gyan Doshi's GitHub release](https://github.com/GyanD/codexffmpeg/releases/tag/9.0.2), about 109 MB. UploadFit verifies the archive and all three executables against committed SHA-256 hashes before using them. After setup, compression and preview work offline. You can also import that exact ZIP on an offline computer. The downloaded engine stays under `%LOCALAPPDATA%\UploadFit\engine\9.0.2`.

FFmpeg is a separate GPLv3 program, downloaded from its publisher, and is not included in UploadFit's application packages. See [third-party notices](THIRD-PARTY.md).

## Using it

1. Add local MP4, MOV, MKV, WebM, AVI, M4V, WMV, MPEG, TS/MTS/M2TS or OGV videos. Drag and drop or use **Ctrl+O**.
2. Choose the limit displayed by the destination app. Discord's limits can vary by account and server; the presets do not detect your subscription.
3. Scrub to a frame and set the start and end, or type seconds, `mm:ss`, or `hh:mm:ss.fff`.
4. Choose an output folder, then **Export queue**. A lower limit or longer clip may require lower resolution. A shorter selection usually preserves more detail.
5. Select a completed clip and choose **Show file** or **Copy file**.

**Space** plays the selection when you are not typing. **Escape** cancels active work. **Delete** removes the selected queue entry. Removing entries does not delete files. Settings apply to the selected clip; **Apply settings to queued clips** keeps each clip's trim.

## Requirements and limits

Windows 10/11 x64. The default release packaging bundles .NET 10. Packages explicitly marked `framework-dependent` are development previews and require the .NET 10 Desktop Runtime separately. The media engine requires a one-time download or offline ZIP import. Source builds require the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

Input files must be local, between 0.25 seconds and 24 hours, and no larger than 20 GB. Linked paths, network shares, playlists and remote media protocols are excluded. Output is H.264, 8-bit SDR, square pixels and optional AAC stereo. Subtitles, extra audio tracks, chapters and source metadata are not copied. UploadFit uses the first video stream that is not cover art and the first audio stream. It does not upscale the display resolution or frame rate. Playback opens a separate FFplay window.

Encoding is lossy. Extremely small budgets are rejected with a suggestion to trim or raise the limit. The app verifies encoded size, but does not guarantee that a service will accept a file for reasons unrelated to size. Disk space must cover the final file and the temporary encoded file. Cancellation preserves completed outputs and cleans up the current job's scratch files, retrying temporary Windows file locks. A power loss or a persistent file lock can leave a `.uploadfit-*` scratch folder in the chosen output directory; it contains only that interrupted job's work.

## Development

```powershell
dotnet build src/UploadFit.App -c Release
dotnet run --project tests/UploadFit.Tests -c Release
dotnet run --project src/UploadFit.App
```

For real media tests, pass a verified FFmpeg bin directory and optionally the original pinned ZIP:

```powershell
dotnet run --project tests/UploadFit.Tests -c Release -- C:\path\to\bin C:\path\to\ffmpeg-9.0.2-essentials_build.zip
```

The app supports `--data-dir <folder>` and `--engine-dir <folder>` for isolated development and testing. An alternate engine directory must contain the exact pinned executables. Remaining arguments are video filenames to add.

To build the app, verify the engine pin, and run the complete suite with a saved log and source-hash report:

```powershell
./scripts/Test-UploadFit.ps1 -EngineArchive C:\path\to\ffmpeg-9.0.2-essentials_build.zip
```

Use `-DownloadEngine` instead to explicitly download the pinned archive. Without either option, the script runs only the build and core tests. GitHub CI is configured to run the complete suite on Windows.

The complete suite also exercises the compiled WPF resources and queue workflows in process. It does not replace visual, keyboard, file-dialog, or clipboard acceptance testing.

After full validation, build a preview from that exact checked source:

```powershell
./scripts/Build-Package.ps1 -ValidationReport .\artifacts\validation-<run-id>\VALIDATION.json
```

The package script checks the validated source hashes, publishes Windows x64, creates a file manifest and ZIP, verifies the extracted files, and starts the extracted app in diagnostic mode. It creates `PACKAGE-VALIDATION.json` and `SHA256SUMS` alongside the ZIP. Use `-FrameworkDependent` only for a development package that requires the .NET 10 Desktop Runtime. Experimental releases include the exact automated results and disclose the remaining acceptance work; they do not establish stable-release readiness.

See [validation](VALIDATION.md), [architecture](docs/ARCHITECTURE.md) and [release notes](CHANGELOG.md). Report reproducible bugs through [GitHub issues](https://github.com/informent/UploadFit/issues). UploadFit is an independent project by informent, not affiliated with Discord.
