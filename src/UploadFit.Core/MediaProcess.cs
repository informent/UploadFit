using System.Diagnostics;
using System.Text;

namespace UploadFit.Core;

public static class MediaProcess
{
    public static ProcessStartInfo StartInfo(string executable, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    public static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken,
        TimeSpan timeout, Action<string>? onLine = null)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        using var process = new Process { StartInfo = StartInfo(executable, arguments) };
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start()) throw new MediaException("The video engine could not start.");
            process.StandardInput.Close();
            using var registration = limit.Token.Register(() => Kill(process));
            var stderr = ReadAsync(process.StandardError, null, 32_768, keepTail: true, limit.Token);
            var stdout = ReadAsync(process.StandardOutput, onLine, 8_000_000, keepTail: false, limit.Token);
            try
            {
                // Observe reader failure as well as process exit so a full pipe cannot deadlock.
                var exited = process.WaitForExitAsync(limit.Token);
                var pending = new List<Task> { exited, stdout, stderr };
                while (pending.Count > 0)
                {
                    var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                    await completed.ConfigureAwait(false);
                    pending.Remove(completed);
                }
                var output = await stdout.ConfigureAwait(false);
                var error = await stderr.ConfigureAwait(false);
                limit.Token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0)
                    throw new MediaException("The video engine could not read or encode this file. " + Compact(error));
                return output;
            }
            finally
            {
                Kill(process);
                // Kill is asynchronous on Windows. Wait for file handles to close before cleanup.
                try { await process.WaitForExitAsync().ConfigureAwait(false); } catch (InvalidOperationException) { }
                try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (MediaException) { }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new MediaException("The video engine took too long. Try a shorter clip or add the file again."); }
        catch (System.ComponentModel.Win32Exception)
        { throw new MediaException("The video engine is missing or could not start. Open Engine setup to repair it."); }
    }

    private static async Task<string> ReadAsync(StreamReader reader, Action<string>? onLine, int max,
        bool keepTail, CancellationToken token)
    {
        var text = new StringBuilder();
        var line = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            if (onLine is null)
            {
                text.Append(buffer, 0, count);
                if (text.Length > max)
                {
                    if (keepTail) text.Remove(0, text.Length - max);
                    else throw new MediaException("The video contains too much metadata to read safely.");
                }
            }
            else
            {
                for (var i = 0; i < count; i++)
                {
                    if (buffer[i] == '\n') { onLine(line.ToString().TrimEnd('\r')); line.Clear(); }
                    else if (line.Length < 4096) line.Append(buffer[i]);
                }
            }
        }
        return text.ToString();
    }

    private static string Compact(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var last = string.Join(" ", lines.TakeLast(2)).Trim();
        return last.Length <= 400 ? last : last[^400..];
    }

    public static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
