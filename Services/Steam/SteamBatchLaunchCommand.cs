using System;
using System.IO;
using SteamPluginManager.Models;

namespace SteamPluginManager.Services.Steam;

internal static class SteamBatchLaunchCommand
{
    internal static SteamLaunchOption Create(
        string executablePath,
        string relativeExecutable,
        string arguments,
        string workingDirectory)
    {
        string extension = Path.GetExtension(executablePath);
        if (!extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            return new SteamLaunchOption
            {
                Executable = relativeExecutable,
                Arguments = arguments,
                WorkingDirectory = workingDirectory
            };
        }

        string batchPath = Path.GetFullPath(executablePath);
        string commandInterpreter = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        string relativeInterpreter = Path.GetRelativePath(workingDirectory, commandInterpreter);
        if (Path.IsPathRooted(relativeInterpreter))
        {
            throw new InvalidOperationException(
                "The game and Windows are on different drives, so Steam cannot resolve the batch command interpreter as a relative launch executable.");
        }

        string batchArguments = string.IsNullOrWhiteSpace(arguments) ? string.Empty : $" {arguments}";
        string batchDirectory = Path.GetDirectoryName(batchPath) ?? workingDirectory;
        string relativeWorkingDirectory = Path.GetRelativePath(workingDirectory, batchDirectory);
        return new SteamLaunchOption
        {
            Executable = relativeInterpreter,
            Arguments = $"/D /S /C \"\"{batchPath}\"{batchArguments}\"",
            WorkingDirectory = relativeWorkingDirectory == "." ? string.Empty : relativeWorkingDirectory
        };
    }

    internal static bool TryExtractArguments(
        string executable,
        string arguments,
        string gameInstallDirectory,
        out string batchArguments)
    {
        batchArguments = arguments;

        try
        {
            string commandInterpreter = Path.GetFullPath(Path.Combine(
                gameInstallDirectory,
                executable));
            if (!string.Equals(
                    commandInterpreter,
                    Path.GetFullPath(Path.Combine(Environment.SystemDirectory, "cmd.exe")),
                    StringComparison.OrdinalIgnoreCase))
                return false;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }

        const string prefix = "/D /S /C \"\"";
        if (!arguments.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || arguments.Length <= prefix.Length
            || arguments[^1] != '"')
            return false;

        int pathEnd = arguments.IndexOf('"', prefix.Length);
        if (pathEnd < prefix.Length)
            return false;

        string batchPath = arguments.Substring(prefix.Length, pathEnd - prefix.Length);
        string extension = Path.GetExtension(batchPath);
        if (!extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
            return false;

        batchArguments = arguments.Substring(pathEnd + 1, arguments.Length - pathEnd - 2).TrimStart();
        return true;
    }
}
