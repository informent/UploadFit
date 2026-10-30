using System.Windows;

namespace UploadFit.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 0 && e.Args[0] == "--check-package")
        {
            try
            {
                if (e.Args.Length != 2) throw new ArgumentException("Supply a new package diagnostic report path.");
                PackageDiagnostic.Write(this, e.Args[1]);
                Shutdown(0);
            }
            catch (Exception) { Shutdown(1); }
            return;
        }
        var data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var options = new LaunchOptions(System.IO.Path.Combine(data, "UploadFit"), null, []);
        for (var i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--data-dir" && i + 1 < e.Args.Length) options = options with { DataDirectory = System.IO.Path.GetFullPath(e.Args[++i]) };
            else if (e.Args[i] == "--engine-dir" && i + 1 < e.Args.Length) options = options with { EngineDirectory = System.IO.Path.GetFullPath(e.Args[++i]) };
            else if (!e.Args[i].StartsWith("--", StringComparison.Ordinal)) options.Files.Add(e.Args[i]);
        }
        var window = new MainWindow(options);
        MainWindow = window;
        window.Show();
    }
}

public sealed record LaunchOptions(string DataDirectory, string? EngineDirectory, List<string> Files);
