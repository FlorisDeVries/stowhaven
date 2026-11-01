using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FlorisDeV.BackupClient.Dashboard;

internal static class DashboardLogSources
{
    // Unlike reporting storage, existing log storage must never be created, chmodded,
    // replaced, or pruned by the observation host.
    internal static void ValidateDirectory(string directory)
    {
        if (!OperatingSystem.IsLinux()) throw new IOException("Client log observation requires Linux.");
        var leaf = Path.GetFullPath(directory);
        for (string? path = leaf; path != null; path = Path.GetDirectoryName(path))
        {
            var info = Inspect(-100, path, 0x100); // AT_SYMLINK_NOFOLLOW
            var trustedStickyParent = path != leaf && info.Uid == 0 && (info.Mode & 0x200) != 0;
            if ((info.Mode & 0xf000) != 0x4000 || (info.Uid != 0 && info.Uid != GetEffectiveUserId()) ||
                ((info.Mode & 0x12) != 0 && !trustedStickyParent))
                throw new IOException("Unsafe client log directory.");
        }
    }

    internal static FileStream Open(string path)
    {
        // O_NOFOLLOW prevents leaf-link replacement races. O_NONBLOCK allows us to
        // reject a FIFO/device before reading it instead of waiting for a writer.
        var descriptor = OpenNative(path, 0x20000 | 0x80000 | 0x800);
        if (descriptor < 0)
        {
            if (Marshal.GetLastPInvokeError() == 2) throw new FileNotFoundException();
            throw new IOException("Cannot open client log source.");
        }
        var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        try
        {
            var info = Inspect(descriptor, "", 0x1000); // AT_EMPTY_PATH: inspect the opened inode.
            if ((info.Mode & 0xf000) != 0x8000 || info.Uid != GetEffectiveUserId() || info.Links > 1)
                throw new IOException("Client log source must be a regular user-owned file without aliases.");
            return new FileStream(handle, FileAccess.Read);
        }
        catch { handle.Dispose(); throw; }
    }

    private static LinuxStat Inspect(int directory, string path, int flags)
    {
        if (Statx(directory, path, flags, 0x7ff, out var info) != 0) throw new IOException("Cannot inspect client log source.");
        return info;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStat
    {
        [FieldOffset(16)] public uint Links;
        [FieldOffset(20)] public uint Uid;
        [FieldOffset(28)] public ushort Mode;
    }
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, string path, int flags, uint mask, out LinuxStat info);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenNative(string path, int flags);
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
}
