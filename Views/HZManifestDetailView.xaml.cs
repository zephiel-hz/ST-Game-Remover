#pragma warning disable CS0103 // The name does not exist
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Management;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Diagnostics;
using SteamPluginManager.Models;
using SteamPluginManager.Services.API;
using SteamPluginManager.Services.Parsing;

namespace SteamPluginManager.Views
{
    public partial class HZManifestDetailView : UserControl
    {
        private readonly string _thumbnailCacheFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SteamPluginManager", "ThumbnailCache");

        private ManifestFile? _currentFile;
        private UserSystemSpecs? _localSystemSpecs;
        public ObservableCollection<DLCItem> DLCList { get; } = new ObservableCollection<DLCItem>();

        private sealed record DownloadResult(string Path, bool DirectToLibrary, IEnumerable<string> DownloadedFiles);

        private class UserSystemSpecs
        {
            public string CpuName { get; init; } = "";
            public long RamGb { get; init; }
            public string GpuName { get; init; } = "";
            public string OsName { get; init; } = "";
        }

        private UserSystemSpecs GetLocalSystemSpecs()
        {
            if (_localSystemSpecs is not null)
                return _localSystemSpecs;

            try
            {
                string cpu = "";
                long ramGb = 0;
                string gpu = "";
                string os = "";

                using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
                {
                    foreach (ManagementObject item in searcher.Get())
                    {
                        cpu = item["Name"]?.ToString() ?? cpu;
                        break;
                    }
                }

                using (var searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"))
                {
                    foreach (ManagementObject item in searcher.Get())
                    {
                        if (long.TryParse(item["TotalPhysicalMemory"]?.ToString(), out var total))
                            ramGb = total / 1024 / 1024 / 1024;
                        break;
                    }
                }

                using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
                {
                    foreach (ManagementObject item in searcher.Get())
                    {
                        var name = item["Name"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            gpu = name;
                            break;
                        }
                    }
                }

                using (var searcher = new ManagementObjectSearcher("SELECT Caption FROM Win32_OperatingSystem"))
                {
                    foreach (ManagementObject item in searcher.Get())
                    {
                        os = item["Caption"]?.ToString() ?? os;
                        break;
                    }
                }

                _localSystemSpecs = new UserSystemSpecs
                {
                    CpuName = cpu,
                    RamGb = ramGb,
                    GpuName = gpu,
                    OsName = os
                };

                return _localSystemSpecs;
            }
            catch
            {
                _localSystemSpecs = new UserSystemSpecs();
                return _localSystemSpecs;
            }
        }

        private bool MatchesRam(string text, long ramGb)
        {
            var gbMatch = Regex.Match(text, @"(\d+)\s*GB", RegexOptions.IgnoreCase);
            if (gbMatch.Success && int.TryParse(gbMatch.Groups[1].Value, out var reqGb))
                return ramGb >= reqGb;

            var mbMatch = Regex.Match(text, @"(\d+)\s*MB", RegexOptions.IgnoreCase);
            if (mbMatch.Success && int.TryParse(mbMatch.Groups[1].Value, out var reqMb))
                return ramGb * 1024 >= reqMb;

            return false;
        }

        private bool MatchesCpu(string text, string cpu)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(cpu))
                return false;

            var normalizedCpu = cpu.ToLowerInvariant();
            var requirement = text.ToLowerInvariant();

            if (requirement.Contains("intel") && normalizedCpu.Contains("intel"))
            {
                if (TryExtractCpuSeries(requirement, out var requiredSeries) && TryExtractCpuSeries(normalizedCpu, out var localSeries))
                    return localSeries >= requiredSeries;
                return true;
            }

            if (requirement.Contains("amd") && normalizedCpu.Contains("amd"))
            {
                if (TryExtractCpuSeries(requirement, out var requiredSeries) && TryExtractCpuSeries(normalizedCpu, out var localSeries))
                    return localSeries >= requiredSeries;
                return true;
            }

            if (requirement.Contains("ryzen") && normalizedCpu.Contains("ryzen"))
            {
                if (TryExtractCpuSeries(requirement, out var requiredSeries) && TryExtractCpuSeries(normalizedCpu, out var localSeries))
                    return localSeries >= requiredSeries;
                return true;
            }

            if (requirement.Contains("ghz") || requirement.Contains("mhz"))
                return !string.IsNullOrWhiteSpace(normalizedCpu);

            if (TryExtractCpuSeries(requirement, out var cpuRequirementSeries) && TryExtractCpuSeries(normalizedCpu, out var cpuLocalSeries))
                return cpuLocalSeries >= cpuRequirementSeries;

            return false;
        }

        private bool TryExtractCpuSeries(string text, out int series)
        {
            series = 0;
            var match = Regex.Match(text, @"i([3579])", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out series))
                return true;

            match = Regex.Match(text, @"ryzen\s*([3579])", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out series))
                return true;

            match = Regex.Match(text, @"core\s*2", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                series = 2;
                return true;
            }

            return false;
        }

        private bool MatchesGpu(string text, string gpu)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(gpu))
                return false;

            var normalizedGpu = gpu.ToLowerInvariant();
            var requirement = text.ToLowerInvariant();

            if (TryExtractGpuSeries(requirement, out var requiredSeries, out var requiredBrand) && TryExtractGpuSeries(normalizedGpu, out var localSeries, out var localBrand))
            {
                if (!string.IsNullOrWhiteSpace(requiredBrand) && requiredBrand == localBrand)
                    return localSeries >= requiredSeries;
                return false;
            }

            if (requirement.Contains("directx") && !string.IsNullOrWhiteSpace(normalizedGpu))
                return true;
            if (requirement.Contains("compatible video card") && !string.IsNullOrWhiteSpace(normalizedGpu))
                return true;
            if (requirement.Contains("graphics card") && !string.IsNullOrWhiteSpace(normalizedGpu))
                return true;
            if (requirement.Contains("video card") && !string.IsNullOrWhiteSpace(normalizedGpu))
                return true;

            if (requirement.Contains("geforce") && normalizedGpu.Contains("geforce"))
                return true;
            if (requirement.Contains("radeon") && normalizedGpu.Contains("radeon"))
                return true;
            if (requirement.Contains("intel") && normalizedGpu.Contains("intel"))
                return true;
            if (requirement.Contains("nvidia") && normalizedGpu.Contains("nvidia"))
                return true;

            return false;
        }

        private bool TryExtractGpuSeries(string text, out int series, out string brand)
        {
            series = 0;
            brand = string.Empty;
            var normalizedText = text.ToLowerInvariant();

            var match = Regex.Match(normalizedText, @"(gtx|rtx|nvidia)\s*([0-9]{3,4})", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[2].Value, out series))
            {
                brand = "nvidia";
                return true;
            }

            match = Regex.Match(normalizedText, @"(rx|radeon)\s*([0-9]{3,4})", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[2].Value, out series))
            {
                brand = "amd";
                return true;
            }

            match = Regex.Match(normalizedText, @"vega\s*(\d+)", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out series))
            {
                brand = "amd";
                return true;
            }

            if (normalizedText.Contains("geforce") || normalizedText.Contains("nvidia"))
            {
                brand = "nvidia";
                return false;
            }

            if (normalizedText.Contains("radeon") || normalizedText.Contains("rx") || normalizedText.Contains("vega"))
            {
                brand = "amd";
                return false;
            }

            if (normalizedText.Contains("intel"))
            {
                brand = "intel";
                return false;
            }

            return false;
        }

        private bool MatchesOs(string text, string osName)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(osName))
                return false;

            return text.Contains("windows", StringComparison.OrdinalIgnoreCase) && osName.Contains("windows", StringComparison.OrdinalIgnoreCase);
        }

        private bool HasRequirement(string text, string pattern)
        {
            return Regex.IsMatch(text ?? string.Empty, pattern, RegexOptions.IgnoreCase);
        }

        private void UpdateRequirementStatus(string requirementText, TextBlock icon, TextBlock label, bool isRecommended)
        {
            if (string.IsNullOrWhiteSpace(requirementText))
            {
                icon.Text = "\uE711";
                icon.Foreground = System.Windows.Media.Brushes.Gray;
                label.Text = isRecommended ? "No recommended requirements available" : "No minimum requirements available";
                return;
            }

            var specs = GetLocalSystemSpecs();
            bool ramMatch = !HasRequirement(requirementText, @"ram|memory|\d+\s*gb") || MatchesRam(requirementText, specs.RamGb);
            bool cpuMatch = !HasRequirement(requirementText, @"cpu|processor|intel|amd|ryzen|core") || MatchesCpu(requirementText, specs.CpuName);
            bool gpuMatch = !HasRequirement(requirementText, @"gpu|graphics|video|geforce|radeon|nvidia|intel") || MatchesGpu(requirementText, specs.GpuName);
            bool osMatch = !HasRequirement(requirementText, @"os|operating|windows|linux|mac") || MatchesOs(requirementText, specs.OsName);

            bool meets = ramMatch && cpuMatch && gpuMatch && osMatch;

            Logger.Log($"[HZManifestDetailView] Requirement debug: CPU='{specs.CpuName}' RAM='{specs.RamGb}GB' GPU='{specs.GpuName}' OS='{specs.OsName}' | ram={ramMatch} cpu={cpuMatch} gpu={gpuMatch} os={osMatch} | text='{requirementText.Replace("\r", "").Replace("\n", " ")}'");

            icon.Text = meets ? "\uE73E" : "\uE711";
            icon.Foreground = meets ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.Red;
            label.Text = meets
                ? (isRecommended ? "Likely meets recommended specs" : "Meets minimum requirements")
                : (isRecommended ? "May not meet recommended specs" : "Does not meet minimum requirements");
        }

        private void ShowMoreDlcButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var currentWindow = Window.GetWindow(this);
                if (currentWindow == null)
                    return;

                if (DLCList.Count == 0)
                    return;

                string message = string.Join("\n\n", DLCList.Select(item => $"{item.AppId} - {item.Name}"));
                var dialog = new StyledMessageDialog("All DLC Included", message);
                dialog.Owner = currentWindow;
                dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                Logger.Log($"[HZManifestDetailView] ShowMoreDlcButton_Click error: {ex.Message}");
            }
        }

        public HZManifestDetailView()
        {
            try
            {
                Directory.CreateDirectory(_thumbnailCacheFolder);
                InitializeComponent(); // Initialize XAML UI
                DataContext = this;
            }
            catch (Exception ex)
            {
                Logger.Log($"[HZManifestDetailView] InitializeComponent error: {ex.Message}");
            }
            Loaded += HZManifestDetailView_Loaded;
        }

        private void HZManifestDetailView_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                RegisterTextElements();
                DisplayFileDetails();
            }
            catch (Exception ex)
            {
                Logger.Log($"[HZManifestDetailView] Error: {ex.Message}");
            }
        }

        public void SetFile(ManifestFile file)
        {
            _currentFile = file;
            if (IsLoaded)
            {
                DisplayFileDetails();
            }
        }

        private void RegisterTextElements()
        {
            try
            {
                if (FindName("DetailTitle") is TextBlock detailTitle)
                    detailTitle.Text = "Game Details";
                if (FindName("DetailSubtitle") is TextBlock detailSubtitle)
                    detailSubtitle.Text = "HZ Manifest Information";
                if (FindName("AddToLibraryButton") is Button addToLibraryButton)
                    addToLibraryButton.Content = "Add to Library";
                if (FindName("BackButtonText") is TextBlock backButtonText)
                    backButtonText.Text = "← Back";
            }
            catch (Exception ex)
            {
                Logger.Log($"[HZManifestDetailView] RegisterTextElements error: {ex.Message}");
            }
        }

        private void ShowLoadingState(bool show, string status = "Processing...")
        {
            try
            {
                if (FindName("ProgressOverlay") is Grid overlay)
                {
                    overlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                    Logger.Log($"[HZManifestDetailView] Overlay visibility set to: {overlay.Visibility}");
                }

                if (show && FindName("ProgressStatus") is TextBlock statusText)
                {
                    statusText.Text = status;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[HZManifestDetailView] Error in ShowLoadingState: {ex.Message}");
            }
        }

        private async Task<bool> VerifyFolderAvailabilityAsync(string folderPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folderPath))
                    return false;

                var prefix = folderPath.TrimEnd('/', '\\') + "/";
                var files = await SupabaseConfig.ListBucketFilesAsync(prefix);
                return files.Count > 0;
            }
            catch (Exception ex)
            {
                Logger.Log($"[HZManifestDetailView] Folder availability check error: {ex.Message}");
                return false;
            }
        }

        private string ResolveFolderPath()
        {
            if (_currentFile == null)
                return string.Empty;

            if (!string.IsNullOrWhiteSpace(_currentFile.FolderPath) && !IsBooleanLikeFolderPath(_currentFile.FolderPath))
                return _currentFile.FolderPath.TrimEnd('/', '\\') + "/";

            if (!string.IsNullOrWhiteSpace(_currentFile.FileName) && (_currentFile.FileName.Contains("/") || _currentFile.FileName.Contains("\\")))
            {
                var path = _currentFile.FileName.Replace("\\", "/");
                var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1)
                {
                    return string.Join("/", parts.Take(parts.Length - 1)) + "/";
                }
            }

            return _currentFile.AppId.HasValue ? $"{_currentFile.AppId.Value}/" : string.Empty;
        }

        private static bool IsBooleanLikeFolderPath(string path)
        {
            return string.Equals(path?.Trim(), "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(path?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<DownloadResult> DownloadFolderStorageAsync(string folderPath, int appId)
        {
            try
            {
                var files = await SupabaseConfig.ListBucketFilesAsync(folderPath);
                if (files.Count == 0)
                    throw new Exception($"No files found in folder path: {folderPath}");

                var httpClient = SharedHttpClient.Instance;
                if (!httpClient.DefaultRequestHeaders.Contains("User-Agent"))
                    httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");

                bool directLibrarySave = files.All(file =>
                    file.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase));

                var downloadedFiles = new List<string>();
                string resultPath;

                if (directLibrarySave)
                {
                    string steamPath = MainWindow.GetSteamPath();
                    if (string.IsNullOrEmpty(steamPath))
                        throw new Exception("Steam path not found for direct folder download");

                    string pluginPath = Path.Combine(steamPath, "config", "stplug-in");
                    string luaPath = Path.Combine(steamPath, "config", "lua");
                    string depotCachePath = Path.Combine(steamPath, "depotcache");
                    string depotCachePathOld = Path.Combine(steamPath, "config", "depotcache");

                    Directory.CreateDirectory(pluginPath);
                    Directory.CreateDirectory(luaPath);
                    Directory.CreateDirectory(depotCachePath);
                    Directory.CreateDirectory(depotCachePathOld);

                    foreach (var objectName in files)
                    {
                        var encodedName = string.Join("/", objectName.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(Uri.EscapeDataString));
                        var fileUrl = $"{SupabaseConfig.SupabaseUrl}/storage/v1/object/public/{SupabaseConfig.StorageBucketName}/{encodedName}";

                        string relativePath = objectName;
                        if (relativePath.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase))
                            relativePath = relativePath.Substring(folderPath.Length);

                        relativePath = relativePath.TrimStart('/');
                        var extension = Path.GetExtension(relativePath).ToLowerInvariant();

                        List<string> targetPaths = new();
                        if (extension == ".lua")
                        {
                            targetPaths.Add(Path.Combine(pluginPath, $"{appId}.lua"));
                            targetPaths.Add(Path.Combine(luaPath, $"{appId}.lua"));
                        }
                        else if (extension == ".manifest")
                        {
                            string fileName = Path.GetFileName(relativePath);
                            targetPaths.Add(Path.Combine(depotCachePath, fileName));
                            targetPaths.Add(Path.Combine(depotCachePathOld, fileName));
                        }
                        else
                        {
                            throw new Exception($"Unsupported direct download file type: {relativePath}");
                        }

                        foreach (var destinationPath in targetPaths)
                        {
                            string destinationDir = Path.GetDirectoryName(destinationPath) ?? Path.GetDirectoryName(destinationPath) ?? pluginPath;
                            if (!Directory.Exists(destinationDir))
                                Directory.CreateDirectory(destinationDir);

                            Logger.Log($"[HZManifestDetailView] Downloading folder file: {fileUrl} -> {destinationPath}");
                        var response = await httpClient.GetAsync(fileUrl);
                        if (!response.IsSuccessStatusCode)
                            throw new Exception($"Failed to download {fileUrl}: HTTP {(int)response.StatusCode}");

                        long? contentLength = response.Content.Headers.ContentLength;
                        Logger.Log($"[HZManifestDetailView] Response content length: {contentLength?.ToString() ?? "unknown"}");

                        var contentBytes = await response.Content.ReadAsByteArrayAsync();
                        Logger.Log($"[HZManifestDetailView] Actual bytes received: {contentBytes.Length}");
                        if (contentBytes.Length == 0)
                            throw new Exception($"Downloaded file is empty before writing: {destinationPath}");

                        await File.WriteAllBytesAsync(destinationPath, contentBytes);
                        var fileInfo = new FileInfo(destinationPath);
                        if (fileInfo.Length == 0)
                            throw new Exception($"Downloaded file is empty after writing: {destinationPath}");

                        downloadedFiles.Add(destinationPath);
                        }
                    }

                    resultPath = Path.Combine(MainWindow.GetSteamPath() ?? string.Empty, "config", "stplug-in");
                    return new DownloadResult(resultPath, true, downloadedFiles);
                }

                string tempFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SteamPluginManager", "Downloads", appId.ToString());

                if (Directory.Exists(tempFolder))
                    Directory.Delete(tempFolder, true);

                Directory.CreateDirectory(tempFolder);

                foreach (var objectName in files)
                {
                    var encodedName = string.Join("/", objectName.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(Uri.EscapeDataString));
                    var fileUrl = $"{SupabaseConfig.SupabaseUrl}/storage/v1/object/public/{SupabaseConfig.StorageBucketName}/{encodedName}";

                    string relativePath = objectName;
                    if (relativePath.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase))
                        relativePath = relativePath.Substring(folderPath.Length);

                    relativePath = relativePath.TrimStart('/');
                    string destinationPath = Path.Combine(tempFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));
                    string destinationDir = Path.GetDirectoryName(destinationPath) ?? tempFolder;
                    if (!Directory.Exists(destinationDir))
                        Directory.CreateDirectory(destinationDir);

                    Logger.Log($"[HZManifestDetailView] Downloading folder file: {fileUrl} -> {destinationPath}");

                    var response = await httpClient.GetAsync(fileUrl);
                    if (!response.IsSuccessStatusCode)
                        throw new Exception($"Failed to download {fileUrl}: HTTP {(int)response.StatusCode}");

                    long? contentLength = response.Content.Headers.ContentLength;
                    Logger.Log($"[HZManifestDetailView] Response content length: {contentLength?.ToString() ?? "unknown"}");

                    var contentBytes = await response.Content.ReadAsByteArrayAsync();
                    Logger.Log($"[HZManifestDetailView] Actual bytes received: {contentBytes.Length}");
                    if (contentBytes.Length == 0)
                        throw new Exception($"Downloaded file is empty before writing: {destinationPath}");

                    await File.WriteAllBytesAsync(destinationPath, contentBytes);
                    var fileInfo = new FileInfo(destinationPath);
                    if (fileInfo.Length == 0)
                        throw new Exception($"Downloaded file is empty after writing: {destinationPath}");

                    downloadedFiles.Add(destinationPath);
                }

                return new DownloadResult(tempFolder, false, downloadedFiles);
            }
            catch (Exception ex)
            {
                Logger.Log($"[HZManifestDetailView] Folder download error: {ex.Message}");
                throw;
            }
        }

        private async Task StoreDlcDataAsync(string extractedFolderPath, int gameAppId, IEnumerable<string>? downloadedFiles = null)
        {
            try
            {
                string[] luaFiles;
                if (downloadedFiles != null)
                {
                    luaFiles = downloadedFiles
                        .Where(path => path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                }
                else
                {
                    luaFiles = Directory.GetFiles(extractedFolderPath, "*.lua", SearchOption.AllDirectories);
                }

                if (luaFiles.Length == 0)
                {
                    Logger.Log("[HZManifestDetailView] No lua file found for DLC parsing");
                    return;
                }

                string luaContent = await File.ReadAllTextAsync(luaFiles[0]);
                var dlcEntries = LuaManifestParser.ExtractDlcEntries(luaContent);
                if (dlcEntries.Count == 0)
                {
                    Logger.Log("[HZManifestDetailView] No DLC app IDs found in lua file");
                    return;
                }

                var dlcAppIds = dlcEntries.Select(e => e.AppId).ToList();
                var dlcNames = await SteamDlcNameFetcher.FetchDlcNamesAsync(dlcAppIds);
                var dlcItems = new List<DLCItem>();
                foreach (var entry in dlcEntries)
                {
                    var dlcName = !string.IsNullOrWhiteSpace(entry.Name)
                        ? entry.Name
                        : dlcNames.ContainsKey(entry.AppId)
                            ? dlcNames[entry.AppId]
                            : $"DLC {entry.AppId}";

                    dlcItems.Add(new DLCItem
                    {
                        AppId = entry.AppId,
                        Name = dlcName,
                        FetchDate = DateTime.UtcNow
                    });
                }

                if (dlcItems.Count > 0)
                {
                    await SupabaseDlcService.UpsertDlcItemsAsync(gameAppId, dlcItems);
                    Logger.Log($"[HZManifestDetailView] Stored {dlcItems.Count} DLC items for game {gameAppId}");
                    await LoadDlcListAsync();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[HZManifestDetailView] StoreDlcDataAsync error: {ex.Message}");
            }
        }

        private async Task LoadDlcListAsync()
        {
            try
            {
                if (_currentFile?.AppId == null)
                    return;

                var statusText = FindName("DlcStatusText") as TextBlock;
                var emptyText = FindName("DlcEmptyText") as TextBlock;
                var previewItemsControl = FindName("DlcPreviewItemsControl") as ItemsControl;
                var showMoreButton = FindName("ShowMoreDlcButton") as Button;

                if (statusText != null)
                {
                    statusText.Text = "Loading DLC list...";
                    statusText.Visibility = Visibility.Visible;
                }

                if (emptyText != null)
                    emptyText.Visibility = Visibility.Collapsed;

                var dlcItems = await SupabaseDlcService.GetDlcItemsAsync(_currentFile.AppId.Value);
                DLCList.Clear();

                foreach (var item in dlcItems)
                    DLCList.Add(item);

                if (previewItemsControl != null)
                {
                    previewItemsControl.ItemsSource = DLCList.Take(3).ToList();
                }

                if (showMoreButton != null)
                {
                    showMoreButton.Visibility = dlcItems.Count > 3 ? Visibility.Visible : Visibility.Collapsed;
                }

                if (dlcItems.Count == 0)
                {
                    if (emptyText != null)
                        emptyText.Visibility = Visibility.Visible;
                }

                if (statusText != null)
                    statusText.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Logger.Log($"[HZManifestDetailView] LoadDlcListAsync error: {ex.Message}");
            }
        }

        private void ShowResultNotification(bool isSuccess, string message)
        {
            try
            {
                if (FindName("ResultNotification") is Border resultBorder)
                {
                    resultBorder.Visibility = Visibility.Visible;
                    resultBorder.BorderBrush = FindResource(isSuccess ? "SuccessButtonBrush" : "DangerButtonBrush") as System.Windows.Media.Brush
                        ?? (isSuccess ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.Red);
                }

                if (FindName("ResultIcon") is TextBlock resultIcon)
                    resultIcon.Text = isSuccess ? "\uE73E" : "\uE711";

                if (FindName("ResultMessage") is TextBlock resultMsg)
                    resultMsg.Text = message;

                if (FindName("ProgressBar") is ProgressBar progressBar)
                    progressBar.Visibility = Visibility.Collapsed;
                if (FindName("StepsPanel") is StackPanel stepsPanel)
                    stepsPanel.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Logger.Log($"[HZManifestDetailView] Error showing result notification: {ex.Message}");
            }
        }

        private void UpdateProgressStep(int stepNumber, bool completed = false)
        {
            try
            {
                string[] stepIcons = new[] { "Step1Icon", "Step2Icon", "Step3Icon", "Step4Icon" };
                
                for (int i = 1; i <= 4; i++)
                {
                    if (FindName(stepIcons[i - 1]) is TextBlock stepIcon)
                    {
                        if (i < stepNumber)
                        {
                            // Completed steps show checkmark
                            stepIcon.Text = "\uE73E"; // Icon.Check
                            stepIcon.Foreground = FindResource("SuccessButtonBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Green;
                        }
                        else if (i == stepNumber && stepNumber <= 4)
                        {
                            // Current step shows spinner
                            stepIcon.Text = "\uE7BA"; // Spinner icon
                            stepIcon.Foreground = FindResource("PrimaryButtonBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;
                        }
                        else
                        {
                            // Future steps show empty circle
                            stepIcon.Text = "\uE7BA";
                            stepIcon.Foreground = FindResource("MutedForegroundBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.LightGray;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[HZManifestDetailView] Error updating progress step: {ex.Message}");
            }
        }

        private void ConfigureOverlayForAddToLibrary()
        {
            try
            {
                if (FindName("ProgressTitle") is TextBlock title)
                    title.Text = "Adding to Library...";
                if (FindName("Step1Text") is TextBlock step1)
                    step1.Text = "Verifying file availability...";
                if (FindName("Step2Text") is TextBlock step2)
                    step2.Text = "Downloading file...";
                if (FindName("Step3Text") is TextBlock step3)
                    step3.Text = "Extracting files...";
                if (FindName("Step4Text") is TextBlock step4)
                    step4.Text = "Moving to library...";
            }
            catch (Exception ex)
            {
                Logger.Log($"[HZManifestDetailView] Error configuring overlay for add-to-library: {ex.Message}");
            }
        }

        private void ConfigureOverlayForRestartSteam()
        {
            try
            {
                if (FindName("ProgressTitle") is TextBlock title)
                    title.Text = "Restarting Steam...";
                if (FindName("Step1Text") is TextBlock step1)
                    step1.Text = "Closing Steam...";
                if (FindName("Step2Text") is TextBlock step2)
                    step2.Text = "Starting Steam...";
                if (FindName("Step3Text") is TextBlock step3)
                    step3.Text = "Waiting for Steam startup...";
                if (FindName("Step4Text") is TextBlock step4)
                    step4.Text = "Finalizing restart...";
            }
            catch (Exception ex)
            {
                Logger.Log($"[HZManifestDetailView] Error configuring overlay for restart Steam: {ex.Message}");
            }
        }

        private void DisplayFileDetails()
        {
            try
            {
                if (_currentFile == null)
                {
                    MessageBox.Show("No file selected", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Display Thumbnail (async)
                _ = LoadBannerAsync();

                // Display Game Name (not file name)
                if (FindName("GameNameDetail") is TextBlock gameNameDetail)
                    gameNameDetail.Text = _currentFile.Name ?? "Unknown Game";
                
                // Display Genre
                if (FindName("GenreDetail") is TextBlock genreDetail)
                    genreDetail.Text = _currentFile.Genre ?? "N/A";

                // Display Download Count
                if (FindName("DownloadCountDetailText") is TextBlock downloadCountText)
                    downloadCountText.Text = $"Downloads: {_currentFile.DownloadCount ?? 0}";
                
                // Display Description (full)
                if (FindName("DescriptionDetail") is TextBlock descriptionDetail)
                    descriptionDetail.Text = _currentFile.Description ?? "No description available";
                
                // Display Minimum Requirements
                if (!string.IsNullOrWhiteSpace(_currentFile.MinimumRequirements))
                {
                    if (FindName("MinimumRequirementsDetail") is TextBlock minReqDetail)
                        minReqDetail.Text = _currentFile.MinimumRequirements;
                    if (FindName("MinRequirementsBorder") is Border minReqBorder)
                        minReqBorder.Visibility = Visibility.Visible;
                    if (FindName("MinRequirementsEmpty") is TextBlock minReqEmpty)
                        minReqEmpty.Visibility = Visibility.Collapsed;
                }
                
                if (FindName("MinRequirementsStatusIcon") is TextBlock minStatusIcon &&
                    FindName("MinRequirementsStatusText") is TextBlock minStatusText)
                {
                    UpdateRequirementStatus(_currentFile.MinimumRequirements, minStatusIcon, minStatusText, false);
                }
                
                // Display Recommended Requirements
                if (!string.IsNullOrWhiteSpace(_currentFile.RecommendedRequirements))
                {
                    if (FindName("RecommendedRequirementsDetail") is TextBlock recReqDetail)
                        recReqDetail.Text = _currentFile.RecommendedRequirements;
                    if (FindName("RecRequirementsBorder") is Border recReqBorder)
                        recReqBorder.Visibility = Visibility.Visible;
                    if (FindName("RecRequirementsEmpty") is TextBlock recReqEmpty)
                        recReqEmpty.Visibility = Visibility.Collapsed;
                }
                
                if (FindName("RecRequirementsStatusIcon") is TextBlock recStatusIcon &&
                    FindName("RecRequirementsStatusText") is TextBlock recStatusText)
                {
                    UpdateRequirementStatus(_currentFile.RecommendedRequirements, recStatusIcon, recStatusText, true);
                }

                // Check if this game is already in user's library
                UpdateAddToLibraryButton();

                _ = LoadDlcListAsync();

                _ = LoadDlcListAsync();
            }
            catch (Exception ex)
            {
                Logger.Log($"[HZManifestDetailView] Error displaying details: {ex.Message}");
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task LoadBannerAsync()
        {
            try
            {
                if (_currentFile == null)
                {
                    Logger.Log("[HZManifestDetailView] CurrentFile is null");
                    if (FindName("ThumbnailBorder") is Border thumbnailBorder)
                        thumbnailBorder.Visibility = Visibility.Collapsed;
                    return;
                }

                if (_currentFile.AppId == null || _currentFile.AppId == 0)
                {
                    Logger.Log($"[HZManifestDetailView] Invalid AppId: {_currentFile.AppId}");
                    if (FindName("ThumbnailBorder") is Border thumbnailBorder)
                        thumbnailBorder.Visibility = Visibility.Collapsed;
                    return;
                }

                Logger.Log($"[HZManifestDetailView] Starting banner load for AppID: {_currentFile.AppId}");

                string cacheFileName = $"{_currentFile.AppId}_detail.jpg";
                string cachedPath = Path.Combine(_thumbnailCacheFolder, cacheFileName);

                if (File.Exists(cachedPath))
                {
                    try
                    {
                        var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        bitmap.UriSource = new Uri(cachedPath, UriKind.Absolute);
                        bitmap.EndInit();

                        Dispatcher.Invoke(() =>
                        {
                            if (FindName("GameThumbnail") is Image thumbnailImg)
                            {
                                thumbnailImg.Source = bitmap;
                                if (FindName("ThumbnailBorder") is Border thumbnailBorder)
                                {
                                    thumbnailBorder.Visibility = Visibility.Visible;
                                    Logger.Log($"[HZManifestDetailView] ✓ Loaded detail image from cache: {cachedPath}");
                                }
                            }
                        });

                        return;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[HZManifestDetailView] Failed to load cached detail image: {ex.Message}");
                    }
                }

                Logger.Log($"[HZManifestDetailView] Detail cache miss, fetching high-resolution image for AppID: {_currentFile.AppId}");

                // Try multiple banner formats in order of preference
                string[] bannerFormats = new[]
                {
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{_currentFile.AppId}/header_1840x620.jpg", // Extra large landscape
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{_currentFile.AppId}/header_920x430.jpg",  // Large landscape
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{_currentFile.AppId}/header_616x353.jpg",  // Medium landscape
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{_currentFile.AppId}/header.jpg",           // Full header
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{_currentFile.AppId}/capsule_467x181.jpg",  // Medium capsule (fallback)
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{_currentFile.AppId}/capsule.jpg"          // Full capsule
                };

                using (var httpClient = new HttpClient())
                {
                    httpClient.Timeout = TimeSpan.FromSeconds(10);
                    httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
                    
                    byte[]? imageData = null;
                    string? successUrl = null;

                    // Try each banner format
                    foreach (var bannerUrl in bannerFormats)
                    {
                        try
                        {
                            Logger.Log($"[HZManifestDetailView] Trying banner URL: {bannerUrl}");
                            var response = await httpClient.GetAsync(bannerUrl);
                            
                            Logger.Log($"[HZManifestDetailView] HTTP Response: {(int)response.StatusCode}");

                            if (response.IsSuccessStatusCode)
                            {
                                imageData = await response.Content.ReadAsByteArrayAsync();
                                successUrl = bannerUrl;
                                Logger.Log($"[HZManifestDetailView] ✓ Successfully found banner at: {bannerUrl} ({imageData.Length} bytes)");
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[HZManifestDetailView] Skipping {bannerUrl}: {ex.Message}");
                        }
                    }

                    // If we got image data, display it
                    if (imageData != null && imageData.Length > 0)
                    {
                        try
                        {
                            try
                            {
                                await File.WriteAllBytesAsync(cachedPath, imageData);
                                Logger.Log($"[HZManifestDetailView] Saved banner to cache: {cachedPath}");
                            }
                            catch (Exception cacheEx)
                            {
                                Logger.Log($"[HZManifestDetailView] Failed to save banner cache: {cacheEx.Message}");
                            }

                            // Create BitmapImage from byte array
                            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                            bitmap.BeginInit();
                            bitmap.StreamSource = new MemoryStream(imageData);
                            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                            bitmap.EndInit();
                            
                            Logger.Log("[HZManifestDetailView] BitmapImage created, updating UI...");

                            // Set image source on UI thread
                            Dispatcher.Invoke(() =>
                            {
                                if (FindName("GameThumbnail") is Image thumbnailImg)
                                {
                                    thumbnailImg.Source = bitmap;
                                    if (FindName("ThumbnailBorder") is Border thumbnailBorder)
                                    {
                                        thumbnailBorder.Visibility = Visibility.Visible;
                                        Logger.Log("[HZManifestDetailView] ✓ Banner border set to visible");
                                    }
                                }
                            });

                            Logger.Log($"[HZManifestDetailView] ✓ Successfully loaded banner for {_currentFile.Name} (AppID: {_currentFile.AppId})");
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[HZManifestDetailView] ✗ Error creating/displaying BitmapImage: {ex.Message}");
                            if (FindName("ThumbnailBorder") is Border thumbnailBorder)
                                thumbnailBorder.Visibility = Visibility.Collapsed;
                        }
                    }
                    else
                    {
                        Logger.Log($"[HZManifestDetailView] ✗ No banner found for AppID {_currentFile.AppId} (tried all formats). Trying store page scrape...");
                        // Try scraping the store page for og:image
                        try
                        {
                            string storeUrl = $"https://store.steampowered.com/app/{_currentFile.AppId}";
                            var resp = await httpClient.GetAsync(storeUrl);
                            if (resp.IsSuccessStatusCode)
                            {
                                var html = await resp.Content.ReadAsStringAsync();
                                var m = System.Text.RegularExpressions.Regex.Match(html, "<meta[^>]*property=[\"']og:image[\"'][^>]*content=[\"']([^\"']+)[\"']", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                if (!m.Success)
                                {
                                    m = System.Text.RegularExpressions.Regex.Match(html, "<meta[^>]*content=[\"']([^\"']+)[\"'][^>]*property=[\"']og:image[\"']", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                }

                                if (m.Success && Uri.IsWellFormedUriString(m.Groups[1].Value, UriKind.Absolute))
                                {
                                    var assetUrl = m.Groups[1].Value;
                                    Logger.Log($"[HZManifestDetailView] Found store asset URL: {assetUrl}");
                                    try
                                    {
                                        using var cts2 = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(6));
                                        var assetResp = await httpClient.GetAsync(assetUrl, cts2.Token);
                                        if (assetResp.IsSuccessStatusCode)
                                        {
                                            var bytes = await assetResp.Content.ReadAsByteArrayAsync();
                                            if (bytes != null && bytes.Length > 0)
                                            {
                                                try
                                                {
                                                    await File.WriteAllBytesAsync(cachedPath, bytes);
                                                    Logger.Log($"[HZManifestDetailView] Saved store asset banner to cache: {cachedPath}");
                                                }
                                                catch (Exception cacheEx)
                                                {
                                                    Logger.Log($"[HZManifestDetailView] Failed to save store asset banner cache: {cacheEx.Message}");
                                                }

                                                // Create BitmapImage from byte array
                                                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                                                bitmap.BeginInit();
                                                bitmap.StreamSource = new MemoryStream(bytes);
                                                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                                                bitmap.EndInit();

                                                Dispatcher.Invoke(() =>
                                                {
                                                    if (FindName("GameThumbnail") is Image thumbnailImg)
                                                    {
                                                        thumbnailImg.Source = bitmap;
                                                        if (FindName("ThumbnailBorder") is Border thumbnailBorder)
                                                            thumbnailBorder.Visibility = Visibility.Visible;
                                                    }
                                                });

                                                Logger.Log($"[HZManifestDetailView] Downloaded banner from store asset for {_currentFile.Name} (AppID: {_currentFile.AppId})");
                                            }
                                        }
                                        else
                                        {
                                            Logger.Log($"[HZManifestDetailView] Failed to download store asset URL: HTTP {(int)assetResp.StatusCode}");
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Logger.Log($"[HZManifestDetailView] Error downloading store asset URL for AppID {_currentFile.AppId}: {ex.Message}");
                                    }
                                }
                                else
                                {
                                    Logger.Log($"[HZManifestDetailView] No og:image found on store page for AppID {_currentFile.AppId}");
                                    if (FindName("ThumbnailBorder") is Border thumbnailBorder)
                                        thumbnailBorder.Visibility = Visibility.Collapsed;
                                }
                            }
                            else
                            {
                                Logger.Log($"[HZManifestDetailView] Failed to fetch store page for AppID {_currentFile.AppId}: HTTP {(int)resp.StatusCode}");
                                if (FindName("ThumbnailBorder") is Border thumbnailBorder)
                                    thumbnailBorder.Visibility = Visibility.Collapsed;
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[HZManifestDetailView] Error scraping store page for AppID {_currentFile.AppId}: {ex.Message}");
                            if (FindName("ThumbnailBorder") is Border thumbnailBorder)
                                thumbnailBorder.Visibility = Visibility.Collapsed;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[HZManifestDetailView] ✗ Error in LoadBannerAsync: {ex.Message}");
                Logger.Log($"[HZManifestDetailView] Stack trace: {ex.StackTrace}");
                if (FindName("ThumbnailBorder") is Border thumbnailBorder)
                    thumbnailBorder.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateAddToLibraryButton()
        {
            try
            {
                if (_currentFile == null || _currentFile.AppId == null || _currentFile.AppId == 0)
                    return;

                // Check if lua file exists in st-plugin folder
                bool isInLibrary = CheckLuaFileExists(_currentFile.AppId.Value);

                if (FindName("AddToLibraryButton") is Button addBtn)
                {
                    if (isInLibrary)
                    {
                        // Lua file already exists - disable button, change text, and set checkmark icon
                        addBtn.Content = "On Your Library";
                        addBtn.Tag = FindResource("Icon.Check");
                        addBtn.IsEnabled = false;
                        Logger.Log($"[HZManifestDetailView] Lua file for {_currentFile.Name} (AppID: {_currentFile.AppId}) found in st-plugin folder");
                    }
                    else
                    {
                        // Lua file not found - enable button and set plus icon
                        addBtn.Content = "Add to Library";
                        addBtn.Tag = FindResource("Icon.Plus");
                        addBtn.IsEnabled = true;
                    }
                }

                // Show/hide Delete Files button based on lua file existence
                if (FindName("DeleteFilesButton") is Button deleteBtn)
                {
                    if (isInLibrary)
                    {
                        // Lua file exists - show delete button
                        deleteBtn.Visibility = Visibility.Visible;
                        Logger.Log($"[HZManifestDetailView] Showing Delete Files button for {_currentFile.Name}");
                    }
                    else
                    {
                        // Lua file not found - hide delete button
                        deleteBtn.Visibility = Visibility.Collapsed;
                        Logger.Log($"[HZManifestDetailView] Hiding Delete Files button for {_currentFile.Name}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[HZManifestDetailView] Error updating button state: {ex.Message}");
            }
        }

        private bool CheckLuaFileExists(int appId)
        {
            try
            {
                // Get Steam path - usually at Program Files/Steam or user defined
                string steamPath = MainWindow.GetSteamPath();
                if (string.IsNullOrEmpty(steamPath))
                {
                    Logger.Log("[HZManifestDetailView] Steam path not found");
                    return false;
                }

                // Build path to st-plugin folder
                string pluginPath = System.IO.Path.Combine(steamPath, "config", "stplug-in");
                if (!Directory.Exists(pluginPath))
                {
                    Logger.Log($"[HZManifestDetailView] Plugin folder not found at {pluginPath}");
                    return false;
                }

                // Check if lua file with AppId name exists (e.g., "123456.lua")
                string luaFilePath = System.IO.Path.Combine(pluginPath, $"{appId}.lua");
                bool exists = File.Exists(luaFilePath);
                
                Logger.Log($"[HZManifestDetailView] Checking lua file: {luaFilePath} - {(exists ? "FOUND" : "NOT FOUND")}");
                return exists;
            }
            catch (Exception ex)
            {
                Logger.Log($"[HZManifestDetailView] Error checking lua file: {ex.Message}");
                return false;
            }
        }

        private async void AddToLibrary_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_currentFile == null)
                {
                    MessageBox.Show("No file selected", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Validate file data
                if (_currentFile.AppId == null || _currentFile.AppId == 0)
                {
                    ShowLoadingState(false);
                    Logger.Log($"[HZManifestDetailView] AppId is invalid for {_currentFile.Name}");
                    if (FindName("AddToLibraryButton") is Button btn2)
                        btn2.IsEnabled = true;
                    return;
                }

                if (!_currentFile.IsFolderStorage && string.IsNullOrEmpty(_currentFile.FileUrl))
                {
                    ShowLoadingState(false);
                    Logger.Log($"[HZManifestDetailView] FileUrl is empty for {_currentFile.Name} and storage type is not folder");
                    if (FindName("AddToLibraryButton") is Button btn)
                        btn.IsEnabled = true;
                    return;
                }

                if (_currentFile.IsFolderStorage && string.IsNullOrWhiteSpace(_currentFile.FolderPath) && string.IsNullOrWhiteSpace(_currentFile.FileName))
                {
                    ShowLoadingState(false);
                    Logger.Log($"[HZManifestDetailView] Folder storage manifest is missing folder path and filename: {_currentFile.Name}");
                    if (FindName("AddToLibraryButton") is Button btn)
                        btn.IsEnabled = true;
                    return;
                }

                // Disable button during operation
                if (FindName("AddToLibraryButton") is Button addBtn)
                    addBtn.IsEnabled = false;

                // Show loading indicator
                ConfigureOverlayForAddToLibrary();
                ShowLoadingState(true, "Initializing...");
                UpdateProgressStep(1);

                Logger.Log($"[HZManifestDetailView] Starting add to library process for: {_currentFile.Name} (AppID: {_currentFile.AppId})");
                Logger.Log($"[HZManifestDetailView] StorageType: '{_currentFile.StorageType}', FolderPath raw: '{_currentFile.FolderPath}', FileName: '{_currentFile.FileName}'");
                
                // Pre-check: Verify file or folder storage is accessible before starting download
                Logger.Log($"[HZManifestDetailView] Pre-check: Verifying file availability...");
                ShowLoadingState(true, "Verifying file availability...");
                bool storageAccessible;
                if (_currentFile.IsFolderStorage)
                {
                    string folderPath = ResolveFolderPath();
                    Logger.Log($"[HZManifestDetailView] Folder storage detected. Path: {folderPath}");
                    storageAccessible = await VerifyFolderAvailabilityAsync(folderPath);
                }
                else
                {
                    storageAccessible = await VerifyUrlAsync(_currentFile.FileUrl);
                }
                
                if (!storageAccessible)
                {
                    Logger.Log($"[HZManifestDetailView] File storage is not accessible: {_currentFile.FileUrl}");
                    
                    // Show error notification
                    ShowResultNotification(false, $"✗ File not available in storage.\n\nPlease contact administrator.");
                    await Task.Delay(1500);
                    
                    // Hide loading state and re-enable button on error
                    ShowLoadingState(false);
                    if (FindName("AddToLibraryButton") is Button btn3)
                        btn3.IsEnabled = true;
                    Logger.Log($"[HZManifestDetailView] ✓ Overlay hidden on URL verification error");
                    return;
                }

                Logger.Log($"[HZManifestDetailView] ✓ URL is accessible, proceeding with download");
                UpdateProgressStep(2);
                
                // Step 2: Download file or folder contents
                string downloadedFilePath;
                IEnumerable<string>? downloadedFiles = null;
                bool directLibrarySave = false;

                if (_currentFile.IsFolderStorage)
                {
                    string folderPath = ResolveFolderPath();
                    Logger.Log($"[HZManifestDetailView] Step 2: Downloading folder contents from {folderPath}");
                    ShowLoadingState(true, $"Downloading files for AppID {_currentFile.AppId}...");
                    var downloadResult = await DownloadFolderStorageAsync(folderPath, _currentFile.AppId.Value);
                    downloadedFilePath = downloadResult.Path;
                    downloadedFiles = downloadResult.DownloadedFiles;
                    directLibrarySave = downloadResult.DirectToLibrary;
                }
                else
                {
                    Logger.Log($"[HZManifestDetailView] Step 2: Downloading from {_currentFile.FileUrl}");
                    ShowLoadingState(true, $"Downloading '{_currentFile.FileName}'...");
                    downloadedFilePath = await DownloadFileAsync(_currentFile.FileUrl, _currentFile.FileName);
                }
                
                if (string.IsNullOrEmpty(downloadedFilePath) || !Directory.Exists(downloadedFilePath) && !File.Exists(downloadedFilePath))
                {
                    Logger.Log($"[HZManifestDetailView] Download failed for {_currentFile.Name}");
                    Logger.Log($"[HZManifestDetailView] Download failed for {_currentFile.Name}");
                    ShowResultNotification(false, "✗ Failed to download file.\n\nPlease try again.");
                    await Task.Delay(1500);
                    ShowLoadingState(false);
                    if (FindName("AddToLibraryButton") is Button btn) btn.IsEnabled = true;
                    return;
                }
                Logger.Log($"[HZManifestDetailView] ✓ Downloaded to: {downloadedFilePath}");
                UpdateProgressStep(3);

                // Step 3: Extract file or folder contents
                Logger.Log($"[HZManifestDetailView] Step 3: Extracting file");
                ShowLoadingState(true, "Extracting files...");
                string extractedFolderPath = await ExtractFileAsync(downloadedFilePath);
                
                if (string.IsNullOrEmpty(extractedFolderPath) || !Directory.Exists(extractedFolderPath))
                {
                    Logger.Log($"[HZManifestDetailView] Extraction failed for {_currentFile.Name}");
                    ShowResultNotification(false, "✗ Failed to extract file.\n\nFile may be corrupted.");
                    await Task.Delay(1500);
                    ShowLoadingState(false);
                    if (FindName("AddToLibraryButton") is Button btn) btn.IsEnabled = true;
                    return;
                }
                Logger.Log($"[HZManifestDetailView] ✓ Extracted to: {extractedFolderPath}");
                UpdateProgressStep(4);

                // Step 3.5: Parse DLC data from lua file and store to database
                Logger.Log($"[HZManifestDetailView] Step 3.5: Parsing DLC data from lua file");
                ShowLoadingState(true, "Parsing DLC data...");
                try
                {
                    await StoreDlcDataAsync(extractedFolderPath, _currentFile.AppId.Value, downloadedFiles);
                }
                catch (Exception ex)
                {
                    Logger.Log($"[HZManifestDetailView] DLC parsing/storage warning: {ex.Message}");
                }

                // Step 4: Move to st-plugin folder if needed
                bool moveSuccess = true;
                if (!directLibrarySave)
                {
                    Logger.Log($"[HZManifestDetailView] Step 4: Moving to Steam st-plugin folder");
                    ShowLoadingState(true, "Moving to library...");
                    moveSuccess = await MoveToLibraryAsync(extractedFolderPath, _currentFile.AppId.Value);
                }
                else
                {
                    Logger.Log($"[HZManifestDetailView] Direct library save completed, skipping MoveToLibraryAsync");
                }
                
                if (!moveSuccess)
                {
                    Logger.Log($"[HZManifestDetailView] Move failed for {_currentFile.Name}");
                    ShowResultNotification(false, "✗ Failed to move files to library.\n\nCheck Steam path settings.");
                    await Task.Delay(1500);
                    ShowLoadingState(false);
                    if (FindName("AddToLibraryButton") is Button btn) btn.IsEnabled = true;
                    return;
                }
                Logger.Log($"[HZManifestDetailView] ✓ Successfully moved to library");

                bool downloadCountUpdated = await SteamPluginManager.SupabaseConfig.IncrementDownloadCountAsync(
                    _currentFile.Id,
                    _currentFile.AppId,
                    _currentFile.FileName);
                Logger.Log($"[HZManifestDetailView] Download count update {(downloadCountUpdated ? "succeeded" : "failed")} for {_currentFile.Name}");

                if (downloadCountUpdated)
                {
                    var latestDownloadCount = await global::SteamPluginManager.SupabaseConfig.GetDownloadCountAsync(
                        _currentFile.Id,
                        _currentFile.AppId,
                        _currentFile.FileName);

                    if (latestDownloadCount.HasValue)
                    {
                        _currentFile.DownloadCount = latestDownloadCount.Value;
                        if (FindName("DownloadCountDetailText") is TextBlock downloadCountText)
                            downloadCountText.Text = $"Downloads: {latestDownloadCount.Value}";
                    }

                    await HZManifestView.RefreshActiveManifestViewAsync(forceRefresh: true);
                }

                // Clean up temp files
                try
                {
                    if (!_currentFile.IsFolderStorage && File.Exists(downloadedFilePath))
                    {
                        File.Delete(downloadedFilePath);
                        Logger.Log($"[HZManifestDetailView] Cleaned up downloaded file");
                    }
                    if (!_currentFile.IsFolderStorage && Directory.Exists(extractedFolderPath))
                    {
                        Directory.Delete(extractedFolderPath, true);
                        Logger.Log($"[HZManifestDetailView] Cleaned up extracted folder");
                    }
                }
                catch (Exception cleanupEx)
                {
                    Logger.Log($"[HZManifestDetailView] Warning: Cleanup failed: {cleanupEx.Message}");
                }

                // Mark all steps as completed
                UpdateProgressStep(5);
                ShowLoadingState(true, "Completed! Finalizing...");
                await Task.Delay(1500); // Show completion message

                // Hide overlay - use Dispatcher to ensure it renders
                Dispatcher.Invoke(() =>
                {
                    ShowResultNotification(true, $"✓ Successfully added '{_currentFile.Name}' to your library!");
                    Logger.Log($"[HZManifestDetailView] ✓ Showing success notification");
                });

                // Wait for user to see the result
                await Task.Delay(1500);

                if (FindName("AddToLibraryButton") is Button btnSuccess)
                    btnSuccess.IsEnabled = true;

                // Hide the overlay
                ShowLoadingState(false);
                Logger.Log($"[HZManifestDetailView] ✓ Successfully added to library: {_currentFile.Name}");
                Logger.Log($"[HZManifestDetailView] ✓ Successfully added to library: {_currentFile.Name}");

                // Refresh the game library view to show the newly added game
                Logger.Log($"[HZManifestDetailView] === STARTING GAME LIBRARY REFRESH ===");
                try
                {
                    Logger.Log($"[HZManifestDetailView] Getting MainWindow instance...");
                    
                    // Method 1: Try to get from Application.Current.MainWindow
                    var mainWindow = Application.Current.MainWindow as MainWindow;
                    Logger.Log($"[HZManifestDetailView] MainWindow from Application.Current.MainWindow: {(mainWindow != null ? "FOUND" : "NULL")}");
                    
                    if (mainWindow == null)
                    {
                        // Method 2: Try to get from window owner chain
                        var currentWindow = Window.GetWindow(this);
                        Logger.Log($"[HZManifestDetailView] Current window: {currentWindow?.GetType().Name}");
                        
                        // Walk up the window ownership chain
                        while (currentWindow != null && !(currentWindow is MainWindow))
                        {
                            if (currentWindow is MainShell mainShell)
                            {
                                Logger.Log($"[HZManifestDetailView] Found MainShell, trying to get MainWindow from its DataContext...");
                                mainWindow = mainShell.DataContext as MainWindow;
                                break;
                            }
                            currentWindow = currentWindow.Owner;
                        }
                        
                        if (currentWindow is MainWindow mw)
                        {
                            mainWindow = mw;
                        }
                    }
                    
                    Logger.Log($"[HZManifestDetailView] MainWindow instance: {(mainWindow != null ? "FOUND" : "NULL")}");
                    
                    if (mainWindow != null)
                    {
                        // Get the plugin path and verify the lua file exists before refreshing
                        string steamPath = SteamHelper.GetSteamPath();
                        string pluginPath = System.IO.Path.Combine(steamPath, "config", "stplug-in");
                        string targetLuaFile = System.IO.Path.Combine(pluginPath, $"{_currentFile.AppId.Value}.lua");
                        
                        Logger.Log($"[HZManifestDetailView] Target Lua file: {targetLuaFile}");
                        
                        // Wait and verify that the file actually exists on disk
                        int retryCount = 0;
                        int maxRetries = 10; // Maximum 5 seconds (10 * 500ms)
                        while (!File.Exists(targetLuaFile) && retryCount < maxRetries)
                        {
                            Logger.Log($"[HZManifestDetailView] Waiting for lua file to appear on disk... (attempt {retryCount + 1}/{maxRetries})");
                            await Task.Delay(500);
                            retryCount++;
                        }
                        
                        if (!File.Exists(targetLuaFile))
                        {
                            Logger.Log($"[HZManifestDetailView] WARNING: Lua file still not found after waiting: {targetLuaFile}");
                        }
                        else
                        {
                            Logger.Log($"[HZManifestDetailView] ✓ Lua file confirmed on disk after {retryCount * 500}ms");
                        }
                        
                        Logger.Log($"[HZManifestDetailView] === CALLING mainWindow.RefreshGameList() ===");
                        mainWindow.RefreshGameList();
                        Logger.Log($"[HZManifestDetailView] ✓ mainWindow.RefreshGameList() completed");
                        
                        // Also refresh the GameLibraryView if the shell is active and currently showing it
                        WindowNavigator.RefreshGameLibraryView();
                        Logger.Log($"[HZManifestDetailView] WindowNavigator.RefreshGameLibraryView() called");
                        
                        // Also refresh the GameLibraryView collection view explicitly if it's currently shown
                        // Try to find GameLibraryView in MainShell ContentArea
                        Logger.Log($"[HZManifestDetailView] Attempting to refresh GameLibraryView if visible...");
                        if (mainWindow.Owner is MainShell mainShell)
                        {
                            Logger.Log($"[HZManifestDetailView] MainShell found");
                            var contentArea = mainShell.FindName("ContentArea") as ContentControl;
                            if (contentArea != null)
                            {
                                Logger.Log($"[HZManifestDetailView] ContentArea found, Content type: {contentArea.Content?.GetType().Name}");
                                if (contentArea.Content is GameLibraryView gameLibraryView)
                                {
                                    Logger.Log($"[HZManifestDetailView] GameLibraryView is currently visible, refreshing it");
                                    gameLibraryView.RefreshCollectionView();
                                    Logger.Log($"[HZManifestDetailView] ✓ GameLibraryView UI refreshed");
                                    
                                    // Log the addition to activity log
                                    string appIdStr = _currentFile.AppId.HasValue ? $" (AppID: {_currentFile.AppId})" : "";
                                    gameLibraryView.UpdateActivityLog($"Added '{_currentFile.Name}' to library{appIdStr}");
                                    Logger.Log($"[HZManifestDetailView] ✓ Activity log updated");
                                }
                                else
                                {
                                    Logger.Log($"[HZManifestDetailView] GameLibraryView not currently visible, refresh will occur when user navigates to it");
                                }
                            }
                            else
                            {
                                Logger.Log($"[HZManifestDetailView] ContentArea not found in MainShell");
                            }
                        }
                        else
                        {
                            Logger.Log($"[HZManifestDetailView] MainShell not found as MainWindow.Owner");
                        }
                    }
                    else
                    {
                        Logger.Log($"[HZManifestDetailView] ERROR: MainWindow is NULL, cannot refresh game library");
                    }
                }
                catch (Exception refreshEx)
                {
                    Logger.Log($"[HZManifestDetailView] EXCEPTION in refresh block: {refreshEx.Message}");
                    Logger.Log($"[HZManifestDetailView] Stack trace: {refreshEx.StackTrace}");
                }

                // Update button state
                UpdateAddToLibraryButton();
            }
            catch (Exception ex)
            {
                Logger.Log($"[HZManifestDetailView] Add to library error: {ex.Message}");
                Logger.Log($"[HZManifestDetailView] Stack trace: {ex.StackTrace}");
                
                // Show error notification
                ShowResultNotification(false, $"✗ Error: {ex.Message}");
                Task.Delay(1500).Wait(); // Wait for user to see the error
                
                // Hide loading state and re-enable button on error
                ShowLoadingState(false);
                if (FindName("AddToLibraryButton") is Button btnError)
                    btnError.IsEnabled = true;
            }
        }

        private async Task<bool> VerifyUrlAsync(string fileUrl)
        {
            try
            {
                Logger.Log($"[HZManifestDetailView] Verifying URL: {fileUrl}");
                
                var httpClient = SharedHttpClient.Instance;
                if (!httpClient.DefaultRequestHeaders.Contains("User-Agent"))
                {
                    httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
                }
                {
                    // Try HEAD first, then fallback to GET if HEAD doesn't work
                    var headRequest = new HttpRequestMessage(HttpMethod.Head, fileUrl);
                    var headResponse = await httpClient.SendAsync(headRequest);
                    
                    Logger.Log($"[HZManifestDetailView] HEAD request status: HTTP {(int)headResponse.StatusCode}");
                    
                    if (headResponse.IsSuccessStatusCode)
                    {
                        return true;
                    }
                    
                    // If HEAD fails with 405 (Method Not Allowed) or 404, try GET
                    if (headResponse.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed || 
                        headResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        Logger.Log($"[HZManifestDetailView] HEAD failed, trying GET request...");
                        
                        var getRequest = new HttpRequestMessage(HttpMethod.Get, fileUrl);
                        var getResponse = await httpClient.SendAsync(getRequest);
                        
                        Logger.Log($"[HZManifestDetailView] GET request status: HTTP {(int)getResponse.StatusCode}");
                        
                        if (getResponse.IsSuccessStatusCode)
                        {
                            // Read a small portion to verify file exists
                            var content = await getResponse.Content.ReadAsByteArrayAsync();
                            Logger.Log($"[HZManifestDetailView] File verified: {content?.Length} bytes");
                            return content != null && content.Length > 0;
                        }
                    }
                    
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[HZManifestDetailView] URL verification error: {ex.Message}");
                return false;
            }
        }

        private async Task<string> DownloadFileAsync(string fileUrl, string fileName)
        {
            try
            {
                // Create temp folder in AppData
                string tempFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SteamPluginManager", "Downloads");
                
                Directory.CreateDirectory(tempFolder);
                string filePath = Path.Combine(tempFolder, fileName);

                Logger.Log($"[HZManifestDetailView] Downloading: {fileUrl}");
                Logger.Log($"[HZManifestDetailView] Target: {filePath}");

                var httpClient = SharedHttpClient.Instance;
                if (!httpClient.DefaultRequestHeaders.Contains("User-Agent"))
                {
                    httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
                }
                {
                    // Downloading file

                    var response = await httpClient.GetAsync(fileUrl);
                    
                    // Log detailed error info for debugging
                    if (!response.IsSuccessStatusCode)
                    {
                        Logger.Log($"[HZManifestDetailView] Download failed: HTTP {(int)response.StatusCode}");
                        Logger.Log($"[HZManifestDetailView] Content Type: {response.Content.Headers.ContentType}");
                        
                        // Try to read response content for error details
                        try
                        {
                            string errorContent = await response.Content.ReadAsStringAsync();
                            if (!string.IsNullOrEmpty(errorContent) && errorContent.Length < 500)
                            {
                                Logger.Log($"[HZManifestDetailView] Response: {errorContent}");
                            }
                        }
                        catch { }
                        
                        // If 404, file doesn't exist in bucket
                        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                        {
                            throw new Exception($"File not found in Supabase storage bucket. URL: {fileUrl}");
                        }
                        
                        // If 403, might need authentication or different access
                        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                        {
                            throw new Exception($"Access denied to file. The bucket might require authentication.");
                        }
                        
                        throw new Exception($"Download failed with HTTP {(int)response.StatusCode}");
                    }

                    using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await response.Content.CopyToAsync(fileStream);
                    }
                }

                // Verify file was created and has content
                if (!File.Exists(filePath))
                {
                    throw new Exception("File was not created after download");
                }
                
                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Length == 0)
                {
                    throw new Exception("Downloaded file is empty");
                }

                Logger.Log($"[HZManifestDetailView] ✓ Download complete: {filePath} ({fileInfo.Length} bytes)");
                return filePath;
            }
            catch (Exception ex)
            {
                Logger.Log($"[HZManifestDetailView] Download error: {ex.Message}");
                throw;
            }
        }

        private async Task<string> ExtractFileAsync(string filePath)
        {
            try
            {
                if (_currentFile?.IsFolderStorage == true && Directory.Exists(filePath))
                {
                    Logger.Log($"[HZManifestDetailView] Folder storage detected, skipping extraction: {filePath}");
                    return filePath;
                }

                Logger.Log($"[HZManifestDetailView] Extracting: {filePath}");

                // Create extraction folder
                string extractFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SteamPluginManager", "Extracts",
                    Path.GetFileNameWithoutExtension(filePath));

                if (Directory.Exists(extractFolder))
                {
                    Directory.Delete(extractFolder, true);
                }
                Directory.CreateDirectory(extractFolder);

                // Extract zip file
                await Task.Run(() =>
                {
                    try
                    {
                        using (var archive = System.IO.Compression.ZipFile.OpenRead(filePath))
                        {
                            foreach (var entry in archive.Entries)
                            {
                                string entryPath = Path.Combine(extractFolder, entry.FullName);
                                string entryDir = Path.GetDirectoryName(entryPath);

                                // Create directory if it doesn't exist
                                if (!string.IsNullOrEmpty(entryDir) && !Directory.Exists(entryDir))
                                {
                                    Directory.CreateDirectory(entryDir);
                                }

                                // Extract file if it's not a directory
                                if (!entry.FullName.EndsWith("/") && !entry.FullName.EndsWith("\\"))
                                {
                                    entry.ExtractToFile(entryPath, overwrite: true);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[HZManifestDetailView] ZipFile extraction error: {ex.Message}");
                        throw;
                    }
                });

                Logger.Log($"[HZManifestDetailView] ✓ Extraction complete: {extractFolder}");
                return extractFolder;
            }
            catch (Exception ex)
            {
                Logger.Log($"[HZManifestDetailView] Extract error: {ex.Message}");
                throw;
            }
        }

        private async Task<bool> MoveToLibraryAsync(string extractedFolderPath, int appId)
        {
            try
            {
                // Get Steam path
                string steamPath = MainWindow.GetSteamPath();
                if (string.IsNullOrEmpty(steamPath))
                {
                    Logger.Log("[HZManifestDetailView] Steam path not found");
                    throw new Exception("Steam installation path not found");
                }

                // Build path to st-plugin, lua, and depot cache folders
                string pluginPath = Path.Combine(steamPath, "config", "stplug-in");
                string luaPath = Path.Combine(steamPath, "config", "lua");
                string depotCachePath = Path.Combine(steamPath, "depotcache");
                string depotCachePathOld = Path.Combine(steamPath, "config", "depotcache");

                // Create folders if they don't exist
                if (!Directory.Exists(pluginPath))
                {
                    Logger.Log($"[HZManifestDetailView] Plugin folder not found, creating: {pluginPath}");
                    Directory.CreateDirectory(pluginPath);
                }

                if (!Directory.Exists(luaPath))
                {
                    Logger.Log($"[HZManifestDetailView] Lua folder not found, creating: {luaPath}");
                    Directory.CreateDirectory(luaPath);
                }

                if (!Directory.Exists(depotCachePath))
                {
                    Logger.Log($"[HZManifestDetailView] Depot cache folder not found, creating: {depotCachePath}");
                    Directory.CreateDirectory(depotCachePath);
                }

                if (!Directory.Exists(depotCachePathOld))
                {
                    Logger.Log($"[HZManifestDetailView] Old depot cache folder not found, creating: {depotCachePathOld}");
                    Directory.CreateDirectory(depotCachePathOld);
                }

                Logger.Log($"[HZManifestDetailView] Target plugin path: {pluginPath}");
                Logger.Log($"[HZManifestDetailView] Target depot cache paths: {depotCachePath} and {depotCachePathOld}");

                // Copy files to library
                await Task.Run(() =>
                {
                    try
                    {
                        bool luaFileCopied = false;
                        bool manifestFileCopied = false;

                        // Find and copy lua files to st-plugin folder
                        var luaFiles = Directory.GetFiles(extractedFolderPath, "*.lua", SearchOption.AllDirectories);
                        if (luaFiles.Length > 0)
                        {
                            // Use the first lua file found
                            string sourceLuaFile = luaFiles[0];
                            string targetLuaFile = Path.Combine(pluginPath, $"{appId}.lua");
                            string targetLuaFileOld = Path.Combine(luaPath, $"{appId}.lua");

                            Logger.Log($"[HZManifestDetailView] Copying lua file: {sourceLuaFile} -> {targetLuaFile} and {targetLuaFileOld}");
                            
                            // Copy file with explicit flush to ensure it's written to disk
                            using (var sourceStream = File.OpenRead(sourceLuaFile))
                            using (var targetStream = File.Create(targetLuaFile))
                            {
                                sourceStream.CopyTo(targetStream);
                                targetStream.Flush();
                                targetStream.Flush(flushToDisk: true);
                            }
                            using (var sourceStream = File.OpenRead(sourceLuaFile))
                            using (var targetStream = File.Create(targetLuaFileOld))
                            {
                                sourceStream.CopyTo(targetStream);
                                targetStream.Flush();
                                targetStream.Flush(flushToDisk: true);
                            }
                            
                            // Verify files were written
                            if (!File.Exists(targetLuaFile))
                            {
                                throw new Exception($"File copy verification failed: {targetLuaFile} does not exist after copy");
                            }
                            if (!File.Exists(targetLuaFileOld))
                            {
                                throw new Exception($"File copy verification failed: {targetLuaFileOld} does not exist after copy");
                            }
                            
                            Logger.Log($"[HZManifestDetailView] ✓ Lua file copied and verified successfully");
                            luaFileCopied = true;
                        }
                        else
                        {
                            Logger.Log($"[HZManifestDetailView] No lua files found in extracted folder");
                        }

                        // Find and copy manifest files to both depotcache paths
                        var manifestFiles = Directory.GetFiles(extractedFolderPath, "*.manifest", SearchOption.AllDirectories);
                        if (manifestFiles.Length > 0)
                        {
                            // Copy all manifest files found to both paths
                            foreach (var sourceManifestFile in manifestFiles)
                            {
                                string manifestFileName = Path.GetFileName(sourceManifestFile);
                                string targetManifestFile = Path.Combine(depotCachePath, manifestFileName);
                                string targetManifestFileOld = Path.Combine(depotCachePathOld, manifestFileName);

                                Logger.Log($"[HZManifestDetailView] Copying manifest file: {sourceManifestFile} -> {targetManifestFile} and {targetManifestFileOld}");
                                
                                // Copy file to new path with explicit flush
                                using (var sourceStream = File.OpenRead(sourceManifestFile))
                                using (var targetStream = File.Create(targetManifestFile))
                                {
                                    sourceStream.CopyTo(targetStream);
                                    targetStream.Flush();
                                    targetStream.Flush(flushToDisk: true);
                                }
                                
                                // Copy file to old path with explicit flush
                                using (var sourceStream = File.OpenRead(sourceManifestFile))
                                using (var targetStream = File.Create(targetManifestFileOld))
                                {
                                    sourceStream.CopyTo(targetStream);
                                    targetStream.Flush();
                                    targetStream.Flush(flushToDisk: true);
                                }
                                
                                // Verify both files were written
                                if (!File.Exists(targetManifestFile))
                                {
                                    throw new Exception($"File copy verification failed: {targetManifestFile} does not exist after copy");
                                }
                                if (!File.Exists(targetManifestFileOld))
                                {
                                    throw new Exception($"File copy verification failed: {targetManifestFileOld} does not exist after copy");
                                }
                                
                                Logger.Log($"[HZManifestDetailView] ✓ Manifest file copied and verified in both paths: {manifestFileName}");
                                manifestFileCopied = true;
                            }
                        }
                        else
                        {
                            Logger.Log($"[HZManifestDetailView] No manifest files found in extracted folder");
                        }

                        // At least one file type must be copied
                        if (!luaFileCopied && !manifestFileCopied)
                        {
                            Logger.Log($"[HZManifestDetailView] No lua or manifest files found in extracted folder");
                            throw new Exception("No .lua or .manifest files found in extracted archive");
                        }

                        if (luaFileCopied && manifestFileCopied)
                        {
                            Logger.Log($"[HZManifestDetailView] ✓ Both lua and manifest files copied successfully");
                        }
                        else if (luaFileCopied)
                        {
                            Logger.Log($"[HZManifestDetailView] ✓ Lua file copied (no manifest files found)");
                        }
                        else if (manifestFileCopied)
                        {
                            Logger.Log($"[HZManifestDetailView] ✓ Manifest file(s) copied (no lua file found)");
                        }

                        // Force file system to flush buffer and ensure files are written to disk
                        Logger.Log($"[HZManifestDetailView] Flushing file system buffers...");
                        System.GC.Collect();
                        System.GC.WaitForPendingFinalizers();
                        System.Threading.Thread.Sleep(500); // Wait 500ms for file system to stabilize
                        Logger.Log($"[HZManifestDetailView] ✓ File system flush complete");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[HZManifestDetailView] Move operation error: {ex.Message}");
                        throw;
                    }
                });

                // Additional delay to ensure file system is completely ready
                await Task.Delay(500);
                Logger.Log($"[HZManifestDetailView] Waiting for file system sync complete");

                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[HZManifestDetailView] Move to library error: {ex.Message}");
                throw;
            }
        }

        private void BackToDashboard_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                WindowNavigator.NavigateBackFromHZManifestDetail();
            }
            catch (Exception ex)
            {
                Logger.Log($"[HZManifestDetailView] Navigation error: {ex.Message}");
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void DeleteFiles_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_currentFile == null || _currentFile.AppId == null)
                {
                    MessageBox.Show("No file selected", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                int appId = _currentFile.AppId.Value;

                // Confirm deletion
                var result = MessageBox.Show(
                    $"Delete lua and manifest files for {_currentFile.Name}?\n\n" +
                    "This will remove:\n" +
                    $"• Lua file: {appId}.lua\n" +
                    $"• All related manifest files\n\n" +
                    "You can apply the manifest again after deletion.",
                    "Confirm Delete",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                    return;

                // Show loading state
                ShowLoadingState(true, "Deleting files...");
                if (FindName("DeleteFilesButton") is Button deleteBtn)
                    deleteBtn.IsEnabled = false;

                await Task.Run(() =>
                {
                    try
                    {
                        string steamPath = SteamHelper.GetSteamPath();
                        if (string.IsNullOrEmpty(steamPath))
                            throw new Exception("Steam path not found");

                        string pluginPath = Path.Combine(steamPath, "config", "stplug-in");
                        string luaPath = Path.Combine(steamPath, "config", "lua");
                        string depotCachePath = Path.Combine(steamPath, "depotcache");
                        string depotCachePathOld = Path.Combine(steamPath, "config", "depotcache");

                        int filesDeleted = 0;
                        
                        // First, read lua file to find related manifest files
                        var relatedManifestFiles = new List<string>();
                        string luaFilePath = Path.Combine(pluginPath, $"{appId}.lua");
                        string alternateLuaFilePath = Path.Combine(luaPath, $"{appId}.lua");

                        if (!File.Exists(luaFilePath) && File.Exists(alternateLuaFilePath))
                        {
                            luaFilePath = alternateLuaFilePath;
                        }

                        if (File.Exists(luaFilePath))
                        {
                            try
                            {
                                string luaContent = File.ReadAllText(luaFilePath);
                                Logger.Log($"[HZManifestDetailView] Reading lua file: {luaFilePath}");
                                
                                // Extract manifest files from lua file using regex: setManifestid(depotId,"manifestId",...)
                                // Format: depotId_manifestId.manifest
                                var regex = new System.Text.RegularExpressions.Regex(@"setManifestid\((\d+),\s*""(\d+)""");
                                var matches = regex.Matches(luaContent);
                                
                                foreach (System.Text.RegularExpressions.Match match in matches)
                                {
                                    if (match.Groups.Count > 2)
                                    {
                                        string depotId = match.Groups[1].Value;
                                        string manifestId = match.Groups[2].Value;
                                        string manifestFileName = $"{depotId}_{manifestId}.manifest";
                                        relatedManifestFiles.Add(manifestFileName);
                                        Logger.Log($"[HZManifestDetailView] Found manifest file: {manifestFileName}");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"[HZManifestDetailView] Error reading lua file content: {ex.Message}");
                            }
                        }

                        // Delete lua files from both plugin and lua folders
                        string pluginLuaFile = Path.Combine(pluginPath, $"{appId}.lua");
                        string luaFolderLuaFile = Path.Combine(luaPath, $"{appId}.lua");

                        if (File.Exists(pluginLuaFile))
                        {
                            File.Delete(pluginLuaFile);
                            Logger.Log($"[HZManifestDetailView] Deleted lua file: {pluginLuaFile}");
                            filesDeleted++;
                        }
                        if (File.Exists(luaFolderLuaFile) && !string.Equals(pluginLuaFile, luaFolderLuaFile, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Delete(luaFolderLuaFile);
                            Logger.Log($"[HZManifestDetailView] Deleted lua file: {luaFolderLuaFile}");
                            filesDeleted++;
                        }

                        // Delete only manifest files related to this lua file
                        foreach (var manifestFileName in relatedManifestFiles)
                        {
                            // Delete from new path
                            if (Directory.Exists(depotCachePath))
                            {
                                string manifestPath = Path.Combine(depotCachePath, manifestFileName);
                                if (File.Exists(manifestPath))
                                {
                                    try
                                    {
                                        File.Delete(manifestPath);
                                        Logger.Log($"[HZManifestDetailView] Deleted manifest file: {manifestPath}");
                                        filesDeleted++;
                                    }
                                    catch (Exception ex)
                                    {
                                        Logger.Log($"[HZManifestDetailView] Failed to delete {manifestPath}: {ex.Message}");
                                    }
                                }
                            }
                            
                            // Delete from old path
                            if (Directory.Exists(depotCachePathOld))
                            {
                                string manifestPath = Path.Combine(depotCachePathOld, manifestFileName);
                                if (File.Exists(manifestPath))
                                {
                                    try
                                    {
                                        File.Delete(manifestPath);
                                        Logger.Log($"[HZManifestDetailView] Deleted old manifest file: {manifestPath}");
                                        filesDeleted++;
                                    }
                                    catch (Exception ex)
                                    {
                                        Logger.Log($"[HZManifestDetailView] Failed to delete old {manifestPath}: {ex.Message}");
                                    }
                                }
                            }
                        }

                        Dispatcher.Invoke(() =>
                        {
                            ShowLoadingState(false);
                            if (FindName("DeleteFilesButton") is Button btn)
                                btn.IsEnabled = true;

                            // Refresh button to show "Add to Library" again
                            UpdateAddToLibraryButton();

                            if (relatedManifestFiles.Count == 0)
                            {
                                MessageBox.Show(
                                    $"Files deleted successfully!\n\n" +
                                    $"• Lua file deleted (1)\n" +
                                    $"• No related manifest files found\n\n" +
                                    "You can now apply the manifest again from scratch.",
                                    "Delete Complete",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Information);
                            }
                            else
                            {
                                MessageBox.Show(
                                    $"Files deleted successfully!\n\n" +
                                    $"Lua file: 1\n" +
                                    $"Manifest files: {filesDeleted - 1}\n" +
                                    $"Total: {filesDeleted} file(s) removed\n\n" +
                                    "You can now apply the manifest again from scratch.",
                                    "Delete Complete",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Information);
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[HZManifestDetailView] Error deleting files: {ex.Message}");
                        Dispatcher.Invoke(() =>
                        {
                            ShowLoadingState(false);
                            if (FindName("DeleteFilesButton") is Button btn)
                                btn.IsEnabled = true;

                            MessageBox.Show(
                                $"Error deleting files:\n{ex.Message}",
                                "Delete Error",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                        });
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"[HZManifestDetailView] DeleteFiles_Click error: {ex.Message}");
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void RestartSteam_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Confirm restart
                var result = MessageBox.Show(
                    "Restart Steam to apply changes?\n\n" +
                    "This will:\n" +
                    "• Close Steam if it's running\n" +
                    "• Wait for Steam to fully close\n" +
                    "• Restart Steam\n\n" +
                    "Make sure to save your work in any Steam games first.",
                    "Restart Steam",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                    return;

                // Disable button during operation
                if (FindName("RestartSteamButton") is Button restartBtn)
                    restartBtn.IsEnabled = false;

                // Show loading state
                ConfigureOverlayForRestartSteam();
                ShowLoadingState(true, "Restarting Steam...");

                await Task.Run(async () =>
                {
                    try
                    {
                        Logger.Log("[HZManifestDetailView] Starting Steam restart process...");

                        // Step 1: Close Steam gracefully
                        Logger.Log("[HZManifestDetailView] Step 1: Closing Steam...");
                        Dispatcher.Invoke(() =>
                        {
                            ShowLoadingState(true, "Closing Steam...");
                            UpdateProgressStep(1);
                        });

                        // Try to close Steam gracefully first
                        var steamProcesses = Process.GetProcessesByName("steam");
                        if (steamProcesses.Length > 0)
                        {
                            Logger.Log($"[HZManifestDetailView] Found {steamProcesses.Length} Steam process(es), attempting graceful close...");

                            // Send close message to Steam
                            foreach (var process in steamProcesses)
                            {
                                try
                                {
                                    if (!process.HasExited)
                                    {
                                        process.CloseMainWindow(); // Try graceful close first
                                        Logger.Log($"[HZManifestDetailView] Sent close message to Steam process {process.Id}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Logger.Log($"[HZManifestDetailView] Error closing Steam process {process.Id}: {ex.Message}");
                                }
                            }

                            // Wait for Steam to close (max 3 seconds)
                            int waitCount = 0;
                            const int maxWait = 3; // 3 seconds
                            while (Process.GetProcessesByName("steam").Length > 0 && waitCount < maxWait)
                            {
                                await Task.Delay(1000);
                                waitCount++;
                                Logger.Log($"[HZManifestDetailView] Waiting for Steam to close... ({waitCount}/{maxWait}s)");
                            }

                            // Force kill if still running
                            steamProcesses = Process.GetProcessesByName("steam");
                            if (steamProcesses.Length > 0)
                            {
                                Logger.Log("[HZManifestDetailView] Steam still running after graceful close, force killing...");
                                foreach (var process in steamProcesses)
                                {
                                    try
                                    {
                                        if (!process.HasExited)
                                        {
                                            process.Kill();
                                            Logger.Log($"[HZManifestDetailView] Force killed Steam process {process.Id}");
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Logger.Log($"[HZManifestDetailView] Error force killing Steam process {process.Id}: {ex.Message}");
                                    }
                                }
                            }
                        }
                        else
                        {
                            Logger.Log("[HZManifestDetailView] Steam is not currently running");
                        }

                        // Wait a bit more to ensure Steam is fully closed
                        await Task.Delay(2000);

                        // Step 2: Start Steam
                        Logger.Log("[HZManifestDetailView] Step 2: Starting Steam...");
                        Dispatcher.Invoke(() =>
                        {
                            ShowLoadingState(true, "Starting Steam...");
                            UpdateProgressStep(2);
                        });

                        // Get Steam executable path
                        string steamPath = MainWindow.GetSteamPath();
                        if (string.IsNullOrEmpty(steamPath))
                        {
                            throw new Exception("Steam installation path not found. Please check Steam settings.");
                        }

                        string steamExePath = System.IO.Path.Combine(steamPath, "steam.exe");
                        if (!File.Exists(steamExePath))
                        {
                            throw new Exception($"Steam executable not found at: {steamExePath}");
                        }

                        Logger.Log($"[HZManifestDetailView] Starting Steam from: {steamExePath}");

                        // Start Steam process with silent startup to avoid additional Steam UI overlays
                        var startInfo = new ProcessStartInfo
                        {
                            FileName = steamExePath,
                            Arguments = "-silent",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WindowStyle = ProcessWindowStyle.Minimized,
                            WorkingDirectory = steamPath
                        };

                        var steamProcess = System.Diagnostics.Process.Start(startInfo);
                        if (steamProcess != null)
                        {
                            Logger.Log($"[HZManifestDetailView] Steam started successfully (PID: {steamProcess.Id})");
                        }
                        else
                        {
                            Logger.Log("[HZManifestDetailView] Warning: Steam process start returned null");
                        }

                        // Wait a moment for Steam to start
                        await Task.Delay(3000);

                        Logger.Log("[HZManifestDetailView] Steam restart process completed");

                        // Show success message
                        Dispatcher.Invoke(() =>
                        {
                            ShowLoadingState(false);
                            if (FindName("RestartSteamButton") is Button btn)
                                btn.IsEnabled = true;

                            MessageBox.Show(
                                "Steam has been restarted successfully!\n\n" +
                                "The manifest changes should now be applied.\n\n" +
                                "Note: Steam may take a few moments to fully load.",
                                "Restart Complete",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
                        });
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[HZManifestDetailView] Error in Steam restart process: {ex.Message}");

                        Dispatcher.Invoke(() =>
                        {
                            ShowLoadingState(false);
                            if (FindName("RestartSteamButton") is Button btn)
                                btn.IsEnabled = true;

                            MessageBox.Show(
                                $"Error restarting Steam:\n{ex.Message}\n\n" +
                                "You may need to restart Steam manually.",
                                "Restart Error",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                        });
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"[HZManifestDetailView] RestartSteam_Click error: {ex.Message}");
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowButtonLoadingState(bool isLoading)
        {
            try
            {
                var buttonText = FindName("AddToLibraryButtonText") as TextBlock;
                var loadingGrid = FindName("LoadingGrid") as Grid;

                if (isLoading)
                {
                    if (buttonText != null)
                        buttonText.Visibility = Visibility.Collapsed;
                    
                    if (loadingGrid != null)
                        loadingGrid.Visibility = Visibility.Visible;

                    // Start the spinning animation
                    var storyboard = FindResource("SpinAnimation") as Storyboard;
                    if (storyboard != null)
                        storyboard.Begin();
                }
                else
                {
                    if (buttonText != null)
                        buttonText.Visibility = Visibility.Visible;
                    
                    if (loadingGrid != null)
                        loadingGrid.Visibility = Visibility.Collapsed;

                    // Stop the animation
                    var storyboard = FindResource("SpinAnimation") as Storyboard;
                    if (storyboard != null)
                        storyboard.Stop();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[HZManifestDetailView] Error in ShowLoadingState: {ex.Message}");
            }
        }
    }

}
#pragma warning restore CS0103 // The name does not exist
