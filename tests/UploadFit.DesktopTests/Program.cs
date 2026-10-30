using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UploadFit.App;
using UploadFit.Core;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            var task = RunAsync(args);
            _ = task.ContinueWith(_ => dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal), TaskScheduler.Default);
            Dispatcher.Run();
            task.GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex); return 1; }
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    private static T Control<T>(MainWindow window, string name) where T : FrameworkElement =>
        window.FindName(name) as T ?? throw new Exception("Missing compiled control: " + name);

    private static async Task RunAsync(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Pass the pinned FFmpeg bin directory.");
        var bin = Path.GetFullPath(args[0]);
        Check(await new EngineInstaller(bin).VerifyAsync(), "Engine hashes do not match the pin");
        var root = Path.Combine(Path.GetTempPath(), "UploadFit-desktop-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Console.WriteLine("Desktop test evidence: " + root);
        var passed = 0;
        async Task Test(string name, Func<Task> action)
        { await action(); Console.WriteLine("PASS desktop: " + name); passed++; }

        await Test("missing engine shows setup and prevents importing clips", async () => {
            var missing = new MainWindow(new(Path.Combine(root, "missing"), Path.Combine(root, "no-engine"), []));
            try
            {
                await missing.InitializeAsync();
                Check(!missing.EngineReady && !missing.IsWorking, "Missing engine state is wrong");
                Check(Control<Border>(missing, "SetupOverlay").Visibility == Visibility.Visible, "Setup was not shown");
                Check(!Control<Button>(missing, "ExportButton").IsEnabled, "Export is enabled without an engine");
                await missing.AddFilesAsync([Path.Combine(root, "not-a-video.mp4")]);
                Check(missing.Clips.Count == 0, "Imported a clip without an engine");
            }
            finally { missing.Close(); }
        });

        var data = Path.Combine(root, "preferences");
        var output = Path.Combine(root, "output");
        new Preferences(TargetMb: 1, Speed: EncodeSpeed.Fast, OutputDirectory: output).Save(Path.Combine(data, "preferences.json"));
        var window = new MainWindow(new(data, bin, []));
        var input = Path.Combine(root, "a long clip name ' # \u00e9.mp4");
        var other = Path.Combine(root, "second clip.mp4");
        try
        {
            await Test("compiled resources load and saved settings initialize", async () => {
                await window.InitializeAsync();
                Check(window.EngineReady && !window.IsWorking, "Engine did not initialize");
                Check(Control<Border>(window, "SetupOverlay").Visibility == Visibility.Collapsed, "Setup did not close");
                Check(Control<TextBox>(window, "LimitBox").Text == "1" && window.OutputDirectory == output, "Saved settings not loaded");
                var rootGrid = Control<Grid>(window, "RootGrid");
                rootGrid.Measure(new Size(1020, 680));
                rootGrid.Arrange(new Rect(0, 0, 1020, 680));
            });

            await MediaProcess.RunAsync(Path.Combine(bin, "ffmpeg.exe"), ["-hide_banner", "-loglevel", "error", "-nostdin",
                "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30:duration=6", "-f", "lavfi", "-i", "sine=duration=6",
                "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", input], default, TimeSpan.FromMinutes(1));
            File.Copy(input, other);

            await Test("import keeps valid clips and ignores duplicate paths", async () => {
                await window.AddFilesAsync([input, input, Path.Combine(root, "missing.mp4"), other]);
                Check(window.Clips.Count == 2 && !window.IsWorking, "Valid files or duplicate handling failed");
                Check(Control<ListBox>(window, "ClipList").SelectedIndex == 0, "First clip not selected");
            });

            await Test("frame preview decodes into the WPF image", async () => {
                var timer = Stopwatch.StartNew();
                while (Control<Image>(window, "FrameImage").Source is null && timer.Elapsed < TimeSpan.FromSeconds(15))
                    await Task.Delay(50);
                Check(Control<Image>(window, "FrameImage").Source is not null, "Frame preview never completed");
            });

            await Test("invalid visible settings block the whole export queue", async () => {
                Control<TextBox>(window, "LimitBox").Text = "0";
                Check(window.Clips[0].EditError is not null, "Invalid limit was accepted");
                await window.ExportQueueAsync();
                Check(window.Clips.All(c => c.Result is null) && !Directory.Exists(output), "Invalid edits allowed an export");
                Control<TextBox>(window, "LimitBox").Text = "1";
            });

            await Test("applying settings keeps each clip's trim", () => {
                Control<ListBox>(window, "ClipList").SelectedIndex = 1;
                Control<TextBox>(window, "StartBox").Text = "2";
                Control<TextBox>(window, "EndBox").Text = "4";
                Control<ListBox>(window, "ClipList").SelectedIndex = 0;
                Control<TextBox>(window, "StartBox").Text = "0.5";
                Control<TextBox>(window, "EndBox").Text = "2.5";
                Control<CheckBox>(window, "MuteBox").IsChecked = true;
                Control<Button>(window, "ApplyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(window.Clips.All(c => c.Options.Mute && c.EditError is null), "Shared settings not applied");
                Check(window.Clips[0].Options.Start == 0.5 && window.Clips[1].Options.Start == 2 && window.Clips[1].Options.End == 4, "Per-clip trims changed");
                return Task.CompletedTask;
            });

            await Test("queue exports verified files and returns to an idle state", async () => {
                await window.ExportQueueAsync();
                Check(!window.IsWorking && window.Clips.All(c => c.Result is not null && c.Result.Bytes <= 1_000_000), "Queue export failed");
                Check(window.Clips.All(c => !c.Result!.HasAudio && Math.Abs(c.Result.Duration - 2) < 0.15), "Trim or mute was lost");
                Check(!Control<Button>(window, "ExportButton").IsEnabled, "Completed queue remains exportable");
            });

            await Test("editing a finished clip requeues it without replacing its file", async () => {
                var previous = window.Clips[0].Result!.Path;
                var untouched = window.Clips[1].Result!.Path;
                Control<TextBox>(window, "LimitBox").Text = "2";
                Check(window.Clips[0].Result is null && File.Exists(previous), "Requeue lost the original export");
                await window.ExportQueueAsync();
                Check(window.Clips[0].Result is not null && window.Clips[0].Result!.Path != previous && File.Exists(previous), "Retry replaced the finished file");
                Check(window.Clips[1].Result!.Path == untouched, "Another finished clip was re-exported");
            });

            await Test("clearing completed entries preserves sources and outputs", () => {
                var exports = Directory.GetFiles(output, "*.mp4");
                Control<Button>(window, "ClearButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(window.Clips.Count == 0 && exports.All(File.Exists) && File.Exists(input) && File.Exists(other), "Clearing the queue deleted files");
                return Task.CompletedTask;
            });
        }
        finally { window.Close(); }
        Console.WriteLine($"PASS {passed} desktop workflow tests (in-process; no interactive desktop or screenshot checks).");
    }
}
