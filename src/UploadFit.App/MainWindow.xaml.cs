using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using UploadFit.Core;

namespace UploadFit.App;

public partial class MainWindow : Window
{
    private readonly LaunchOptions launch;
    private readonly EngineInstaller installer;
    private readonly ObservableCollection<ClipItem> clips = [];
    private readonly string preferencesPath;
    private Preferences preferences;
    private CancellationTokenSource? workCancel;
    private CancellationTokenSource? frameCancel;
    private Process? player;
    private bool loading = true, busy, ready, closeRequested;
    private string outputDirectory;
    public bool IsWorking => busy;
    public bool EngineReady => ready;
    public IReadOnlyList<ClipItem> Clips => clips;
    public string OutputDirectory => outputDirectory;

    public MainWindow(LaunchOptions options)
    {
        launch = options;
        preferencesPath = Path.Combine(options.DataDirectory, "preferences.json");
        preferences = Preferences.Load(preferencesPath);
        installer = new(options.EngineDirectory ?? Path.Combine(options.DataDirectory, "engine", EngineInstaller.Version));
        outputDirectory = string.IsNullOrWhiteSpace(preferences.OutputDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "UploadFit") : preferences.OutputDirectory;
        InitializeComponent();
        ClipList.ItemsSource = clips;
        LimitBox.Text = preferences.TargetMb.ToString(CultureInfo.InvariantCulture);
        SpeedBox.SelectedIndex = (int)preferences.Speed;
        ResolutionBox.SelectedIndex = (int)preferences.Resolution;
        FpsBox.SelectedIndex = preferences.MaxFps == 60 ? 1 : 0;
        OutputLabel.Text = "Save to: " + outputDirectory;
        OutputLabel.ToolTip = outputDirectory;
        loading = false;
        Loaded += async (_, _) => await InitializeAsync();
    }

    public async Task InitializeAsync()
    {
        SetBusy(true);
        try
        {
            ready = await installer.VerifyAsync();
            EngineButton.Content = ready ? "Engine ready / 9.0.2" : "Engine setup";
            SetStatus(ready ? "Add clips to begin. Your originals stay intact." : "Set up the video engine to add clips.");
            SetupOverlay.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex) { Handle(ex); }
        finally { SetBusy(false); }
        if (ready && launch.Files.Count > 0) await AddFilesAsync(launch.Files);
    }

    private ClipItem? Selected => ClipList.SelectedItem as ClipItem;
    private VideoEncoder Encoder => new(installer.Paths);

    private void SetBusy(bool value)
    {
        busy = value;
        AddButton.IsEnabled = !value;
        ClearButton.IsEnabled = !value;
        ClipList.IsEnabled = !value;
        SettingsPanel.IsEnabled = !value;
        TrimPanel.IsEnabled = !value && Selected is not null;
        Scrubber.IsEnabled = !value && Selected is not null;
        RemoveButton.IsEnabled = !value && Selected is not null;
        PlayButton.IsEnabled = !value && ready && Selected is not null;
        FolderButton.IsEnabled = !value;
        EngineButton.IsEnabled = !value;
        ApplyButton.IsEnabled = !value && clips.Count > 0;
        CancelButton.Visibility = value && workCancel is not null ? Visibility.Visible : Visibility.Collapsed;
        UpdateQueue();
        if (!value && closeRequested) Dispatcher.BeginInvoke(Close);
    }

    private void UpdateQueue()
    {
        QueueCount.Text = "QUEUE / " + clips.Count;
        var waiting = clips.Count(c => c.Result is null);
        ExportButton.Content = waiting == 0 ? "Export queue" : "Export " + waiting + (waiting == 1 ? " clip" : " clips");
        ExportButton.IsEnabled = !busy && ready && waiting > 0;
        ApplyButton.IsEnabled = !busy && clips.Count > 0;
    }

    private void SetStatus(string message, bool error = false)
    {
        StatusLabel.Text = message;
        StatusLabel.ToolTip = message;
        StatusLabel.Foreground = (Brush)FindResource(error ? "Accent" : "Ink");
    }

    private void Handle(Exception exception)
    {
        var message = exception switch
        {
            OperationCanceledException => "Cancelled. Finished files are kept; unfinished work is removed.",
            UnauthorizedAccessException => "This folder cannot be written. Choose a folder in your Windows account.",
            IOException => "A file is missing, locked, or the disk is full. Check the file and output folder.",
            System.Net.Http.HttpRequestException => "The engine download could not connect. Try again or import the pinned engine ZIP.",
            MediaException => exception.Message,
            _ => "This operation could not finish. " + exception.Message
        };
        SetStatus(message, exception is not OperationCanceledException);
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (!ready) { SetupOverlay.Visibility = Visibility.Visible; return; }
        var dialog = new OpenFileDialog { Title = "Add video clips", Multiselect = true,
            Filter = "Videos|*.mp4;*.mov;*.mkv;*.webm;*.avi;*.m4v;*.wmv;*.mpg;*.mpeg;*.ts;*.mts;*.m2ts;*.ogv|All files|*.*" };
        if (dialog.ShowDialog(this) == true) await AddFilesAsync(dialog.FileNames);
    }

    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        if (busy) return;
        if (!ready) { SetupOverlay.Visibility = Visibility.Visible; return; }
        workCancel = new(); SetBusy(true);
        var added = 0; var errors = new List<string>();
        try
        {
            var list = paths.Take(101).ToArray();
            foreach (var path in list)
            {
                workCancel.Token.ThrowIfCancellationRequested();
                if (clips.Count >= 100) { errors.Add("The queue holds up to 100 clips."); break; }
                if (clips.Any(c => c.Media.Path.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))) continue;
                SetStatus("Reading " + Path.GetFileName(path) + "...");
                try
                {
                    var media = await new MediaProbe(installer.Paths).ReadAsync(path, workCancel.Token);
                    var opts = new ExportOptions(TargetMb: preferences.TargetMb, Speed: preferences.Speed,
                        Resolution: preferences.Resolution, MaxFps: preferences.MaxFps);
                    var clip = new ClipItem(media, opts);
                    try { EncodingPlanner.Create(media, opts); }
                    catch (MediaException ex) { clip.EditError = ex.Message; clip.Status = "Needs attention"; }
                    clips.Add(clip); added++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add(Path.GetFileName(path) + ": " + ex.Message); }
            }
            SetStatus(added + (added == 1 ? " clip added." : " clips added.") +
                (errors.Count > 0 ? " " + string.Join(" ", errors.Take(3)) : " Choose a size and export when ready."), errors.Count > 0);
        }
        catch (Exception ex) { Handle(ex); }
        finally
        {
            workCancel.Dispose(); workCancel = null; SetBusy(false);
            if (Selected is null && clips.Count > 0) ClipList.SelectedIndex = 0;
        }
    }

    private void Clip_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading) return;
        LoadSelected();
    }

    private void LoadSelected()
    {
        StopPlayer(); frameCancel?.Cancel();
        var clip = Selected;
        loading = true;
        FrameImage.Source = null;
        EmptyPreview.Visibility = Visibility.Visible;
        FrameBusy.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Collapsed;
        if (clip is null)
        {
            ClipTitle.Text = "Add your first clip"; ClipDetails.Text = "MP4, MOV, MKV, WebM and more";
            PreviewHint.Text = "Drop videos here"; Scrubber.Value = 0; Scrubber.Maximum = 1;
            StartBox.Text = EndBox.Text = "00:00:00.000";
        }
        else
        {
            ClipTitle.Text = clip.Name; ClipTitle.ToolTip = clip.Name;
            ClipDetails.Text = $"{clip.Media.Width} x {clip.Media.Height} / {clip.Media.FrameRate:0.##} fps / {Display.Bytes(clip.Media.Bytes)}" + (clip.Media.IsHdr ? " / HDR to SDR" : "");
            PreviewHint.Text = "Preparing preview";
            StartBox.Text = clip.StartText; EndBox.Text = clip.EndText; LimitBox.Text = clip.LimitText;
            SpeedBox.SelectedIndex = (int)clip.Options.Speed;
            ResolutionBox.SelectedIndex = (int)clip.Options.Resolution;
            FpsBox.SelectedIndex = clip.Options.MaxFps == 60 ? 1 : 0;
            MuteBox.IsChecked = clip.Options.Mute;
            Scrubber.Maximum = clip.Media.Duration;
            Scrubber.Value = clip.Options.Start;
            PositionLabel.Text = Display.Time(Scrubber.Value);
            if (clip.Error is not null) SetStatus(clip.Error, true);
        }
        loading = false;
        SetBusy(busy);
        RefreshPlan(); ShowResult();
        if (clip is not null) QueueFrame();
    }

    private ExportOptions ReadOptions(MediaInfo media)
    {
        if (!decimal.TryParse(LimitBox.Text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var limit))
            throw new MediaException("Enter a file size such as 20 or 9.5 MB.");
        if (!Display.TryTime(StartBox.Text, out var start) || !Display.TryTime(EndBox.Text, out var end))
            throw new MediaException("Enter trim times as seconds, mm:ss, or hh:mm:ss. Milliseconds use a decimal point.");
        var options = new ExportOptions(start, end, limit, MuteBox.IsChecked == true,
            (EncodeSpeed)SpeedBox.SelectedIndex, (Resolution)ResolutionBox.SelectedIndex, FpsBox.SelectedIndex == 1 ? 60 : 30);
        EncodingPlanner.Create(media, options);
        return options;
    }

    private void Settings_Changed(object sender, RoutedEventArgs e)
    {
        if (loading || !IsInitialized || busy) return;
        var clip = Selected;
        if (clip is not null)
        {
            clip.StartText = StartBox.Text; clip.EndText = EndBox.Text; clip.LimitText = LimitBox.Text;
            clip.Result = null; clip.Error = null;
            try { clip.Options = ReadOptions(clip.Media); clip.EditError = null; clip.Status = "Queued"; }
            catch (MediaException ex) { clip.EditError = ex.Message; clip.Status = "Needs attention"; }
        }
        SavePreferences();
        RefreshPlan(); ShowResult(); UpdateQueue();
    }

    private void SavePreferences()
    {
        if (!decimal.TryParse(LimitBox.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var limit) || limit is < 1 or > 500) return;
        preferences = new(limit, (EncodeSpeed)Math.Max(0, SpeedBox.SelectedIndex),
            (Resolution)Math.Max(0, ResolutionBox.SelectedIndex), FpsBox.SelectedIndex == 1 ? 60 : 30, outputDirectory);
        try { preferences.Save(preferencesPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MediaException)
        { SetStatus("Settings could not be saved. This session will still work.", true); }
    }

    private void RefreshPlan()
    {
        var clip = Selected;
        if (clip is null) { PlanLabel.Text = "MP4 / H.264 video + AAC audio"; PlanNote.Text = "Finished files are checked against your size limit."; return; }
        try
        {
            var plan = EncodingPlanner.Create(clip.Media, ReadOptions(clip.Media));
            PlanLabel.Text = $"{plan.Width} x {plan.Height} / {plan.Fps:0.##} fps / MP4";
            PlanNote.Text = plan.QualityNote + " " + (plan.AudioKbps == 0 ? "No audio." : "AAC audio included.");
            PlanNote.Foreground = (Brush)FindResource("Muted");
            SelectionLabel.Text = Display.Time(plan.Duration) + " selected\nPlayback opens in a separate player";
            PlayButton.IsEnabled = !busy && ready;
        }
        catch (MediaException ex)
        {
            PlanLabel.Text = "Check these settings"; PlanNote.Text = ex.Message;
            PlanNote.Foreground = (Brush)FindResource("Accent"); PlayButton.IsEnabled = false;
        }
    }

    private void ShowResult()
    {
        var result = Selected?.Result;
        ResultPanel.Visibility = result is null ? Visibility.Collapsed : Visibility.Visible;
        if (result is null) return;
        ResultTitle.Text = "Ready / " + Display.Bytes(result.Bytes) + " of " + Display.Bytes(result.LimitBytes);
        ResultDetail.Text = result.Width + " x " + result.Height + " / " + Display.Time(result.Duration) +
            "\n" + Path.GetFileName(result.Path);
    }

    private void Preset_Click(object sender, RoutedEventArgs e) => LimitBox.Text = (string)((Button)sender).Tag;
    private void SetStart_Click(object sender, RoutedEventArgs e) => StartBox.Text = Display.Time(Scrubber.Value);
    private void SetEnd_Click(object sender, RoutedEventArgs e) => EndBox.Text = Display.Time(Scrubber.Value);

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null || busy) return;
        try
        {
            var settings = ReadOptions(Selected.Media);
            foreach (var clip in clips.Where(c => c.Result is null))
            {
                clip.Options = settings with { Start = clip.Options.Start, End = clip.Options.End };
                clip.LimitText = settings.TargetMb.ToString(CultureInfo.InvariantCulture);
                try
                {
                    if (!Display.TryTime(clip.StartText, out var start) || !Display.TryTime(clip.EndText, out var end))
                        throw new MediaException("Correct the trim times for this clip.");
                    clip.Options = clip.Options with { Start = start, End = end };
                    EncodingPlanner.Create(clip.Media, clip.Options);
                    clip.EditError = null; clip.Status = "Queued";
                }
                catch (MediaException ex) { clip.EditError = ex.Message; clip.Status = "Needs attention"; }
            }
            SetStatus("Export settings applied to queued clips. Individual trims were kept.");
            RefreshPlan(); UpdateQueue();
        }
        catch (Exception ex) { Handle(ex); }
    }

    private void Scrubber_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (loading || !IsInitialized) return;
        PositionLabel.Text = Display.Time(Scrubber.Value);
        if (!busy) QueueFrame();
    }

    private async void QueueFrame()
    {
        frameCancel?.Cancel();
        var clip = Selected; var position = Scrubber.Value;
        if (clip is null || !ready) return;
        var cts = new CancellationTokenSource(); frameCancel = cts;
        try
        {
            await Task.Delay(220, cts.Token);
            FrameBusy.Visibility = Visibility.Visible;
            var bytes = await Encoder.FrameAsync(clip.Media, position, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (Selected != clip) return;
            var bitmap = new BitmapImage();
            using (var stream = new MemoryStream(bytes))
            {
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            }
            FrameImage.Source = bitmap; EmptyPreview.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (Selected == clip) { PreviewHint.Text = "Frame unavailable"; SetStatus(ex.Message, true); }
        }
        finally
        {
            if (ReferenceEquals(frameCancel, cts)) { frameCancel = null; FrameBusy.Visibility = Visibility.Collapsed; }
            cts.Dispose();
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e) => await ExportQueueAsync();

    public async Task ExportQueueAsync()
    {
        if (busy || !ready) return;
        var pending = clips.Where(c => c.Result is null).ToArray();
        if (pending.Length == 0) return;
        foreach (var clip in pending)
        {
            if (clip.EditError is not null) { ClipList.SelectedItem = clip; SetStatus(clip.Name + ": " + clip.EditError, true); return; }
        }
        try { LocalFiles.ValidateOutput(outputDirectory); }
        catch (Exception ex) { Handle(ex); return; }
        frameCancel?.Cancel(); StopPlayer();
        workCancel = new(); SetBusy(true);
        var done = 0; var failed = 0;
        var timer = Stopwatch.StartNew();
        try
        {
            // Recheck the pinned binaries immediately before invoking them.
            if (!await installer.VerifyAsync(workCancel.Token)) { ready = false; throw new MediaException("The video engine needs repair. Open Engine setup."); }
            for (var i = 0; i < pending.Length; i++)
            {
                workCancel.Token.ThrowIfCancellationRequested();
                var clip = pending[i];
                clip.Status = "Working"; clip.Error = null;
                var index = i;
                var progress = new Progress<EncodeProgress>(p =>
                {
                    WorkProgress.Value = (index + p.Fraction) / pending.Length;
                    clip.Status = p.Stage;
                    SetStatus($"{index + 1}/{pending.Length} / {p.Stage} / {p.Detail} / {timer.Elapsed:mm\\:ss}");
                });
                try
                {
                    clip.Result = await Encoder.ExportAsync(clip.Media, clip.Options, outputDirectory, progress, workCancel.Token);
                    clip.Status = "Ready / " + Display.Bytes(clip.Result.Bytes); done++;
                    ShowResult();
                }
                catch (OperationCanceledException) { clip.Status = "Cancelled"; throw; }
                catch (Exception ex) { clip.Status = "Failed"; clip.Error = ex.Message; failed++; }
            }
            WorkProgress.Value = 1;
            SetStatus(done + " ready" + (failed > 0 ? ", " + failed + " failed. Select a failed clip for details and retry." : ". Select a clip to show or copy the finished file."), failed > 0);
        }
        catch (Exception ex) { Handle(ex); }
        finally { workCancel.Dispose(); workCancel = null; SetBusy(false); ShowResult(); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { SetStatus("Cancelling and cleaning up..."); workCancel?.Cancel(); }
    private void Remove_Click(object sender, RoutedEventArgs e) { if (!busy && Selected is { } clip) { clips.Remove(clip); UpdateQueue(); } }
    private void Clear_Click(object sender, RoutedEventArgs e) { if (!busy) foreach (var clip in clips.Where(c => c.Result is not null).ToArray()) clips.Remove(clip); UpdateQueue(); }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Where should finished clips be saved?", InitialDirectory = Directory.Exists(outputDirectory) ? outputDirectory : "" };
        if (dialog.ShowDialog(this) == true) SetOutputDirectory(dialog.FolderName);
    }

    public void SetOutputDirectory(string directory)
    {
        if (busy) return;
        outputDirectory = Path.GetFullPath(directory);
        OutputLabel.Text = "Save to: " + outputDirectory; OutputLabel.ToolTip = outputDirectory;
        SavePreferences();
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } clip) return;
        try { StopPlayer(); player = Encoder.Play(clip.Media, ReadOptions(clip.Media)); }
        catch (Exception ex) { Handle(ex); }
    }

    private void StopPlayer()
    {
        if (player is null) return;
        MediaProcess.Kill(player); player.Dispose(); player = null;
    }

    private void ShowFile_Click(object sender, RoutedEventArgs e)
    {
        if (Selected?.Result is not { } result) return;
        try
        {
            if (!File.Exists(result.Path)) throw new MediaException("The finished file was moved or deleted.");
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add("/select,"); start.ArgumentList.Add(result.Path);
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) { Handle(ex); }
    }

    private void CopyFile_Click(object sender, RoutedEventArgs e)
    {
        if (Selected?.Result is not { } result) return;
        try
        {
            if (!File.Exists(result.Path)) throw new MediaException("The finished file was moved or deleted.");
            var files = new StringCollection { result.Path };
            Clipboard.SetFileDropList(files);
            SetStatus("File copied. Paste it into Discord or another app that accepts files.");
        }
        catch (Exception ex) { Handle(ex); }
    }

    private void Engine_Click(object sender, RoutedEventArgs e)
    {
        SetupStatus.Text = ready ? "Engine verified. You can reinstall it or use a downloaded ZIP." : "No videos are sent during setup or compression.";
        SetupOverlay.Visibility = Visibility.Visible;
    }

    private async void Download_Click(object sender, RoutedEventArgs e) => await SetupEngineAsync(null);
    private async void ImportEngine_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose ffmpeg-9.0.2-essentials_build.zip", Filter = "Engine ZIP|*.zip" };
        if (dialog.ShowDialog(this) == true) await SetupEngineAsync(dialog.FileName);
    }

    public async Task SetupEngineAsync(string? archive)
    {
        if (busy) return;
        workCancel = new(); SetBusy(true);
        DownloadButton.IsEnabled = ImportEngineButton.IsEnabled = false;
        CloseSetupButton.Content = "Cancel setup";
        try
        {
            var progress = new Progress<EncodeProgress>(p => { SetupProgress.Value = p.Fraction; SetupStatus.Text = p.Stage + ". " + p.Detail; });
            if (archive is null) await installer.DownloadAsync(progress, workCancel.Token);
            else await installer.InstallArchiveAsync(archive, progress, workCancel.Token);
            ready = await installer.VerifyAsync(workCancel.Token);
            if (!ready) throw new MediaException("The installed engine did not pass verification. Try setup again.");
            SetupOverlay.Visibility = Visibility.Collapsed; EngineButton.Content = "Engine ready / 9.0.2";
            SetStatus("Engine ready. Add clips to begin.");
        }
        catch (Exception ex) { Handle(ex); SetupStatus.Text = StatusLabel.Text; }
        finally
        {
            workCancel.Dispose(); workCancel = null;
            DownloadButton.IsEnabled = ImportEngineButton.IsEnabled = true; CloseSetupButton.Content = "Later"; SetBusy(false);
        }
    }

    private void CloseSetup_Click(object sender, RoutedEventArgs e)
    {
        if (busy) { workCancel?.Cancel(); return; }
        SetupOverlay.Visibility = Visibility.Collapsed;
    }

    private void EngineSource_Click(object sender, RoutedEventArgs e) => OpenLink("https://www.gyan.dev/ffmpeg/builds/");
    private static void OpenLink(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "1. Add videos with the button, drag and drop, or Ctrl+O.\n2. Choose a file size limit. MB means 1,000,000 bytes.\n3. Scrub to a frame and set the trim start and end.\n4. Export your queue, then show or copy the finished files.\n\n" +
            "Auto resolution balances detail with the size budget. Careful encoding takes longer. Output is MP4 with H.264 video and optional AAC audio. HDR clips are converted to SDR.\n\n" +
            "Space: play selection (when not typing). Escape: cancel active work. Delete: remove the selected queued item.\n\n" +
            "Your original files stay intact. Exports get a new name; existing files are never replaced. Clips and queue history are not stored between sessions. Only export preferences and the output folder are saved.\n\n" +
            "The first engine download connects to GitHub. Video processing works locally afterward. UploadFit is an independent project by informent and is not affiliated with Discord.\n\n" +
            "Source, issues and releases: github.com/informent/UploadFit", "Using UploadFit", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!busy && e.Data.GetData(DataFormats.FileDrop) is string[] files) await AddFilesAsync(files);
    }
    private void Window_DragOver(object sender, DragEventArgs e)
    { e.Effects = !busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { workCancel?.Cancel(); e.Handled = busy; return; }
        if (busy) return;
        if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control) { Add_Click(this, new()); e.Handled = true; }
        if (Keyboard.FocusedElement is TextBox) return;
        if (e.Key == Key.Delete && Selected is not null) { Remove_Click(this, new()); e.Handled = true; }
        if (e.Key == Key.Space && PlayButton.IsEnabled) { Play_Click(this, new()); e.Handled = true; }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        frameCancel?.Cancel(); StopPlayer();
        if (busy) { closeRequested = true; e.Cancel = true; workCancel?.Cancel(); }
    }
}
