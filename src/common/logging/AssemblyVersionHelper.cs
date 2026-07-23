using System.Reflection;

namespace FlorisDeV.Logging;

/// <summary>
/// Reads an assembly's informational version without touching <see cref="Assembly.Location"/>,
/// which is always empty for assemblies embedded in a single-file publish.
/// </summary>
public static class AssemblyVersionHelper
{
    public static string GetInformationalVersion(Assembly assembly)
    {
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }
}
