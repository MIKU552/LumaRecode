using System.Runtime.InteropServices;
using System.Text;

namespace VideoConverter;

/// <summary>Attaches a WinExe to its calling terminal while preserving PowerShell pipeline handles.</summary>
internal static class ConsoleConnection
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint process);
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int kind);
    [DllImport("kernel32.dll")] private static extern bool SetStdHandle(int kind, IntPtr handle);
    [DllImport("kernel32.dll")] private static extern uint GetFileType(IntPtr handle);

    public static void Connect()
    {
        // AttachConsole may replace standard handles. Preserve inherited pipes/files.
        var handles = new[] { -10, -11, -12 }.Select(k => (kind: k, handle: GetStdHandle(k)))
            .Where(h => h.handle != IntPtr.Zero && h.handle != new IntPtr(-1) && GetFileType(h.handle) != 0).ToArray();
        AttachConsole(uint.MaxValue);
        foreach (var h in handles)
        {
            SetStdHandle(h.kind, h.handle);
        }

        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
    }
}
