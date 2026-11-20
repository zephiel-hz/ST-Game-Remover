using SteamPluginManager.Services.Steam;

namespace SteamPluginManager.Tests;

public class OpenSteamToolDeploymentServiceTests
{
    [Fact]
    public void DeployAndRestore_PreservesSteamFilesAndRemovesManagedFiles()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string steamRoot = Path.Combine(root, "Steam");
            string archiveRoot = Path.Combine(root, "release");
            string supplementalRoot = Path.Combine(steamRoot, "config", "stplug-in");
            Directory.CreateDirectory(Path.Combine(steamRoot, "config"));
            Directory.CreateDirectory(Path.Combine(archiveRoot, "nested", "bin"));
            Directory.CreateDirectory(supplementalRoot);

            string originalSteamFile = Path.Combine(steamRoot, "dwmapi.dll");
            File.WriteAllText(originalSteamFile, "steam-original");
            File.WriteAllText(Path.Combine(archiveRoot, "dwmapi.dll"), "release-dll");
            File.WriteAllText(Path.Combine(archiveRoot, "OpenSteamTool.dll"), "open-steam-tool");
            File.WriteAllText(Path.Combine(archiveRoot, "nested", "bin", "OpenSteamTool.dll"), "release-file");
            File.WriteAllText(Path.Combine(supplementalRoot, "plugin.lua"), "plugin-file");

            OpenSteamToolDeploymentService.Deploy(
                steamRoot,
                archiveRoot,
                supplementalRoot,
                Path.Combine("config", "lua"),
                synchronizeSupplementalFiles: true);

            Assert.Equal("release-dll", File.ReadAllText(originalSteamFile));
            Assert.Equal("open-steam-tool", File.ReadAllText(Path.Combine(steamRoot, "OpenSteamTool.dll")));
            Assert.Equal("release-file", File.ReadAllText(Path.Combine(steamRoot, "nested", "bin", "OpenSteamTool.dll")));
            Assert.Equal("plugin-file", File.ReadAllText(Path.Combine(steamRoot, "config", "lua", "plugin.lua")));

            File.Move(
                Path.Combine(steamRoot, "OpenSteamTool.dll"),
                Path.Combine(steamRoot, "OpenSteamTool.dll.bak"));
            string unrelatedFile = Path.Combine(steamRoot, "user-data.txt");
            File.WriteAllText(unrelatedFile, "keep");

            Assert.True(OpenSteamToolDeploymentService.RestoreOriginalState(steamRoot));

            Assert.Equal("steam-original", File.ReadAllText(originalSteamFile));
            Assert.False(File.Exists(Path.Combine(steamRoot, "OpenSteamTool.dll")));
            Assert.False(File.Exists(Path.Combine(steamRoot, "OpenSteamTool.dll.bak")));
            Assert.False(File.Exists(Path.Combine(steamRoot, "nested", "bin", "OpenSteamTool.dll")));
            Assert.False(File.Exists(Path.Combine(steamRoot, "config", "lua", "plugin.lua")));
            Assert.False(Directory.Exists(Path.Combine(steamRoot, "nested")));
            Assert.Equal("keep", File.ReadAllText(unrelatedFile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeployUpdate_RemovesObsoleteFilesAndKeepsOriginalBackup()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string steamRoot = Path.Combine(root, "Steam");
            string firstRelease = Path.Combine(root, "release-1");
            string secondRelease = Path.Combine(root, "release-2");
            Directory.CreateDirectory(steamRoot);
            Directory.CreateDirectory(firstRelease);
            Directory.CreateDirectory(secondRelease);

            File.WriteAllText(Path.Combine(steamRoot, "existing.dat"), "steam-original");
            File.WriteAllText(Path.Combine(firstRelease, "existing.dat"), "version-1");
            File.WriteAllText(Path.Combine(firstRelease, "obsolete.dat"), "obsolete");
            File.WriteAllText(Path.Combine(secondRelease, "existing.dat"), "version-2");
            File.WriteAllText(Path.Combine(secondRelease, "current.dat"), "current");

            OpenSteamToolDeploymentService.Deploy(steamRoot, firstRelease);
            OpenSteamToolDeploymentService.Deploy(steamRoot, secondRelease);

            Assert.Equal("version-2", File.ReadAllText(Path.Combine(steamRoot, "existing.dat")));
            Assert.False(File.Exists(Path.Combine(steamRoot, "obsolete.dat")));
            Assert.Equal("current", File.ReadAllText(Path.Combine(steamRoot, "current.dat")));

            Assert.True(OpenSteamToolDeploymentService.RestoreOriginalState(steamRoot));

            Assert.Equal("steam-original", File.ReadAllText(Path.Combine(steamRoot, "existing.dat")));
            Assert.False(File.Exists(Path.Combine(steamRoot, "obsolete.dat")));
            Assert.False(File.Exists(Path.Combine(steamRoot, "current.dat")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ManagedDisableAndRestore_RestoresBothDllAndExistingBakFiles()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string steamRoot = Path.Combine(root, "Steam");
            string archiveRoot = Path.Combine(root, "release");
            Directory.CreateDirectory(steamRoot);
            Directory.CreateDirectory(archiveRoot);

            File.WriteAllText(Path.Combine(steamRoot, "OpenSteamTool.dll"), "steam-original");
            File.WriteAllText(Path.Combine(steamRoot, "OpenSteamTool.dll.bak"), "user-backup");
            File.WriteAllText(Path.Combine(archiveRoot, "OpenSteamTool.dll"), "release-file");
            OpenSteamToolDeploymentService.Deploy(steamRoot, archiveRoot);

            Assert.True(OpenSteamToolDeploymentService.TryMoveManagedFile(
                steamRoot,
                "OpenSteamTool.dll",
                "OpenSteamTool.dll.bak"));
            Assert.Equal("release-file", File.ReadAllText(Path.Combine(steamRoot, "OpenSteamTool.dll.bak")));

            Assert.True(OpenSteamToolDeploymentService.RestoreOriginalState(steamRoot));

            Assert.Equal("steam-original", File.ReadAllText(Path.Combine(steamRoot, "OpenSteamTool.dll")));
            Assert.Equal("user-backup", File.ReadAllText(Path.Combine(steamRoot, "OpenSteamTool.dll.bak")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Deploy_RejectsSupplementalPathOutsideSteamRoot()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string steamRoot = Path.Combine(root, "Steam");
            string archiveRoot = Path.Combine(root, "release");
            string supplementalRoot = Path.Combine(root, "supplemental");
            Directory.CreateDirectory(steamRoot);
            Directory.CreateDirectory(archiveRoot);
            Directory.CreateDirectory(supplementalRoot);
            File.WriteAllText(Path.Combine(archiveRoot, "release.dat"), "release");
            File.WriteAllText(Path.Combine(supplementalRoot, "supplemental.dat"), "supplemental");

            Assert.Throws<InvalidDataException>(() =>
                OpenSteamToolDeploymentService.Deploy(
                    steamRoot,
                    archiveRoot,
                    supplementalRoot,
                    Path.Combine("..", "outside"),
                    synchronizeSupplementalFiles: true));

            Assert.False(File.Exists(Path.Combine(steamRoot, "release.dat")));
            Assert.False(File.Exists(Path.Combine(root, "outside", "supplemental.dat")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RestoreWithoutManifest_LeavesUntrackedSteamFilesUntouched()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string steamRoot = Path.Combine(root, "Steam");
            Directory.CreateDirectory(steamRoot);
            string steamFile = Path.Combine(steamRoot, "dwmapi.dll");
            File.WriteAllText(steamFile, "steam-original");

            Assert.False(OpenSteamToolDeploymentService.RestoreOriginalState(steamRoot));
            Assert.Equal("steam-original", File.ReadAllText(steamFile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"OpenSteamToolDeploymentTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
