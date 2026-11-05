using FluentAssertions;
using FlorisDeV.BackupClient.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlorisDeV.BackupClient.Tests.Services;

/// <summary>
/// Scans a target laid out like a real workstation folder through the shipped default
/// <c>.backupignore</c>, using the same parser and scanner the backup run uses.
/// </summary>
public sealed class DefaultBackupIgnoreTests : IDisposable
{
    private readonly string _target = Path.Combine(Path.GetTempPath(), $"default-ignore-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_target))
        {
            Directory.Delete(_target, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TargetRootFoldersNamedLikeLinuxSystemDirectoriesAreBackedUp()
    {
        // A projects target with a top-level "dev" folder lost every file under it to the
        // root-anchored system-directory patterns, which only make sense when the target is "/".
        var included = await ScanAsync(
            "dev/active/app/src/Program.cs",
            "run/notes.md",
            "tmp/draft.txt",
            "sys/readme.txt",
            "var/log/kept-by-name.txt");

        included.Should().BeEquivalentTo(
            "dev/active/app/src/Program.cs",
            "run/notes.md",
            "tmp/draft.txt",
            "sys/readme.txt",
            "var/log/kept-by-name.txt");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SourceFoldersNamedCoreOrPackagesAreBackedUp()
    {
        // Matching is case-insensitive and an excluded directory is pruned whole, so the old
        // crash-dump ("core") and legacy NuGet ("packages") patterns hid source trees.
        var included = await ScanAsync(
            "src/common/core/Data/Store.cs",
            "Assets/Plugins/DemiLib/Core/Utils.cs",
            "Assets/Packages/SimCrowds/Agent.cs");

        included.Should().BeEquivalentTo(
            "src/common/core/Data/Store.cs",
            "Assets/Plugins/DemiLib/Core/Utils.cs",
            "Assets/Packages/SimCrowds/Agent.cs");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UnityLibraryCachesAreExcludedButProjectSourcesAndMacLibraryAreKept()
    {
        var included = await ScanAsync(
            "dev/Game/Assets/Scenes/Main.unity",
            "dev/Game/Packages/manifest.json",
            "dev/Game/ProjectSettings/ProjectSettings.asset",
            "dev/Game/Library/PackageCache/com.unity.ugui@1.0.0/package.json",
            "dev/Game/Library/Artifacts/00/0011aabb",
            "dev/Game/Library/ShaderCache/shader/a.bin",
            "dev/Game/Library/Bee/fullprofile.json",
            "dev/Game/Library/BurstCache/lib.bin",
            "dev/Game/Library/ScriptAssemblies/Assembly-CSharp.dll",
            "dev/Game/Library/ArtifactDB",
            "dev/Game/Library/SourceAssetDB",
            "home/Library/Application Support/Notes/notes.db",
            "home/Library/Mobile Documents/report.pages");

        included.Should().BeEquivalentTo(
            "dev/Game/Assets/Scenes/Main.unity",
            "dev/Game/Packages/manifest.json",
            "dev/Game/ProjectSettings/ProjectSettings.asset",
            "home/Library/Application Support/Notes/notes.db",
            "home/Library/Mobile Documents/report.pages");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RegenerableLinuxUserStoresAreExcludedButAppDataIsKept()
    {
        var included = await ScanAsync(
            ".local/share/flatpak/runtime/org.kde.Platform/x86_64/6.10/files/lib/libQt.so",
            ".local/share/gem/ruby/3.4.0/doc/rake/ri/cache.ri",
            ".local/share/uv/python/cpython-3.13/bin/python3",
            ".local/share/NuGet/http-cache/package.dat",
            ".local/share/Steam/appcache/httpcache/00/entry",
            ".config/Code/WebStorage/1/CacheStorage/abc/0",
            ".Trash-1000/files/old-project/readme.md",
            ".var/app/org.prismlauncher.PrismLauncher/data/instances/world/level.dat",
            ".local/share/Steam/userdata/123/config/localconfig.vdf",
            ".config/Code/User/settings.json");

        included.Should().BeEquivalentTo(
            ".var/app/org.prismlauncher.PrismLauncher/data/instances/world/level.dat",
            ".local/share/Steam/userdata/123/config/localconfig.vdf",
            ".config/Code/User/settings.json");
    }

    private async Task<List<string>> ScanAsync(params string[] relativePaths)
    {
        foreach (var relativePath in relativePaths)
        {
            var path = Path.Combine(_target, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, relativePath);
        }

        var shippedDefault = Path.Combine(AppContext.BaseDirectory, ".backupignore");
        File.Exists(shippedDefault).Should().BeTrue("the client ships its default ignore file");
        var patterns = BackupIgnoreParser.GetCombinedPatterns(shippedDefault);

        var scanner = new FileSystemService(NullLogger<FileSystemService>.Instance);
        var included = new List<string>();
        await foreach (var file in scanner.ScanDirectoryStreamAsync(_target, patterns))
        {
            included.Add(Path.GetRelativePath(_target, file.FilePath).Replace('\\', '/'));
        }

        return included;
    }
}
