# Validation

## Completed local checks

Checked on Windows x64 on October 22, 2026 with .NET SDK 10.0.402 and the pinned Gyan FFmpeg 9.0.2 essentials build.

- Release build of the WPF app: passed, with zero warnings and zero errors.
- Regression executable: 30 tests passed, including real FFmpeg media workflows.
- Nine in-process WPF workflow tests passed: missing-engine setup state, saved preferences, imports and duplicates, decoded frame previews, invalid edits blocking export, independent trims, queue export, requeue without overwrite, and clearing entries without deleting files.
- The pinned ZIP was checked before extraction, and all three executable hashes were checked before the integration tests ran.
- Three consecutive cancellations preserved finished files, removed the interrupted job's scratch files, and allowed a successful retry.
- A regression check held a Windows file lock during cleanup, released it, and verified that bounded retries removed the scratch directory without touching unrelated files. Separate checks preserved nested and unrelated directories.
- Time formatting round trips include the 24-hour trim endpoint and hour-long queue durations.
- A child process that closes one output pipe before emitting excessive metadata now fails promptly with the correct metadata error. The regression reproduced the previous timeout before the fix.
- Output verification checks 8-bit SDR pixels, AAC audio, frame rate, square pixels, rotation, dimensions, duration, and size.

A framework-dependent Windows x64 preview ZIP was built, extracted, and checked against its file manifest. The extracted app's diagnostic mode loaded its WPF resources and core assembly successfully under .NET 10.0.13. This package requires a separately available .NET 10 Desktop Runtime. Its package gate rejects core-only validation, changed source files, and changed source file sets.

At the October 22 local checkpoint, self-contained runtime packs were not cached, so only the framework-dependent package could be checked. The GitHub workflow builds and verifies a self-contained package after the complete test run. Each tagged release carries its own CI link and package validation evidence; this local checkpoint is historical.

The repeatable validation script builds the app, runs the tests, and writes a log plus a JSON report with source-file SHA-256 hashes under `artifacts/validation-*/`. The workflow in `.github/workflows/ci.yml` runs the same command with an explicit engine download. Release evidence records the run for the tagged source commit.

```powershell
./scripts/Test-UploadFit.ps1 -EngineArchive C:\path\to\ffmpeg-9.0.2-essentials_build.zip
# Or explicitly fetch the pinned engine:
./scripts/Test-UploadFit.ps1 -DownloadEngine
```

Without an archive or `-DownloadEngine`, the script runs the build and 20 core tests only on Windows. Such a report has `mediaIntegration: false`, skips the WPF media workflows, and is not full release validation.

The regression executable exercises budget arithmetic, time parsing, trim validation, source aspect ratio, metadata selection, output verification, non-overwrite promotion, preference recovery and engine checksum rejection. Real FFmpeg fixtures cover H.264/AAC output, trimming, mute, PNG previews, cancellation and retry, changed sources, disguised playlists, portrait WebM, rotation, non-square pixels and HDR conversion.

Visual design was implemented as a native WPF interface. The 12ui service could not generate candidates because its CLI account was not connected. No generated design or pixel-matching claim is made.

## Remaining acceptance work

- Exercise the visible Windows interface with physical input: engine setup and cancellation, file dialogs, drag and drop, frame seeking, playback, showing/copying files, and closing during work. The in-process checks do not establish these interactive behaviors.
- Check keyboard navigation, readable error states, long filenames, the minimum window size, and common display scaling settings.
- Verify operation on a separate Windows machine without a separately installed .NET runtime. The package diagnostic checks bundled runtime files, extracted hashes, version, and application resource loading.
- Keep release-specific Windows CI, package validation, and download checks tied to the tagged source commit. The historical local checks above do not substitute for those release checks.

Version 0.1.0 is an experimental prerelease while physical desktop and display-scaling acceptance remains unfinished. Its release notes and validation assets must preserve that limitation. A stable release requires those checks to be completed.

Synthetic media fixtures validate mechanics. They do not establish subjective visual quality on every game, GPU, capture tool or HDR display. The app uses CPU encoding for consistent size targeting. Outputs are intended for user review before sharing.
