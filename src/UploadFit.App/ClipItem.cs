using System.ComponentModel;
using UploadFit.Core;

namespace UploadFit.App;

public sealed class ClipItem(MediaInfo media, ExportOptions options) : INotifyPropertyChanged
{
    public MediaInfo Media { get; } = media;
    public ExportOptions Options { get; set; } = options;
    public ExportResult? Result { get; set; }
    public string StartText { get; set; } = Display.Time(options.Start);
    public string EndText { get; set; } = Display.Time(options.End ?? media.Duration);
    public string LimitText { get; set; } = options.TargetMb.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string? EditError { get; set; }
    public string Name => System.IO.Path.GetFileName(Media.Path);
    public string Summary => Display.Bytes(Media.Bytes) + "  /  " + Display.Duration(Media.Duration);
    private string status = "Queued";
    public string Status { get => status; set { status = value; Changed(nameof(Status)); Changed(nameof(StatusColor)); } }
    public string StatusColor => Status.StartsWith("Ready", StringComparison.Ordinal) ? "#B3DBAB" :
        Status is "Failed" or "Needs attention" ? "#F4AD92" : "#B9C1B7";
    public string? Error { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
