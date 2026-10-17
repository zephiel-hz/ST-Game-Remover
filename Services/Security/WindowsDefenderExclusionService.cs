using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SteamPluginManager
{
    public static class WindowsDefenderExclusionService
    {
        public static async Task RunExclusionCheckAsync()
        {
            try
            {
                Logger.Log("[WindowsDefenderExclusionService] Starting exclusion check...");

                var foldersToExclude = GetFoldersToExclude();
                var validFolders = foldersToExclude.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                
                Logger.Log($"[WindowsDefenderExclusionService] Folders to check: {validFolders.Count}");
                
                if (validFolders.Count == 0)
                {
                    Logger.Log("[WindowsDefenderExclusionService] No valid folders to exclude. Completed.");
                    return;
                }

                var currentExclusions = await GetCurrentExclusionsAsync();
                var foldersToAdd = new List<string>();

                foreach (var folder in validFolders)
                {
                    string cleanFolder = Path.GetFullPath(folder).TrimEnd('\\');
                    bool alreadyExcluded = currentExclusions.Any(ex => 
                    {
                        try
                        {
                            return Path.GetFullPath(ex).TrimEnd('\\').Equals(cleanFolder, StringComparison.OrdinalIgnoreCase);
                        }
                        catch
                        {
                            return ex.TrimEnd('\\').Equals(cleanFolder, StringComparison.OrdinalIgnoreCase);
                        }
                    });
                    
                    if (alreadyExcluded)
                    {
                        Logger.Log($"[WindowsDefenderExclusionService] Already excluded: {cleanFolder}");
                    }
                    else
                    {
                        Logger.Log($"[WindowsDefenderExclusionService] Needs exclusion: {cleanFolder}");
                        foldersToAdd.Add(cleanFolder);
                    }
                }

                if (foldersToAdd.Count == 0)
                {
                    Logger.Log("[WindowsDefenderExclusionService] All folders already excluded. Completed.");
                    return;
                }

                Logger.Log($"[WindowsDefenderExclusionService] Requesting UAC elevation for {foldersToAdd.Count} new exclusions...");
                bool success = await AddExclusionsAsync(foldersToAdd);
                
                if (success)
                {
                    foreach (var folder in foldersToAdd)
                    {
                        Logger.Log($"[WindowsDefenderExclusionService] Successfully added exclusion: {folder}");
                    }
                }
                else
                {
                    Logger.Log("[WindowsDefenderExclusionService] Failed to add exclusions (UAC denied or error).");
                }
                
                Logger.Log("[WindowsDefenderExclusionService] Exclusion check completed.");
            }
            catch (Exception ex)
            {
                Logger.Log($"[WindowsDefenderExclusionService] Error during exclusion check: {ex.Message}");
            }
        }

        private static List<string> GetFoldersToExclude()
        {
            var folders = new List<string>();

            try
            {
                folders.Add(Path.GetTempPath());
            }
            catch (Exception ex)
            {
                Logger.Log($"[WindowsDefenderExclusionService] Error getting temp path: {ex.Message}");
            }

            string? steamPath = null;
            try
            {
                steamPath = SteamHelper.GetSteamPath();
                if (!string.IsNullOrWhiteSpace(steamPath))
                {
                    folders.Add(steamPath);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[WindowsDefenderExclusionService] Error getting Steam path: {ex.Message}");
            }

            try
            {
                folders.AddRange(GetSteamLibraryFolders(steamPath));
            }
            catch (Exception ex)
            {
                Logger.Log($"[WindowsDefenderExclusionService] Error getting Steam library folders: {ex.Message}");
            }

            return folders;
        }

        private static List<string> GetSteamLibraryFolders(string? steamPath)
        {
            var libraries = new List<string>();

            if (!string.IsNullOrWhiteSpace(steamPath))
            {
                try
                {
                    string[] possibleVdfPaths = {
                        Path.Combine(steamPath, "steamapps", "libraryfolders.vdf"),
                        Path.Combine(steamPath, "config", "libraryfolders.vdf")
                    };

                    foreach (var vdfPath in possibleVdfPaths)
                    {
                        if (File.Exists(vdfPath))
                        {
                            string content = File.ReadAllText(vdfPath);
                            var matches = Regex.Matches(content, "\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
                            foreach (Match match in matches)
                            {
                                if (match.Groups.Count > 1)
                                {
                                    string libPath = match.Groups[1].Value.Replace("\\\\", "\\");
                                    libraries.Add(libPath);
                                }
                            }
                            break; 
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[WindowsDefenderExclusionService] Error parsing libraryfolders.vdf: {ex.Message}");
                }
            }

            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (drive.IsReady && drive.DriveType == DriveType.Fixed)
                    {
                        string possibleLib = Path.Combine(drive.RootDirectory.FullName, "SteamLibrary");
                        if (Directory.Exists(possibleLib))
                        {
                            libraries.Add(possibleLib);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[WindowsDefenderExclusionService] Error scanning drives for SteamLibrary: {ex.Message}");
            }

            return libraries;
        }

        private static async Task<HashSet<string>> GetCurrentExclusionsAsync()
        {
            var exclusions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -Command \"(Get-MpPreference).ExclusionPath\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process != null)
                {
                    string output = await process.StandardOutput.ReadToEndAsync();
                    await Task.Run(() => process.WaitForExit());

                    if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                    {
                        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var line in lines)
                        {
                            exclusions.Add(line.Trim());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[WindowsDefenderExclusionService] Error getting current exclusions: {ex.Message}");
            }
            return exclusions;
        }

        private static async Task<bool> AddExclusionsAsync(List<string> folders)
        {
            try
            {
                string pathsArg = string.Join(", ", folders.Select(f => $"'{f}'"));
                string command = $"Add-MpPreference -ExclusionPath {pathsArg}";

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"{command}\"",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var process = Process.Start(psi);
                if (process != null)
                {
                    await Task.Run(() => process.WaitForExit());
                    return process.ExitCode == 0;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"[WindowsDefenderExclusionService] Error adding exclusions: {ex.Message}");
                return false;
            }
            return false;
        }
    }
}

