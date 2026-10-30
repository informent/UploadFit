using System.Globalization;
using System.Text.Json;

namespace UploadFit.Core;

public sealed class MediaProbe(EnginePaths engine)
{
    // Playlists and protocols that fetch remote resources are deliberately excluded.
    public const string Formats = "mov,matroska,webm,avi,mpeg,mpegts,ogg,asf";

    public async Task<MediaInfo> ReadAsync(string path, CancellationToken token = default)
    {
        path = LocalFiles.ValidateInput(path);
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var file = new FileInfo(path);
        var json = await MediaProcess.RunAsync(engine.Ffprobe,
            ["-v", "error", "-protocol_whitelist", "file,pipe", "-format_whitelist", Formats,
             "-show_streams", "-show_format", "-of", "json", path], token, TimeSpan.FromSeconds(30));
        return Parse(json, path, source.Length, file.LastWriteTimeUtc);
    }

    public static MediaInfo Parse(string json, string path, long bytes, DateTime modified)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("streams", out var streams)) throw new MediaException("No video stream was found.");
            var video = streams.EnumerateArray().FirstOrDefault(s => Text(s, "codec_type") == "video" &&
                !(s.TryGetProperty("disposition", out var d) && Number(d, "attached_pic") == 1));
            if (video.ValueKind == JsonValueKind.Undefined) throw new MediaException("This file does not contain a playable video.");
            var audio = streams.EnumerateArray().FirstOrDefault(s => Text(s, "codec_type") == "audio");
            var duration = Number(video, "duration");
            if (duration <= 0 && root.TryGetProperty("format", out var format)) duration = Number(format, "duration");
            var width = (int)Number(video, "width");
            var height = (int)Number(video, "height");
            if (duration < 0.25 || duration > 86_400 || width < 2 || height < 2 || width > 16384 || height > 16384)
                throw new MediaException("Use a video between 0.25 seconds and 24 hours with valid dimensions.");
            var fps = Ratio(Text(video, "avg_frame_rate"));
            if (fps <= 0) fps = Ratio(Text(video, "r_frame_rate"));
            if (fps <= 0 || fps > 1000) fps = 30;
            var aspect = Ratio(Text(video, "sample_aspect_ratio").Replace(':', '/'));
            if (aspect <= 0 || aspect > 100) aspect = 1;
            var rotation = 0;
            if (video.TryGetProperty("side_data_list", out var sides))
                foreach (var side in sides.EnumerateArray())
                    if (side.TryGetProperty("rotation", out _)) rotation = (int)Number(side, "rotation");
            if (rotation == 0 && video.TryGetProperty("tags", out var tags)) rotation = (int)Number(tags, "rotate");
            return new(path, bytes, modified, duration, width, height, fps,
                audio.ValueKind != JsonValueKind.Undefined, Text(video, "color_transfer") is "smpte2084" or "arib-std-b67",
                Text(video, "codec_name"), (int)Number(video, "index"),
                audio.ValueKind != JsonValueKind.Undefined ? (int)Number(audio, "index") : null, aspect, rotation,
                Text(video, "pix_fmt"), Text(audio, "codec_name"));
        }
        catch (JsonException) { throw new MediaException("The video metadata could not be read."); }
        catch (InvalidOperationException) { throw new MediaException("The video metadata has an unsupported format."); }
    }

    private static string Text(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) ? value.ToString() : "";
    private static double Number(JsonElement element, string key) => double.TryParse(Text(element, key), NumberStyles.Float,
        CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : 0;
    private static double Ratio(string value)
    {
        var parts = value.Split('/');
        if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) || b == 0) return 0;
        var result = a / b;
        return double.IsFinite(result) ? result : 0;
    }
}
