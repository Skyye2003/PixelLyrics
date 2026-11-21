using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PixelLyrics.Probe;

internal static class BoundedHidTransport
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    internal static void Send(string path, byte[] report)
    {
        using var handle = CreateFileW(path, 0x40000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("无法打开 USB 屏幕", Marshal.GetLastWin32Error());
        using var stream = new FileStream(handle, FileAccess.Write, 64, true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        stream.WriteAsync(report.AsMemory(), timeout.Token).AsTask().GetAwaiter().GetResult();
    }
}
