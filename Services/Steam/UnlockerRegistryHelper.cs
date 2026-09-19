using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace SteamPluginManager
{
    public static class UnlockerRegistryHelper
    {
        private const string OpenSteamToolDllFileName = "OpenSteamTool.dll";
        private const string OpenSteamToolBakFileName = "OpenSteamTool.dll.bak";

        public static bool IsUnlockerEnabled()
        {
            try
            {
                string? steamPath = SteamHelper.GetSteamPath();
                if (string.IsNullOrWhiteSpace(steamPath))
                    return false;

                string dllPath = Path.Combine(steamPath, OpenSteamToolDllFileName);
                return File.Exists(dllPath);
            }
            catch (Exception ex)
            {
                Logger.Log($"[Unlocker] Failed to read unlocker file state: {ex.Message}");
                return false;
            }
        }

        public static void SetUnlockerEnabled(bool enabled)
        {
            try
            {
                string? steamPath = SteamHelper.GetSteamPath();
                if (string.IsNullOrWhiteSpace(steamPath))
                {
                    Logger.Log("[Unlocker] Steam path was not found; unable to change unlocker file state.");
                    return;
                }

                string dllPath = Path.Combine(steamPath, OpenSteamToolDllFileName);
                string bakPath = Path.Combine(steamPath, OpenSteamToolBakFileName);

                if (enabled)
                {
                    if (File.Exists(dllPath))
                        return;

                    if (File.Exists(bakPath))
                    {
                        File.Move(bakPath, dllPath);
                        Logger.Log("[Unlocker] Restored OpenSteamTool.dll from backup.");
                    }
                    else
                    {
                        Logger.Log("[Unlocker] OpenSteamTool.dll is not present and no backup file exists to restore.");
                    }

                    return;
                }

                if (File.Exists(dllPath))
                {
                    if (File.Exists(bakPath))
                        File.Delete(bakPath);

                    File.Move(dllPath, bakPath);
                    Logger.Log("[Unlocker] Renamed OpenSteamTool.dll to OpenSteamTool.dll.bak.");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[Unlocker] Failed to update unlocker file state: {ex.Message}");
            }
        }

        public static bool IsOpenSteamToolInstalled()
        {
            try
            {
                string? steamPath = SteamHelper.GetSteamPath();
                if (string.IsNullOrWhiteSpace(steamPath))
                    return false;

                string[] candidateFiles = [OpenSteamToolDllFileName, "dwmapi.dll", "xinput1_4.dll"];
                return candidateFiles.Any(file => File.Exists(Path.Combine(steamPath, file)))
                    || File.Exists(Path.Combine(steamPath, OpenSteamToolBakFileName));
            }
            catch (Exception ex)
            {
                Logger.Log($"[Unlocker] Failed to detect OpenSteamTool installation: {ex.Message}");
                return false;
            }
        }

        public static async Task EnsureOpenSteamToolInstalledAsync()
        {
            try
            {
                if (IsOpenSteamToolInstalled())
                    return;

                await InstallOpenSteamToolSilentlyAsync();
            }
            catch (Exception ex)
            {
                Logger.Log($"[Unlocker] Startup install flow failed: {ex.Message}");
            }
        }

        private static async Task InstallOpenSteamToolSilentlyAsync()
        {
            string? steamPath = SteamHelper.GetSteamPath();
            if (string.IsNullOrWhiteSpace(steamPath))
                throw new InvalidOperationException("Steam install path was not found in registry.");

            StopSteamProcesses();

            var client = SharedHttpClient.Instance;
            const string latestReleaseApi = "https://api.github.com/repos/madoiscool/BetterSteamTools/releases/latest";

            using var request = new HttpRequestMessage(HttpMethod.Get, latestReleaseApi);
            request.Headers.UserAgent.ParseAdd("SteamPluginManager/2.2.2");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var releaseResponse = await client.SendAsync(request);
            releaseResponse.EnsureSuccessStatusCode();

            var releaseJson = await releaseResponse.Content.ReadAsStringAsync();
            using var releaseDoc = JsonDocument.Parse(releaseJson);

            string? assetUrl = null;
            string? assetName = null;
            string? releaseTag = null;
            if (releaseDoc.RootElement.TryGetProperty("tag_name", out var tagElement))
            {
                releaseTag = tagElement.GetString();
            }

            foreach (var asset in releaseDoc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var currentName = asset.GetProperty("name").GetString();
                if (!string.IsNullOrWhiteSpace(currentName) &&
                    currentName.Contains("Release", StringComparison.OrdinalIgnoreCase) &&
                    currentName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    assetUrl = asset.GetProperty("browser_download_url").GetString();
                    assetName = currentName;
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(assetUrl) || string.IsNullOrWhiteSpace(assetName))
                throw new InvalidOperationException("No matching ZIP release asset was found on GitHub.");

            string tempZipPath = Path.Combine(Path.GetTempPath(), $"OpenSteamTool_{Guid.NewGuid():N}.zip");
            string extractDir = Path.Combine(Path.GetTempPath(), $"OpenSteamTool_{Guid.NewGuid():N}");

            try
            {
                using var downloadResponse = await client.GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead);
                downloadResponse.EnsureSuccessStatusCode();

                using (var remoteStream = await downloadResponse.Content.ReadAsStreamAsync())
                using (var localStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await remoteStream.CopyToAsync(localStream);
                }

                Directory.CreateDirectory(extractDir);
                ZipFile.ExtractToDirectory(tempZipPath, extractDir);

                string[] targetFiles = ["OpenSteamTool.dll", "dwmapi.dll", "xinput1_4.dll"];
                foreach (var targetFile in targetFiles)
                {
                    var sourceFile = Directory.GetFiles(extractDir, targetFile, SearchOption.AllDirectories).FirstOrDefault();
                    if (string.IsNullOrWhiteSpace(sourceFile))
                    {
                        Logger.Log($"[Unlocker] Missing file in extracted release: {targetFile}");
                        continue;
                    }

                    string targetPath = Path.Combine(steamPath, targetFile);
                    File.Copy(sourceFile, targetPath, true);
                    Logger.Log($"[Unlocker] Copied {sourceFile} -> {targetPath}");
                }

                string configPath = Path.Combine(steamPath, "config");
                string luaPath = Path.Combine(configPath, "lua");
                Directory.CreateDirectory(luaPath);

                string stplugInPath = Path.Combine(configPath, "stplug-in");
                if (Directory.Exists(stplugInPath))
                {
                    CopyDirectoryContents(stplugInPath, luaPath);
                    Logger.Log($"[Unlocker] Copied contents from {stplugInPath} to {luaPath}");
                }

                string versionToSave = !string.IsNullOrWhiteSpace(releaseTag)
                    ? releaseTag.Trim()
                    : assetName ?? "unknown";
                SaveInstalledVersion(versionToSave);

                Logger.Log($"[Unlocker] OpenSteamTool installed silently from {assetName} with version {versionToSave}");
            }
            finally
            {
                if (File.Exists(tempZipPath))
                    File.Delete(tempZipPath);
                if (Directory.Exists(extractDir))
                    Directory.Delete(extractDir, true);
            }
        }

        private static void StopSteamProcesses()
        {
            try
            {
                var steamProcesses = Process.GetProcessesByName("steam")
                    .Where(p => !string.IsNullOrWhiteSpace(p.ProcessName))
                    .ToList();

                foreach (var process in steamProcesses)
                {
                    try
                    {
                        Logger.Log($"[Unlocker] Stopping Steam process: {process.ProcessName} ({process.Id})");
                        process.Kill(true);
                        process.WaitForExit(10000);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[Unlocker] Failed to stop Steam process {process.Id}: {ex.Message}");
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[Unlocker] Failed to stop Steam processes: {ex.Message}");
            }
        }

        private static void SaveInstalledVersion(string version)
        {
            try
            {
                string appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamPluginManager");
                Directory.CreateDirectory(appDataPath);
                string filePath = Path.Combine(appDataPath, "steamtools_installed_version.txt");
                File.WriteAllText(filePath, version.Trim());
            }
            catch (Exception ex)
            {
                Logger.Log($"[Unlocker] Failed to save installed OpenSteamTool version: {ex.Message}");
            }
        }

        private static void CopyDirectoryContents(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                string destPath = Path.Combine(targetDir, Path.GetFileName(file));
                File.Copy(file, destPath, true);
            }

            foreach (var subDir in Directory.GetDirectories(sourceDir))
            {
                string childTargetDir = Path.Combine(targetDir, Path.GetFileName(subDir));
                CopyDirectoryContents(subDir, childTargetDir);
            }
        }
    }
}
