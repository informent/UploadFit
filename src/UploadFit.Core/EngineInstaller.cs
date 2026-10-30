using System.IO.Compression;
using System.Security.Cryptography;

namespace UploadFit.Core;

public sealed class EngineInstaller(string directory)
{
    public const string Version = "9.0.2";
    public const string DownloadUrl = "https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip";
    public const string ArchiveHash = "60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba";
    public static readonly IReadOnlyDictionary<string, string> BinaryHashes = new Dictionary<string, string>
    {
        ["ffmpeg.exe"] = "3256173f3f8bffd7df12227c68adf68025edb1832273a9530688a7bb1ed8edec",
        ["ffprobe.exe"] = "f0d36ecbbdd3bcfac3efa078c96c7271c2e68b3810595552ac3b7f17e9a65c52",
        ["ffplay.exe"] = "befbbd00a25965365f7fb3eed6ad5aa5f805eb70fabc567883a006de3b26c06f"
    };
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public EnginePaths Paths => new(Path.Combine(DirectoryPath, "ffmpeg.exe"), Path.Combine(DirectoryPath, "ffprobe.exe"), Path.Combine(DirectoryPath, "ffplay.exe"));

    public async Task<bool> VerifyAsync(CancellationToken token = default)
    {
        try
        {
            LocalFiles.RejectLinks(DirectoryPath);
            foreach (var (file, hash) in BinaryHashes)
            {
                var path = Path.Combine(DirectoryPath, file);
                if (!File.Exists(path)) return false;
                LocalFiles.RejectLinks(path);
                if (!await MatchesAsync(path, hash, token).ConfigureAwait(false)) return false;
            }
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (MediaException) { return false; }
    }

    public async Task DownloadAsync(IProgress<EncodeProgress>? progress, CancellationToken token)
    {
        LocalFiles.ValidateOutput(DirectoryPath);
        var scratch = Path.Combine(DirectoryPath, ".uploadfit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var zip = Path.Combine(scratch, "engine.zip");
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("UploadFit/0.1.0");
            using var response = await client.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            var size = response.Content.Headers.ContentLength;
            if (size > 200_000_000) throw new MediaException("The engine download was unexpectedly large.");
            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var target = new FileStream(zip, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, token)) > 0)
                {
                    total += read;
                    if (total > 200_000_000) throw new MediaException("The engine download was unexpectedly large.");
                    await target.WriteAsync(buffer.AsMemory(0, read), token);
                    progress?.Report(new("Downloading video engine", size > 0 ? total / (double)size * 0.85 : 0,
                        Display.Bytes(total) + " downloaded"));
                }
            }
            await InstallArchiveAsync(zip, progress, token);
        }
        finally { await LocalFiles.RemoveOwnedDirectoryAsync(scratch, DirectoryPath).ConfigureAwait(false); }
    }

    public async Task InstallArchiveAsync(string archivePath, IProgress<EncodeProgress>? progress, CancellationToken token)
    {
        LocalFiles.ValidateOutput(DirectoryPath);
        using var installLock = new FileStream(Path.Combine(DirectoryPath, "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        progress?.Report(new("Checking download", 0.87, "Verifying the pinned SHA-256 checksum"));
        if (!await MatchesAsync(archivePath, ArchiveHash, token)) throw new MediaException("The download checksum does not match. Nothing was installed. Try downloading again.");
        var scratch = Path.Combine(DirectoryPath, ".uploadfit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            foreach (var (name, hash) in BinaryHashes)
            {
                var entry = zip.GetEntry("ffmpeg-" + Version + "-essentials_build/bin/" + name);
                if (entry is null || entry.Length is < 100_000 or > 250_000_000) throw new MediaException("The engine archive is incomplete.");
                var staged = Path.Combine(scratch, name);
                await using (var input = entry.Open())
                await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await input.CopyToAsync(output, token);
                if (!await MatchesAsync(staged, hash, token)) throw new MediaException("A video engine component failed verification.");
            }
            token.ThrowIfCancellationRequested();
            progress?.Report(new("Installing video engine", 0.96, "Keeping the engine on this computer"));
            foreach (var name in BinaryHashes.Keys)
            {
                var destination = Path.Combine(DirectoryPath, name);
                LocalFiles.RejectLinks(destination);
                File.Move(Path.Combine(scratch, name), destination, true);
            }
            foreach (var name in new[] { "LICENSE", "README.txt" })
            {
                var entry = zip.GetEntry("ffmpeg-" + Version + "-essentials_build/" + name);
                if (entry is null || entry.Length > 500_000) continue;
                var staged = Path.Combine(scratch, name);
                entry.ExtractToFile(staged);
                var destination = Path.Combine(DirectoryPath, name);
                LocalFiles.RejectLinks(destination);
                File.Move(staged, destination, true);
            }
            progress?.Report(new("Engine ready", 1, "Video processing now works offline"));
        }
        finally { await LocalFiles.RemoveOwnedDirectoryAsync(scratch, DirectoryPath).ConfigureAwait(false); }
    }

    private static async Task<bool> MatchesAsync(string path, string expected, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        var hash = await SHA256.HashDataAsync(stream, token);
        return Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}
