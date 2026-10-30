using System.Diagnostics;
using System.Globalization;

namespace UploadFit.Core;

public sealed class VideoEncoder(EnginePaths engine)
{
    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    public async Task<ExportResult> ExportAsync(MediaInfo source, ExportOptions options, string outputDirectory,
        IProgress<EncodeProgress>? progress = null, CancellationToken token = default)
    {
        var plan = EncodingPlanner.Create(source, options);
        outputDirectory = LocalFiles.ValidateOutput(outputDirectory);
        using var inputLock = LocalFiles.LockSource(source);
        var scratch = Path.Combine(outputDirectory, ".uploadfit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var timer = Stopwatch.StartNew();
        var output = Path.Combine(scratch, "encoded.mp4");
        var videoRate = plan.VideoKbps;
        try
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                token.ThrowIfCancellationRequested();
                var passLog = Path.Combine(scratch, "pass");
                for (var pass = 1; pass <= 2; pass++)
                {
                    var stage = pass == 1 ? "Analyzing frames" : "Encoding video";
                    var detail = attempt == 1 ? "Pass " + pass + " of 2" : "Refining size, attempt " + attempt + " of 3";
                    progress?.Report(new(stage, (pass - 1) * 0.45, detail));
                    var args = InputArguments(source.Path, plan.Start, plan.Duration);
                    args.AddRange(["-map", "0:" + source.VideoStream, "-vf", VideoFilter(plan), "-r", Number(plan.Fps),
                        "-c:v", "libx264", "-preset", plan.Preset, "-b:v", videoRate + "k", "-pix_fmt", "yuv420p",
                        "-threads", Math.Clamp(Environment.ProcessorCount - 1, 1, 8).ToString(CultureInfo.InvariantCulture),
                        "-pass", pass.ToString(CultureInfo.InvariantCulture), "-passlogfile", passLog]);
                    if (plan.ToneMap) args.AddRange(["-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709"]);
                    if (pass == 1)
                        args.AddRange(["-an", "-f", "null", "-"]);
                    else
                    {
                        if (plan.AudioKbps > 0 && source.AudioStream.HasValue)
                            args.AddRange(["-map", "0:" + source.AudioStream, "-c:a", "aac", "-b:a", plan.AudioKbps + "k", "-ac", "2", "-ar", "48000"]);
                        else args.Add("-an");
                        args.AddRange(["-map_metadata", "-1", "-map_chapters", "-1", "-metadata:s:v:0", "rotate=0",
                            "-movflags", "+faststart", "-max_muxing_queue_size", "1024", "-f", "mp4", output]);
                    }
                    var offset = (pass - 1) * 0.45;
                    await MediaProcess.RunAsync(engine.Ffmpeg, args, token, TimeSpan.FromHours(24), line =>
                    {
                        if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                            long.TryParse(line.AsSpan(12), CultureInfo.InvariantCulture, out var microseconds))
                            progress?.Report(new(stage, offset + Math.Clamp(microseconds / 1_000_000d / plan.Duration, 0, 1) * 0.45, detail));
                    }).ConfigureAwait(false);
                }
                progress?.Report(new("Checking output", 0.92, "Checking size, duration and video streams"));
                var bytes = new FileInfo(output).Length;
                if (bytes > plan.LimitBytes)
                {
                    videoRate = RetryBitrate(videoRate, bytes, plan.LimitBytes, plan.AudioKbps, plan.Duration);
                    if (videoRate < 80 || attempt == 3)
                        throw new MediaException("This clip could not fit without losing too much detail. Trim it or choose a larger limit.");
                    continue;
                }
                var result = await new MediaProbe(engine).ReadAsync(output, token).ConfigureAwait(false);
                ValidateOutput(result, plan);
                token.ThrowIfCancellationRequested();
                var final = LocalFiles.Promote(output, outputDirectory, source.Path);
                progress?.Report(new("Ready to share", 1, Display.Bytes(bytes) + " of " + Display.Bytes(plan.LimitBytes)));
                return new(final, bytes, plan.LimitBytes, result.Duration, result.Width, result.Height, attempt,
                    timer.Elapsed, result.HasAudio);
            }
            throw new MediaException("The clip could not be encoded.");
        }
        finally { await LocalFiles.RemoveOwnedDirectoryAsync(scratch, outputDirectory).ConfigureAwait(false); }
    }

    public static int RetryBitrate(int current, long actualBytes, long limit, int audioKbps, double duration)
    {
        var audioBytes = audioKbps * 1000d * duration / 8;
        var ratio = Math.Clamp((limit * 0.90 - audioBytes - 65_536) / Math.Max(1, actualBytes - audioBytes), 0.1, 0.9);
        return (int)Math.Floor(current * ratio);
    }

    public static void ValidateOutput(MediaInfo output, EncodePlan plan)
    {
        if (output.Bytes <= 0 || output.Bytes > plan.LimitBytes || output.Codec != "h264" ||
            !double.IsFinite(output.Duration) || !double.IsFinite(output.FrameRate) ||
            !double.IsFinite(output.SampleAspect) || output.PixelFormat != "yuv420p" || output.IsHdr ||
            Math.Abs(output.FrameRate - plan.Fps) > 0.1 || Math.Abs(output.SampleAspect - 1) > 0.001 ||
            output.Rotation % 360 != 0 || (plan.AudioKbps > 0 && output.AudioCodec != "aac") ||
            Math.Abs(output.Duration - plan.Duration) > Math.Max(0.35, 3 / plan.Fps) ||
            output.HasAudio != (plan.AudioKbps > 0) || output.Width < 2 || output.Height < 2 ||
            Math.Abs(output.Width - plan.Width) > 2 || Math.Abs(output.Height - plan.Height) > 2)
            throw new MediaException("The finished video did not pass verification. No output file was published.");
    }

    public async Task<byte[]> FrameAsync(MediaInfo source, double seconds, CancellationToken token)
    {
        using var inputLock = LocalFiles.LockSource(source);
        var parent = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var scratch = Path.Combine(parent, ".uploadfit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var output = Path.Combine(scratch, "frame.png");
        try
        {
            var args = InputArguments(source.Path, Math.Clamp(seconds, 0, Math.Max(0, source.Duration - 0.1)), null);
            var filter = (source.IsHdr ? ToneMapFilter + "," : "") +
                "scale=960:540:force_original_aspect_ratio=decrease:force_divisible_by=2:reset_sar=1";
            args.AddRange(["-map", "0:" + source.VideoStream, "-frames:v", "1", "-vf", filter, "-an", "-update", "1", output]);
            await MediaProcess.RunAsync(engine.Ffmpeg, args, token, TimeSpan.FromSeconds(30));
            if (!File.Exists(output) || new FileInfo(output).Length > 4_000_000) throw new MediaException("A frame preview is unavailable for this position.");
            return await File.ReadAllBytesAsync(output, token);
        }
        finally { await LocalFiles.RemoveOwnedDirectoryAsync(scratch, parent).ConfigureAwait(false); }
    }

    public Process Play(MediaInfo source, ExportOptions options)
    {
        var plan = EncodingPlanner.Create(source, options);
        LocalFiles.ValidateInput(source.Path);
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-autoexit", "-window_title", "UploadFit preview",
            "-x", "960", "-y", "540", "-volume", "65", "-protocol_whitelist", "file,pipe", "-format_whitelist", MediaProbe.Formats,
            "-ss", Number(plan.Start), "-t", Number(plan.Duration) };
        if (options.Mute) args.Add("-an");
        if (source.IsHdr) args.AddRange(["-vf", ToneMapFilter]);
        args.Add(source.Path);
        var info = MediaProcess.StartInfo(engine.Ffplay, args);
        info.RedirectStandardError = false;
        info.RedirectStandardOutput = false;
        info.RedirectStandardInput = false;
        return Process.Start(info) ?? throw new MediaException("The video preview could not start.");
    }

    private const string ToneMapFilter = "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p";
    private static string VideoFilter(EncodePlan plan) => (plan.ToneMap ? ToneMapFilter + "," : "") +
        $"scale={plan.Width}:{plan.Height}:force_original_aspect_ratio=decrease:force_divisible_by=2:reset_sar=1";

    private static List<string> InputArguments(string input, double start, double? duration)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-xerror", "-nostdin", "-y", "-progress", "pipe:1", "-nostats",
            "-protocol_whitelist", "file,pipe", "-format_whitelist", MediaProbe.Formats, "-ss", Number(start), "-i", input };
        if (duration.HasValue) args.AddRange(["-t", Number(duration.Value)]);
        args.AddRange(["-sn", "-dn"]);
        return args;
    }
}
