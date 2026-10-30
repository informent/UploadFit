namespace UploadFit.Core;

public static class EncodingPlanner
{
    public static EncodePlan Create(MediaInfo source, ExportOptions options)
    {
        if (options.TargetMb is < 1 or > 500) throw new MediaException("Choose a file limit between 1 and 500 MB.");
        if (!Enum.IsDefined(options.Speed) || !Enum.IsDefined(options.Resolution) || options.MaxFps is not (30 or 60))
            throw new MediaException("Choose a supported quality and frame rate.");
        var end = options.End ?? source.Duration;
        if (!double.IsFinite(options.Start) || !double.IsFinite(end) || options.Start < 0 ||
            end > source.Duration + 0.05 || end - options.Start < 0.25)
            throw new MediaException("The end must be after the start, within the clip, with at least 0.25 seconds selected.");
        if (source.Width < 2 || source.Height < 2 || !double.IsFinite(source.DisplayWidth) ||
            !double.IsFinite(source.DisplayHeight) || source.DisplayWidth <= 0 || source.DisplayHeight <= 0 ||
            !double.IsFinite(source.FrameRate) || source.FrameRate <= 0)
            throw new MediaException("This video has invalid dimensions or timing.");
        var duration = end - options.Start;
        var limit = (long)(options.TargetMb * 1_000_000);
        // Reserve muxing overhead and AAC priming. Never truncate a video to enforce its size.
        var availableKbps = (int)Math.Floor((limit * 0.94 - 65_536) * 8 / duration / 1000);
        var audio = source.HasAudio && !options.Mute ? (availableKbps >= 700 ? 128 : availableKbps >= 300 ? 96 : 48) : 0;
        var video = Math.Min(50_000, availableKbps - audio);
        if (video < 80) throw new MediaException("This selection is too long for that limit. Trim it, raise the limit, or remove audio.");
        var fps = Math.Min(options.MaxFps, source.FrameRate);
        var longSide = options.Resolution switch
        {
            Resolution.P1080 => 1920, Resolution.P720 => 1280, Resolution.P480 => 854,
            Resolution.Source => Math.Max(source.DisplayWidth, source.DisplayHeight), _ => 1920
        };
        // Match resolution to the available bits per pixel for an understandable default.
        if (options.Resolution == Resolution.Auto)
        {
            foreach (var candidate in new[] { 1920, 1280, 960, 854, 640, 426 })
            {
                longSide = candidate;
                var ratio = Math.Min(1, candidate / Math.Max(source.DisplayWidth, source.DisplayHeight));
                if (video * 1000 / (source.DisplayWidth * source.DisplayHeight * ratio * ratio * fps) >= 0.07) break;
            }
        }
        var scale = Math.Min(1, longSide / Math.Max(source.DisplayWidth, source.DisplayHeight));
        var width = Math.Max(2, (int)(source.DisplayWidth * scale) / 2 * 2);
        var height = Math.Max(2, (int)(source.DisplayHeight * scale) / 2 * 2);
        if (width > 7680 || height > 7680) throw new MediaException("Choose a smaller resolution for this video.");
        var density = video * 1000 / (width * (double)height * fps);
        var note = density < 0.045 ? "Tight budget. A shorter selection will preserve more detail." :
            density < 0.08 ? "A practical fit. Fast movement may lose some detail." : "Room for detail at this size.";
        return new(options.Start, duration, limit, video, audio, width, height, fps,
            options.Speed switch { EncodeSpeed.Fast => "veryfast", EncodeSpeed.Careful => "slow", _ => "medium" }, note, source.IsHdr);
    }
}
