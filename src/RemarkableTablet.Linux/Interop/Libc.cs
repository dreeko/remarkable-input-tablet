using System.Runtime.InteropServices;

namespace RemarkableTablet.Linux.Interop;

internal static class Libc
{
    internal const int O_WRONLY   = 1;
    internal const int O_NONBLOCK = 0x800;

    [DllImport("libc", SetLastError = true)]
    internal static extern int open([MarshalAs(UnmanagedType.LPStr)] string pathname, int flags);

    // Three ioctl overloads covering the argument shapes we need
    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    internal static extern int ioctl_int(int fd, ulong request, int arg);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    internal static extern unsafe int ioctl_ptr(int fd, ulong request, void* arg);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    internal static extern int ioctl_noarg(int fd, ulong request);

    [DllImport("libc", SetLastError = true)]
    internal static extern unsafe nint write(int fd, void* buf, nuint count);

    [DllImport("libc", SetLastError = true)]
    internal static extern int close(int fd);
}
