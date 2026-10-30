using System.Text.Json;

namespace UploadFit.Core;

public sealed record Preferences(decimal TargetMb = 20, EncodeSpeed Speed = EncodeSpeed.Balanced,
    Resolution Resolution = Resolution.Auto, int MaxFps = 30, string OutputDirectory = "")
{
    public static Preferences Load(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 32_768) return new();
            var value = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path));
            if (value is null || value.TargetMb is < 1 or > 500 || !Enum.IsDefined(value.Speed) ||
                !Enum.IsDefined(value.Resolution) || value.MaxFps is not (30 or 60)) return new();
            return value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save(string path)
    {
        LocalFiles.RejectLinks(path);
        var parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, ".preferences-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, this);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
