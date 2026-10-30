using System.Runtime.InteropServices;

internal static class ProcessFixture
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    public static async Task<int> RunAsync()
    {
        // Reproduce a child that closes stderr before overwhelming the stdout reader.
        if (!OperatingSystem.IsWindows()) return 2;
        CloseHandle(GetStdHandle(-12));
        await Task.Delay(200);
        using var output = Console.OpenStandardOutput();
        var block = new byte[8192];
        Array.Fill(block, (byte)'x');
        for (var i = 0; i < 4096; i++) await output.WriteAsync(block);
        return 0;
    }
}
