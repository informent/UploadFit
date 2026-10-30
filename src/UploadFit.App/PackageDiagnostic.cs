using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using UploadFit.Core;

namespace UploadFit.App;

internal static class PackageDiagnostic
{
    public static void Write(Application app, string reportPath)
    {
        foreach (var key in new[] { "Paper", "Ink", "Muted", "Accent", "PrimaryButton" })
            if (app.TryFindResource(key) is null) throw new InvalidOperationException("Missing application resource: " + key);
        var assembly = typeof(App).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
        var report = new
        {
            product = "UploadFit", version, runtime = RuntimeInformation.FrameworkDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            engineVersion = EngineInstaller.Version, resourcesLoaded = true,
            coreLoaded = typeof(VideoEncoder).Assembly.GetName().Name == "UploadFit.Core",
            interactiveDesktopTested = false
        };
        reportPath = Path.GetFullPath(reportPath);
        LocalFiles.ValidateOutput(Path.GetDirectoryName(reportPath)!);
        using var stream = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, report, new JsonSerializerOptions { WriteIndented = true });
    }
}
