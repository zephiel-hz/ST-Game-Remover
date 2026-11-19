using SteamPluginManager.Models;
using SteamPluginManager.Services.Steam;

namespace SteamPluginManager.Tests;

public sealed class SteamBatchLaunchCommandTests
{
    [Theory]
    [InlineData("Launcher.bat")]
    [InlineData("Launcher.cmd")]
    public void WrapsBatchFileWithCommandInterpreter(string fileName)
    {
        string gameDirectory = Path.Combine(Path.GetTempPath(), "HZ Game");
        string batchPath = Path.Combine(gameDirectory, fileName);

        SteamLaunchOption option = SteamBatchLaunchCommand.Create(
            batchPath,
            fileName,
            "-offline",
            gameDirectory);

        string commandInterpreter = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        Assert.False(Path.IsPathRooted(option.Executable));
        Assert.Equal(
            Path.GetFullPath(commandInterpreter),
            Path.GetFullPath(Path.Combine(gameDirectory, option.Executable)));
        Assert.Equal($"/D /S /C \"\"{Path.GetFullPath(batchPath)}\" -offline\"", option.Arguments);
        Assert.Empty(option.WorkingDirectory);
    }

    [Fact]
    public void LeavesWorkingDirectoryEmptyForBatchInSubfolder()
    {
        string gameDirectory = Path.Combine(Path.GetTempPath(), "HZ Game");
        string batchPath = Path.Combine(gameDirectory, "scripts", "Launcher.bat");

        SteamLaunchOption option = SteamBatchLaunchCommand.Create(
            batchPath,
            "scripts\\Launcher.bat",
            string.Empty,
            gameDirectory);

        Assert.Empty(option.WorkingDirectory);
    }

    [Fact]
    public void ExtractsForwardedArgumentsFromAnExistingBatchEntry()
    {
        string gameDirectory = Path.Combine(Path.GetTempPath(), "HZ Game");
        string batchPath = Path.Combine(gameDirectory, "Launcher.bat");
        SteamLaunchOption option = SteamBatchLaunchCommand.Create(
            batchPath,
            "Launcher.bat",
            "-offline",
            gameDirectory);

        bool extracted = SteamBatchLaunchCommand.TryExtractArguments(
            option.Executable,
            option.Arguments,
            gameDirectory,
            out string arguments);

        Assert.True(extracted);
        Assert.Equal("-offline", arguments);
    }

    [Fact]
    public void LeavesExecutableLaunchWorkingDirectoryEmpty()
    {
        string gameDirectory = Path.Combine(Path.GetTempPath(), "HZ Game");

        SteamLaunchOption option = SteamBatchLaunchCommand.Create(
            Path.Combine(gameDirectory, "game.exe"),
            "bin\\game.exe",
            "-safe",
            gameDirectory);

        Assert.Equal("bin\\game.exe", option.Executable);
        Assert.Equal("-safe", option.Arguments);
        Assert.Empty(option.WorkingDirectory);
    }
}
