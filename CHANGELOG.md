# Changelog

## 0.1.0 preview

First UploadFit desktop release: local video compression to an explicit file size, frame previews, trimming, queue export, cancellation and retry, H.264/AAC output, optional mute, automatic resolution, frame rate caps, HDR-to-SDR conversion, verified output and non-overwriting file handling.

The video engine is downloaded once from its publisher or imported from the exact pinned ZIP. Default release packaging bundles the .NET runtime and does not include FFmpeg binaries. Framework-dependent development previews require the .NET 10 Desktop Runtime separately.

### Preview validation fixes

- Retry temporary Windows file locks during scratch cleanup after export, cancellation, frame preview, and engine installation.
- Preserve total hours in trim endpoints and queue summaries, including a 24-hour video.
- Avoid retaining a disposed preview cancellation source when no clip or engine is available.
- Fail promptly when a media-process reader rejects excessive output after another pipe has closed.
- Verify the encoded audio codec, pixel format, SDR output, frame rate, rotation, pixel shape, and planned dimensions before publishing an export.
- Add 30 core/media tests and nine in-process WPF workflow tests, including pinned engine verification and source-hash evidence.
- Add package generation with stale-source rejection, extracted-file checksums, and an application startup diagnostic. Windows CI builds the self-contained preview; release assets record its exact results. Interactive desktop and display-scaling acceptance remain pending, so this version is an experimental prerelease.
