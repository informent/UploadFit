using System.Globalization;

namespace UploadFit.Core;

public enum EncodeSpeed { Fast, Balanced, Careful }
public enum Resolution { Auto, P1080, P720, P480, Source }

public sealed record MediaInfo(string Path, long Bytes, DateTime LastWriteUtc, double Duration,
    int Width, int Height, double FrameRate, bool HasAudio, bool IsHdr,
    string Codec, int VideoStream, int? AudioStream, double SampleAspect = 1, int Rotation = 0,
    string PixelFormat = "", string AudioCodec = "")
{
    public double DisplayWidth => (Math.Abs(Rotation) % 180 == 90) ? Height : Width * SampleAspect;
    public double DisplayHeight => (Math.Abs(Rotation) % 180 == 90) ? Width * SampleAspect : Height;
}

public sealed record ExportOptions(double Start = 0, double? End = null, decimal TargetMb = 20,
    bool Mute = false, EncodeSpeed Speed = EncodeSpeed.Balanced,
    Resolution Resolution = Resolution.Auto, int MaxFps = 30);

public sealed record EncodePlan(double Start, double Duration, long LimitBytes, int VideoKbps,
    int AudioKbps, int Width, int Height, double Fps, string Preset, string QualityNote, bool ToneMap);

public sealed record EncodeProgress(string Stage, double Fraction, string Detail);
public sealed record ExportResult(string Path, long Bytes, long LimitBytes, double Duration,
    int Width, int Height, int Attempts, TimeSpan Elapsed, bool HasAudio);
public sealed record EnginePaths(string Ffmpeg, string Ffprobe, string Ffplay);

public sealed class MediaException(string message) : Exception(message);

public static class Display
{
    public static string Bytes(long bytes) => (bytes / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + " MB";
    public static string Time(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        var hours = time.Ticks / TimeSpan.TicksPerHour;
        return hours.ToString("00", CultureInfo.InvariantCulture) + ":" +
            time.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
    }

    public static string Duration(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1
            ? (time.Ticks / TimeSpan.TicksPerHour).ToString(CultureInfo.InvariantCulture) + ":" + time.ToString(@"mm\:ss", CultureInfo.InvariantCulture)
            : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
    public static bool TryTime(string text, out double seconds)
    {
        seconds = 0;
        text = text.Trim();
        if (double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out seconds))
            return double.IsFinite(seconds) && seconds >= 0;
        var parts = text.Split(':');
        if (parts.Length is < 2 or > 3) return false;
        double total = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], i == parts.Length - 1 ? NumberStyles.AllowDecimalPoint : NumberStyles.None,
                    CultureInfo.InvariantCulture, out var part) || !double.IsFinite(part) || part < 0 || (i > 0 && part >= 60)) return false;
            total = total * 60 + part;
        }
        seconds = total;
        return double.IsFinite(seconds) && seconds <= 86_400;
    }
}
