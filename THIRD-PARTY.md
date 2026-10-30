# Third-party software

UploadFit's application source is licensed under the MIT license in `LICENSE`.

## FFmpeg

UploadFit invokes FFmpeg, FFprobe and FFplay as separate command-line programs. It does not link to their libraries. The application packages do not redistribute these executables. The app offers a first-run download directly from the upstream binary publisher or an offline import of the same archive.

- Project: https://ffmpeg.org/
- Binary publisher: https://www.gyan.dev/ffmpeg/builds/
- Pinned build: https://github.com/GyanD/codexffmpeg/releases/tag/9.0.2
- Binary archive: `ffmpeg-9.0.2-essentials_build.zip`
- Archive SHA-256: `60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba`
- License: GPLv3, as stated by the binary publisher.
- FFmpeg source revision linked by the publisher: https://github.com/FFmpeg/FFmpeg/commit/946fcce07b
- Build configuration, component versions, source links and notices: the publisher's build page and archive documentation.
- Licensing information: https://ffmpeg.org/legal.html

The installer verifies the archive checksum and pinned hashes for all three executables. It copies the archive's license and README when present. Updating the pinned build requires reviewing the publisher's release, updating all hashes, and rerunning media, installation and UI tests. No floating "latest" engine URL is executed.

## .NET

Microsoft .NET and Windows Desktop runtime are bundled in self-contained application packages under their applicable MIT and third-party notices. Runtime distribution notices accompany the packages. Source and license information: https://github.com/dotnet/runtime and https://github.com/dotnet/wpf.
