# Architecture

## Boundaries

`UploadFit.Core` handles metadata, size planning, process arguments, engine acquisition, cancellation, output verification and filesystem operations. It has no UI framework or third-party NuGet dependencies. `UploadFit.App` is a WPF desktop application with an in-memory queue. The UI calls asynchronous core operations and never constructs shell command strings.

## Encoding

1. Probe an allowed local media container with FFprobe. Ignore cover art when choosing the video stream.
2. Validate the trim, limit, resolution and frame rate. Reserve 6 percent of the budget plus 64 KiB for muxing and audio priming. Subtract the AAC bitrate from the remaining video budget.
3. Choose an even output size from the display aspect ratio, including rotation and sample aspect ratio. Auto mode chooses resolution using the bitrate per pixel per frame.
4. Lock the original for reading. Reject a source whose size or modification time changed after probing.
5. Run two libx264 passes in a unique scratch directory on the output volume. Every argument is passed through `ProcessStartInfo.ArgumentList`; no shell is involved. Audio is encoded only in the second pass.
6. Inspect actual bytes. If over budget, lower the bitrate after accounting for the audio cost and retry both passes, at most three attempts. Never use `-fs` to truncate a clip.
7. Probe the output and check H.264/AAC codecs, 8-bit SDR pixels, frame rate, square pixels, rotation, duration, planned dimensions, audio presence and size. Move it without overwrite to an available `-fit.mp4` filename on the same volume.
8. Remove only the current job's flat scratch files. Cancellation kills the process tree and waits for handles to close before cleanup. Cleanup retries transient Windows I/O failures up to six times over 750 milliseconds, independently of the cancelled job token. Persistent locks leave the owned scratch directory in place; cleanup never recursively deletes nested content.

HDR transfer metadata triggers a zscale/Hable SDR conversion. Frame previews use the same tone mapping. Output drops source metadata, chapters, subtitles and additional tracks.

The process runner watches both output readers and process exit until all complete. Closing one pipe does not disable error observation on the other. A reader rejection kills and reaps the child before scratch cleanup, instead of waiting for the process timeout.

## Files and network

Remote protocols and playlist demuxers are not allowed in probing, preview or encoding. Local source/output paths reject reparse points and UNC paths. FFmpeg and FFprobe use explicit format and protocol allowlists. These checks reduce accidental side effects; they are not a sandbox for a hostile native decoder. Keep the pinned engine updated.

Only engine acquisition uses HTTP. It fetches a fixed upstream HTTPS archive and verifies its committed SHA-256 before extracting specific expected members to an owned directory. Executables each have independent pinned hashes. No update service, account system or video upload is implemented.

## State and errors

The queue and clip paths live in memory. Preferences use staged writes. Invalid preferences fall back to defaults. Invalid per-clip edits are retained and block the queue before export, so a previous valid setting cannot silently substitute for visible invalid input. A failed clip keeps its error and can be retried; later clips in the queue continue. Completed exports are preserved on cancellation.
