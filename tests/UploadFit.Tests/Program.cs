using System.Globalization;
using System.Security.Cryptography;
using UploadFit.Core;

if (args is ["--process-fixture"]) return await ProcessFixture.RunAsync();

try
{
var passed = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); }
void Test(string name, Action action) { action(); Console.WriteLine("PASS " + name); passed++; }
async Task AsyncTest(string name, Func<Task> action) { await action(); Console.WriteLine("PASS " + name); passed++; }
void Reject(Action action) { try { action(); } catch (MediaException) { return; } throw new Exception("Expected rejection"); }
async Task RejectAsync(Func<Task> action) { try { await action(); } catch (MediaException) { return; } throw new Exception("Expected rejection"); }
var source = new MediaInfo("clip.mp4", 50_000_000, DateTime.UtcNow, 60, 1920, 1080, 60, true, false, "h264", 0, 1);

if (OperatingSystem.IsWindows())
    await AsyncTest("metadata overflow fails promptly after the other pipe closes", async () => {
        var runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var host = Path.GetFullPath(Path.Combine(runtime, "..", "..", "..", "dotnet.exe"));
        try
        {
            await MediaProcess.RunAsync(host, [System.Reflection.Assembly.GetExecutingAssembly().Location, "--process-fixture"],
                default, TimeSpan.FromSeconds(5));
            throw new Exception("Metadata overflow was accepted");
        }
        catch (MediaException ex)
        {
            Check(ex.Message.Contains("too much metadata", StringComparison.Ordinal), "Reader failure was masked by a process timeout: " + ex.Message);
        }
    });

Test("budget reserves room for audio and container", () => {
    var p = EncodingPlanner.Create(source, new(TargetMb: 10));
    Check((p.VideoKbps + p.AudioKbps) * 1000d * p.Duration / 8 < p.LimitBytes * 0.95, "No budget margin");
    Check(p.AudioKbps == 128 && p.Fps == 30 && p.Width % 2 == 0, "Unexpected settings");
});
Test("trim uses selected duration for bitrate", () => {
    var whole = EncodingPlanner.Create(source, new());
    var part = EncodingPlanner.Create(source, new(Start: 10, End: 20));
    Check(part.Duration == 10 && part.VideoKbps > whole.VideoKbps * 5, "Trim budget is wrong");
});
Test("bad limits and timing rejected", () => {
    foreach (var option in new[] { new ExportOptions(TargetMb: 0), new(TargetMb: 501), new(Start: -1), new(End: 61),
        new(Start: double.NaN), new(End: double.PositiveInfinity), new(Start: 5, End: 5.1), new(MaxFps: 120), new(Speed: (EncodeSpeed)99) })
        Reject(() => EncodingPlanner.Create(source, option));
});
Test("impossible duration is actionable", () => Reject(() => EncodingPlanner.Create(source with { Duration = 3600 }, new(TargetMb: 1))));
Test("mute returns audio budget to video", () => {
    var normal = EncodingPlanner.Create(source, new()); var muted = EncodingPlanner.Create(source, new(Mute: true));
    Check(muted.AudioKbps == 0 && muted.VideoKbps > normal.VideoKbps, "Mute did not change budget");
});
Test("small video never upscaled", () => {
    var p = EncodingPlanner.Create(source with { Width = 320, Height = 180, FrameRate = 24 }, new(MaxFps: 60));
    Check(p.Width == 320 && p.Height == 180 && p.Fps == 24, "Unexpected upscale");
});
Test("portrait and anamorphic display aspect", () => {
    var p = EncodingPlanner.Create(source with { Width = 720, Height = 576, SampleAspect = 16d / 15, Rotation = 90 }, new(Resolution: Resolution.Source));
    Check(p.Width == 576 && p.Height == 768, "Display aspect not respected");
});
Test("time entry is culture independent and rejects rollover", () => {
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
    Check(Display.TryTime("01:02.250", out var s) && s == 62.25, "Minute syntax");
    Check(Display.TryTime("1:02:03.500", out s) && s == 3723.5, "Hour syntax");
    foreach (var text in new[] { "NaN", "Infinity", "-1", "01:60", "1,5", "1:99:00", "" }) Check(!Display.TryTime(text, out _), "Accepted " + text);
});
Test("long clip times retain total hours and round trip at 24 hours", () => {
    foreach (var seconds in new[] { 59.5, 3600, 86399.5, 86400 })
        Check(Display.TryTime(Display.Time(seconds), out var parsed) && parsed == seconds, "Time wrapped: " + seconds);
    Check(Display.Time(86400) == "24:00:00.000", "Full-day trim endpoint wrapped");
    Check(Display.Duration(3661) == "1:01:01" && Display.Duration(86400) == "24:00:00", "Queue duration lost hours");
});
Test("metadata parser skips cover art and reads HDR rotation", () => {
    var parsed = MediaProbe.Parse("""{"streams":[{"codec_type":"video","disposition":{"attached_pic":1}},{"index":2,"codec_type":"video","codec_name":"hevc","width":1920,"height":1080,"avg_frame_rate":"30000/1001","color_transfer":"smpte2084","side_data_list":[{"rotation":-90}]},{"index":3,"codec_type":"audio"}],"format":{"duration":"10.5"}}""", "clip.mp4", 100, DateTime.UtcNow);
    Check(parsed.IsHdr && parsed.Rotation == -90 && parsed.VideoStream == 2 && parsed.AudioStream == 3 && parsed.Duration == 10.5, "Metadata mismatch");
});
Test("malformed and audio-only metadata rejected", () => {
    foreach (var value in new[] { "{", "{}", "{\"streams\":[{\"codec_type\":\"audio\"}]}" }) Reject(() => MediaProbe.Parse(value, "x", 1, DateTime.UtcNow));
});
Test("verification rejects oversize, short and missing-audio results", () => {
    var plan = EncodingPlanner.Create(source, new());
    var good = source with { Width = plan.Width, Height = plan.Height, Bytes = plan.LimitBytes - 1000,
        FrameRate = plan.Fps, PixelFormat = "yuv420p", AudioCodec = "aac" };
    VideoEncoder.ValidateOutput(good, plan);
    Reject(() => VideoEncoder.ValidateOutput(good with { Bytes = plan.LimitBytes + 1 }, plan));
    Reject(() => VideoEncoder.ValidateOutput(good with { Duration = 50 }, plan));
    Reject(() => VideoEncoder.ValidateOutput(good with { HasAudio = false }, plan));
    Reject(() => VideoEncoder.ValidateOutput(good with { Codec = "hevc" }, plan));
});
Test("verification enforces SDR pixels, AAC audio, frame rate and display geometry", () => {
    var plan = EncodingPlanner.Create(source, new());
    var good = source with { Width = plan.Width, Height = plan.Height, Bytes = plan.LimitBytes - 1000,
        FrameRate = plan.Fps, PixelFormat = "yuv420p", AudioCodec = "aac" };
    foreach (var invalid in new[] {
        good with { PixelFormat = "yuv420p10le" }, good with { AudioCodec = "opus" }, good with { IsHdr = true },
        good with { FrameRate = 60 }, good with { SampleAspect = 2 }, good with { Rotation = 90 },
        good with { Width = 2 }, good with { Height = 2 }, good with { Duration = double.NaN },
        good with { FrameRate = double.NaN }, good with { SampleAspect = double.NaN } })
        Reject(() => VideoEncoder.ValidateOutput(invalid, plan));
});
Test("retry accounts for fixed audio cost", () => {
    var bitrate = VideoEncoder.RetryBitrate(1000, 12_000_000, 10_000_000, 128, 60);
    Check(bitrate > 500 && bitrate < 800, "Retry did not reduce enough");
});

var root = Path.Combine(Path.GetTempPath(), "UploadFit-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Console.WriteLine("Evidence: " + root);
await AsyncTest("scratch cleanup retries a released Windows file lock", async () => {
    var scratch = Path.Combine(root, ".uploadfit-transient-lock");
    Directory.CreateDirectory(scratch);
    var locked = Path.Combine(scratch, "pass.log");
    await File.WriteAllTextAsync(locked, "owned scratch");
    var keeper = Path.Combine(root, "keep.txt");
    await File.WriteAllTextAsync(keeper, "unrelated");
    using var handle = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read);
    var cleanup = LocalFiles.RemoveOwnedDirectoryAsync(scratch, root);
    if (OperatingSystem.IsWindows()) Check(!cleanup.IsCompleted, "Cleanup did not wait for the lock");
    await Task.Delay(120);
    handle.Dispose();
    Check(await cleanup && !Directory.Exists(scratch), "Scratch remained after the lock was released");
    Check(await File.ReadAllTextAsync(keeper) == "unrelated", "Cleanup changed an unrelated file");
});
await AsyncTest("scratch cleanup refuses unrelated folders and nested contents", async () => {
    var unrelated = Path.Combine(root, "unrelated");
    Directory.CreateDirectory(unrelated);
    Check(!await LocalFiles.RemoveOwnedDirectoryAsync(unrelated, root) && Directory.Exists(unrelated), "Unowned folder removed");
    var scratch = Path.Combine(root, ".uploadfit-nested");
    var nested = Path.Combine(scratch, "keep");
    Directory.CreateDirectory(nested);
    await File.WriteAllTextAsync(Path.Combine(nested, "keep.txt"), "keep");
    Check(!await LocalFiles.RemoveOwnedDirectoryAsync(scratch, root), "Nested folder removed recursively");
    Check(File.Exists(Path.Combine(nested, "keep.txt")), "Nested content changed");
    Check(!await LocalFiles.RemoveOwnedDirectoryAsync(scratch, nested), "Parent boundary accepted");
});
Test("preferences save atomically and reject invalid data", () => {
    var path = Path.Combine(root, "preferences.json");
    new Preferences(TargetMb: 50, OutputDirectory: root).Save(path);
    Check(Preferences.Load(path).TargetMb == 50, "Settings not persisted");
    File.WriteAllText(path, "{\"TargetMb\":999}");
    Check(Preferences.Load(path).TargetMb == 20, "Invalid settings accepted");
    File.WriteAllText(path, "{broken"); Check(Preferences.Load(path).TargetMb == 20, "Corrupt settings not recovered");
});
Test("promotion never replaces existing outputs", () => {
    var a = Path.Combine(root, "clip-fit.mp4"); File.WriteAllText(a, "keep");
    var staged = Path.Combine(root, "staged.tmp"); File.WriteAllText(staged, "new");
    var destination = LocalFiles.Promote(staged, root, "clip.mkv");
    Check(File.ReadAllText(a) == "keep" && Path.GetFileName(destination) == "clip-fit-2.mp4", "Collision replaced data");
});
await AsyncTest("engine checksum rejects an unrelated archive", async () => {
    var path = Path.Combine(root, "wrong.zip"); await File.WriteAllTextAsync(path, "not the engine");
    await RejectAsync(() => new EngineInstaller(Path.Combine(root, "bad-engine")).InstallArchiveAsync(path, null, default));
    Check(!File.Exists(Path.Combine(root, "bad-engine", "ffmpeg.exe")), "Bad engine installed");
});

if (args.Length == 0)
{
    Console.WriteLine($"PASS {passed} tests. Pass the FFmpeg bin directory for real media integration tests.");
    return 0;
}
var bin = Path.GetFullPath(args[0]);
var paths = new EnginePaths(Path.Combine(bin, "ffmpeg.exe"), Path.Combine(bin, "ffprobe.exe"), Path.Combine(bin, "ffplay.exe"));
Check(await new EngineInstaller(bin).VerifyAsync(), "Integration tests require the exact pinned engine binaries");
var probe = new MediaProbe(paths); var encoder = new VideoEncoder(paths);
async Task Generate(string output, params string[] input)
{
    await MediaProcess.RunAsync(paths.Ffmpeg, new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" }.Concat(input).Append(output), default, TimeSpan.FromMinutes(3));
}
var main = Path.Combine(root, "ranked ' # % \u00e9.mkv");
await Generate(main, "-f", "lavfi", "-i", "testsrc2=size=960x540:rate=60:duration=12", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=12", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "12", "-c:a", "aac", "-metadata", "comment=private fixture metadata");
var media = await probe.ReadAsync(main);
var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(main));
var outputFolder = Path.Combine(root, "output");
ExportResult? exported = null;
await AsyncTest("real two-pass H264+AAC export fits 1 MB and preserves original", async () => {
    exported = await encoder.ExportAsync(media, new(TargetMb: 1, Speed: EncodeSpeed.Fast), outputFolder);
    Check(exported.Bytes <= 1_000_000 && exported.HasAudio && exported.Duration > 11.7, "Export contract failed");
    var currentHash = SHA256.HashData(await File.ReadAllBytesAsync(main));
    Check(originalHash.SequenceEqual(currentHash), "Source changed");
    var metadata = await MediaProcess.RunAsync(paths.Ffprobe, ["-v", "error", "-show_format", "-of", "json", exported.Path], default, TimeSpan.FromSeconds(20));
    Check(!metadata.Contains("private fixture metadata"), "Source metadata leaked");
    Check(!Directory.EnumerateDirectories(outputFolder, ".uploadfit-*").Any(), "Scratch was not cleaned");
});
await AsyncTest("trimmed muted export has accurate duration and no audio", async () => {
    var result = await encoder.ExportAsync(media, new(Start: 1.125, End: 5.375, TargetMb: 1, Mute: true, Speed: EncodeSpeed.Fast), outputFolder);
    Check(!result.HasAudio && Math.Abs(result.Duration - 4.25) < 0.15, "Trim or mute failed");
    Check(result.Path != exported!.Path && File.Exists(exported.Path), "Previous output replaced");
});
await AsyncTest("PNG frame preview is real and handles special characters", async () => {
    var png = await encoder.FrameAsync(media, 3, default);
    Check(png.Length > 1000 && png.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "Invalid frame");
    await File.WriteAllBytesAsync(Path.Combine(root, "preview.png"), png);
});
await AsyncTest("repeated cancellation removes partial output and permits retry", async () => {
    for (var attempt = 0; attempt < 3; attempt++)
    {
        using var cts = new CancellationTokenSource(); cts.CancelAfter(350);
        var before = Directory.GetFiles(outputFolder).Length;
        try { await encoder.ExportAsync(media, new(TargetMb: 1, Speed: EncodeSpeed.Careful), outputFolder, null, cts.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { }
        Check(Directory.GetFiles(outputFolder).Length == before && !Directory.EnumerateDirectories(outputFolder).Any(), "Cancelled job left output, attempt " + attempt);
    }
    var retry = await encoder.ExportAsync(media, new(Start: 0, End: 1, TargetMb: 1, Speed: EncodeSpeed.Fast), outputFolder);
    Check(File.Exists(retry.Path), "Retry failed");
});
await AsyncTest("changed input is rejected before export", async () => {
    await RejectAsync(() => encoder.ExportAsync(media with { Bytes = media.Bytes + 1 }, new(), outputFolder));
});
await AsyncTest("playlist disguised as video is rejected", async () => {
    var path = Path.Combine(root, "playlist.mp4");
    await File.WriteAllTextAsync(path, "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:10,\nhttp://127.0.0.1:1/private.ts\n#EXT-X-ENDLIST\n");
    await RejectAsync(() => probe.ReadAsync(path));
});
var silent = Path.Combine(root, "portrait.webm");
await Generate(silent, "-f", "lavfi", "-i", "testsrc2=size=240x426:rate=24:duration=2", "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-an");
await AsyncTest("portrait WebM with no audio exports without upscaling", async () => {
    var info = await probe.ReadAsync(silent);
    var result = await encoder.ExportAsync(info, new(TargetMb: 1, Speed: EncodeSpeed.Fast), outputFolder);
    Check(!result.HasAudio && result.Height > result.Width && result.Height <= 426, "Portrait output failed");
});
var baseRotation = Path.Combine(root, "rotation-base.mp4"); var rotated = Path.Combine(root, "rotated.mov");
await Generate(baseRotation, "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=24:duration=2", "-vf", "setsar=2/1", "-c:v", "libx264", "-preset", "ultrafast", "-an");
await Generate(rotated, "-display_rotation:v:0", "90", "-i", baseRotation, "-c", "copy");
await AsyncTest("rotation and non-square pixels produce correct display aspect", async () => {
    var info = await probe.ReadAsync(rotated);
    Check(Math.Abs(info.Rotation) == 90, "Rotation fixture did not contain a display matrix");
    var result = await encoder.ExportAsync(info, new(TargetMb: 1, Speed: EncodeSpeed.Fast, Resolution: Resolution.Source), outputFolder);
    Check(Math.Abs(result.Width / (double)result.Height - 240d / 640) < 0.02, "Rotated display aspect failed");
});
var hdr = Path.Combine(root, "hdr.mkv");
await Generate(hdr, "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24:duration=2", "-vf", "format=yuv420p10le,setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc", "-c:v", "libx265", "-preset", "ultrafast", "-x265-params", "log-level=error:colorprim=9:transfer=16:colormatrix=9", "-an");
await AsyncTest("HDR input tone maps to SDR H264", async () => {
    var info = await probe.ReadAsync(hdr); Check(info.IsHdr, "HDR metadata not found");
    var result = await encoder.ExportAsync(info, new(TargetMb: 1, Speed: EncodeSpeed.Fast), outputFolder);
    var verified = await probe.ReadAsync(result.Path); Check(!verified.IsHdr && verified.Codec == "h264", "HDR conversion failed");
    Check((await encoder.FrameAsync(info, 0.5, default)).Length > 100, "HDR preview failed");
});
if (args.Length > 1)
    await AsyncTest("pinned engine archive installs and verifies all binaries", async () => {
        var installer = new EngineInstaller(Path.Combine(root, "installed-engine"));
        await installer.InstallArchiveAsync(args[1], null, default);
        Check(await installer.VerifyAsync(), "Installed engine not verified");
        await File.WriteAllTextAsync(installer.Paths.Ffprobe, "corrupted");
        Check(!await installer.VerifyAsync(), "Corruption not detected");
    });
Console.WriteLine($"PASS {passed} tests, including real FFmpeg media workflows.");
return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("FAIL " + ex);
    return 1;
}
