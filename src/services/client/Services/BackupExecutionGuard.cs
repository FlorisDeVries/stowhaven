using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FlorisDeV.BackupClient.Services;

public sealed class BackupAlreadyRunningException() : InvalidOperationException("A backup attempt already owns this state database. No additional attempt was queued.");

/// <summary>
/// Whole-attempt ownership of the actual state file, including hard-link and directory aliases.
/// Linux flock is independent of SQLite's POSIX locks and survives SQLite connection closure.
/// Never delete or replace the file when releasing ownership.
/// </summary>
internal sealed class BackupExecutionGuard : IDisposable
{
    private readonly SafeFileHandle _handle;
    private BackupExecutionGuard(SafeFileHandle handle) => _handle = handle;

    internal static BackupExecutionGuard Acquire(string databasePath)
        => TryAcquire(databasePath, launchReservation: false) ?? throw new BackupAlreadyRunningException();

    // Observation never creates a database or initializes backup state. A read-only descriptor
    // can probe flock on Linux, independently of SQLite's own locks.
    internal static (string? Identity, bool Active) Observe(string databasePath)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var descriptor = Open(Path.GetFullPath(databasePath), 0x80000, 0); // O_RDONLY|O_CLOEXEC
        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == 2) return (null, false);
            throw Failure("observe execution protection", error);
        }
        using var probe = new BackupExecutionGuard(new SafeFileHandle((IntPtr)descriptor, ownsHandle: true));
        var identity = probe.Identity;
        if (Flock(probe._handle, 2 | 4) == 0) return (identity, false);
        var lockError = Marshal.GetLastPInvokeError();
        if (lockError == 11) return (identity, true);
        throw Failure("observe execution protection", lockError);
    }

    internal static BackupExecutionGuard? TryAcquire(string databasePath, bool launchReservation = false)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new InvalidOperationException("Backup state database identity is required for execution protection.");
        var path = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (OperatingSystem.IsWindows())
        {
            if (launchReservation) throw new PlatformNotSupportedException("Dashboard starts require Linux systemd user services.");
            var windowsHandle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            // Lock an unused byte beyond SQLite's locking page and supported database range.
            // Unlike FileShare.None this permits SQLite to open/read the database in the owner.
            var overlapped = new WindowsOverlapped { OffsetHigh = 0x40000000 };
            if (LockFileEx(windowsHandle, 3, 0, 1, 0, ref overlapped)) return new(windowsHandle);
            var windowsError = Marshal.GetLastPInvokeError(); windowsHandle.Dispose();
            if (windowsError == 33) return null;
            throw Failure("acquire execution protection", windowsError);
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Unsupported backup execution locking platform.");
        if (launchReservation && !OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Dashboard starts require Linux systemd user services.");
        var flags = OperatingSystem.IsLinux() ? 2 | 64 | 0x80000 : 2 | 0x200 | 0x1000000;
        var descriptor = Open(path, flags, 0x180); // Platform O_RDWR|O_CREAT|O_CLOEXEC, 0600
        if (descriptor < 0) throw Failure("open", Marshal.GetLastPInvokeError());
        var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        try
        {
            if (OperatingSystem.IsLinux())
            {
                if (Fstatfs(handle, out var filesystem) != 0) throw Failure("identify filesystem", Marshal.GetLastPInvokeError());
                if (filesystem.Type is 0x6969 or 0x517b or 0xff534d42 or 0xfe534d42)
                    throw new IOException("Backup state must use a local filesystem; NFS/SMB execution locking is unsupported.");
            }
            // A separate open-file-description byte lock serializes start requests without holding
            // execution flock against the service being started. SQLite's lock bytes are at 1 GiB.
            var reservation = new FileLock { Type = 1, Whence = 0, Start = 0, Length = 1 };
            var result = launchReservation ? Fcntl(handle, 37, ref reservation) : Flock(handle, 2 | 4);
            if (result == 0) return new(handle);
            var error = Marshal.GetLastPInvokeError();
            if (error == (OperatingSystem.IsMacOS() ? 35 : 11) || (launchReservation && error == 13)) { handle.Dispose(); return null; }
            throw Failure("acquire execution protection", error);
        }
        catch { handle.Dispose(); throw; }
    }

    internal string Identity
    {
        get
        {
            if (!OperatingSystem.IsLinux()) return OperatingSystem.IsWindows() ? "windows" : "macos";
            if (Statx(_handle, "", 0x1000, 0x7ff, out var value) != 0) throw Failure("identify state", Marshal.GetLastPInvokeError());
            return $"{value.DeviceMajor:x}:{value.DeviceMinor:x}:{value.Inode:x}";
        }
    }
    [StructLayout(LayoutKind.Explicit, Size = 256)] private struct LinuxStat
    {
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)] private static extern int Statx(SafeFileHandle fd, string path, int flags, uint mask, out LinuxStat value);
    public void Dispose() => _handle.Dispose();
    private static IOException Failure(string operation, int error) => new($"Cannot {operation} for backup state. Check state permissions and local filesystem locking support.", new Win32Exception(error));
    [StructLayout(LayoutKind.Sequential)] private struct WindowsOverlapped
    {
        public UIntPtr Internal, InternalHigh;
        public uint Offset, OffsetHigh;
        public IntPtr Event;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockFileEx(SafeFileHandle handle, uint flags, uint reserved, uint lengthLow, uint lengthHigh, ref WindowsOverlapped overlapped);
    [StructLayout(LayoutKind.Sequential)] private struct FileLock { public short Type, Whence; public long Start, Length; public int Pid; }
    [StructLayout(LayoutKind.Sequential)] private struct FileSystemInfo
    {
        public long Type, BlockSize, Blocks, BlocksFree, BlocksAvailable, Files, FilesFree;
        public int Fsid1, Fsid2;
        public long NameLength, FragmentSize, Flags, Spare1, Spare2, Spare3, Spare4;
    }
    [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int Open(string path, int flags, uint mode);
    [DllImport("libc", EntryPoint = "flock", SetLastError = true)] private static extern int Flock(SafeFileHandle fd, int operation);
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)] private static extern int Fcntl(SafeFileHandle fd, int command, ref FileLock value);
    [DllImport("libc", EntryPoint = "fstatfs", SetLastError = true)] private static extern int Fstatfs(SafeFileHandle fd, out FileSystemInfo value);
}
