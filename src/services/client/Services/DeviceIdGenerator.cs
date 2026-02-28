using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FlorisDeV.BackupClient.Services;

/// <summary>
/// Generates a stable, hardware-based device identifier.
/// Uses multiple hardware characteristics to create a unique device ID that persists across reboots.
/// </summary>
public static class DeviceIdGenerator
{
    /// <summary>
    /// Generates a device ID based on hardware characteristics.
    /// Falls back to Guid.NewGuid() if hardware info cannot be obtained.
    /// </summary>
    public static Guid GenerateDeviceId()
    {
        try
        {
            // 1. Machine Name (most stable across OS reinstalls if hostname is preserved)
            var components = new List<string> { $"Machine:{Environment.MachineName}" };

            // 2. MAC Address of first network adapter (stable physical hardware)
            var macAddress = GetFirstMacAddress();
            if (!string.IsNullOrEmpty(macAddress))
            {
                components.Add($"MAC:{macAddress}");
            }

            // 3. OS-specific identifiers
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Windows: Use motherboard serial or CPU ID
                var machineGuid = GetWindowsMachineGuid();
                if (!string.IsNullOrEmpty(machineGuid))
                {
                    components.Add($"WinID:{machineGuid}");
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // Linux: Use machine-id
                var machineId = GetLinuxMachineId();
                if (!string.IsNullOrEmpty(machineId))
                {
                    components.Add($"LinuxID:{machineId}");
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // macOS: Use hardware UUID
                var hardwareUuid = GetMacOSHardwareUuid();
                if (!string.IsNullOrEmpty(hardwareUuid))
                {
                    components.Add($"MacID:{hardwareUuid}");
                }
            }

            // If we have at least one hardware component, hash them together
            if (components.Count > 1) // More than just machine name
            {
                var combined = string.Join("|", components);
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(combined));

                // Use first 16 bytes of hash to create a deterministic GUID
                var guidBytes = new byte[16];
                Array.Copy(hash, guidBytes, 16);

                return new Guid(guidBytes);
            }

            // Fallback to random GUID if hardware info unavailable
            return Guid.NewGuid();
        }
        catch
        {
            // If any error occurs, fall back to random GUID
            return Guid.NewGuid();
        }
    }

    private static string? GetFirstMacAddress()
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n =>
                    n.OperationalStatus == OperationalStatus.Up &&
                    n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    n.GetPhysicalAddress().ToString().Length > 0);

            return nic?.GetPhysicalAddress().ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string? GetWindowsMachineGuid()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null;

        try
        {
            var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid")?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string? GetLinuxMachineId()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return null;

        try
        {
            // /etc/machine-id is the standard location for systemd-based systems
            if (File.Exists("/etc/machine-id"))
            {
                return File.ReadAllText("/etc/machine-id").Trim();
            }

            // Fallback for older systems
            if (File.Exists("/var/lib/dbus/machine-id"))
            {
                return File.ReadAllText("/var/lib/dbus/machine-id").Trim();
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetMacOSHardwareUuid()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return null;

        try
        {
            // Use system_profiler to get hardware UUID
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "/usr/sbin/system_profiler",
                    Arguments = "SPHardwareDataType",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            // Parse output for "Hardware UUID: ..."
            var lines = output.Split('\n');
            var uuidLine = lines.FirstOrDefault(l => l.Contains("Hardware UUID"));
            if (uuidLine != null)
            {
                var parts = uuidLine.Split(':');
                if (parts.Length > 1)
                {
                    return parts[1].Trim();
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
