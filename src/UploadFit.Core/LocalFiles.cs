namespace UploadFit.Core;

public static class LocalFiles
{
    public static readonly string[] Extensions = [".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v", ".wmv", ".mpg", ".mpeg", ".ts", ".mts", ".m2ts", ".ogv"];

    public static string ValidateInput(string path)
    {
        path = Path.GetFullPath(path);
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || new Uri(path).IsUnc)
            throw new MediaException("Copy the video to a local drive before adding it.");
        if (!Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            throw new MediaException("Add a video file such as MP4, MOV, MKV or WebM.");
        RejectLinks(path);
        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= 0) throw new MediaException("This file is missing or empty.");
        if (file.Length > 20L * 1024 * 1024 * 1024) throw new MediaException("Choose a video smaller than 20 GB.");
        return path;
    }

    public static void RejectLinks(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new MediaException("Choose a regular local file or folder instead of a linked path.");
            current = Path.GetDirectoryName(current);
        }
    }

    public static string ValidateOutput(string directory)
    {
        directory = Path.GetFullPath(directory);
        if (directory.StartsWith(@"\\", StringComparison.Ordinal) || new Uri(directory).IsUnc)
            throw new MediaException("Choose an output folder on a local drive.");
        RejectLinks(directory);
        Directory.CreateDirectory(directory);
        RejectLinks(directory);
        return directory;
    }

    public static FileStream LockSource(MediaInfo info)
    {
        ValidateInput(info.Path);
        var stream = new FileStream(info.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var current = new FileInfo(info.Path);
        if (stream.Length != info.Bytes || current.LastWriteTimeUtc != info.LastWriteUtc)
        {
            stream.Dispose();
            throw new MediaException("The source changed after it was added. Remove it and add the current file.");
        }
        return stream;
    }

    public static string Promote(string staged, string outputDirectory, string sourcePath)
    {
        RejectLinks(outputDirectory);
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        name = new string(name.Select(c => char.IsControl(c) || Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        name = name.Trim().TrimEnd('.');
        if (name.Length > 100) name = name[..100];
        if (name.Length == 0) name = "clip";
        // Always append a suffix, including for Windows reserved device names.
        for (var i = 0; i < 10_000; i++)
        {
            var candidate = Path.Combine(outputDirectory, name + "-fit" + (i == 0 ? "" : $"-{i + 1}") + ".mp4");
            try { File.Move(staged, candidate, overwrite: false); return candidate; }
            catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate)) { }
        }
        throw new MediaException("Too many files have this name. Choose a different output folder.");
    }

    public static async Task<bool> RemoveOwnedDirectoryAsync(string directory, string parent)
    {
        var full = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(parent), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith(".uploadfit-", StringComparison.Ordinal)) return false;
        // Windows can briefly retain handles after process exit or while scanning a new file.
        // Cleanup has its own bounded retry and must finish even when the job was cancelled.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                RejectLinks(full);
                if (!Directory.Exists(full)) return true;
                // No recursive delete: only our flat scratch files are removed.
                foreach (var file in Directory.EnumerateFiles(full))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) File.Delete(file);
                }
                Directory.Delete(full, recursive: false);
                return true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (MediaException) { return false; }
            if (attempt < 5) await Task.Delay(50 * (attempt + 1)).ConfigureAwait(false);
        }
        return false;
    }
}
