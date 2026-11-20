using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SteamPluginManager.Services.Steam;

public static class OpenSteamToolDeploymentService
{
    private const string ArchiveCategory = "archive";
    private const string SupplementalCategory = "supplemental";
    private static readonly object DeploymentLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Deploy(
        string steamRoot,
        string extractedArchiveRoot,
        string? supplementalSourceDirectory = null,
        string? supplementalDestinationDirectory = null,
        bool synchronizeSupplementalFiles = false)
    {
        lock (DeploymentLock)
        {
            string normalizedSteamRoot = NormalizeRoot(steamRoot);
            if (!Directory.Exists(normalizedSteamRoot))
                throw new DirectoryNotFoundException($"Steam directory does not exist: {normalizedSteamRoot}");

            string normalizedArchiveRoot = NormalizeRoot(extractedArchiveRoot);
            if (!Directory.Exists(normalizedArchiveRoot))
                throw new DirectoryNotFoundException($"Extracted release directory does not exist: {normalizedArchiveRoot}");

            if (synchronizeSupplementalFiles && string.IsNullOrWhiteSpace(supplementalDestinationDirectory))
                throw new ArgumentException("A destination directory is required when synchronizing supplemental files.", nameof(supplementalDestinationDirectory));

            string trackingDirectory = GetTrackingDirectory(normalizedSteamRoot);
            Directory.CreateDirectory(trackingDirectory);
            DeploymentManifest manifest = LoadManifest(trackingDirectory, normalizedSteamRoot);
            Dictionary<string, DeploymentSource> desiredFiles = CollectDesiredFiles(
                normalizedArchiveRoot,
                supplementalSourceDirectory,
                supplementalDestinationDirectory,
                synchronizeSupplementalFiles);

            if (desiredFiles.Count == 0)
                throw new InvalidOperationException("The OpenSteamTools release contains no files to install.");

            foreach (var item in desiredFiles)
            {
                string destinationPath = GetContainedPath(normalizedSteamRoot, item.Key);
                EnsureNoReparsePoints(normalizedSteamRoot, item.Key);
                if (Directory.Exists(destinationPath))
                    throw new IOException($"A directory already exists where a release file should be installed: {destinationPath}");

                ManagedFile? managedFile = manifest.Files.FirstOrDefault(
                    file => string.Equals(file.RelativePath, item.Key, StringComparison.OrdinalIgnoreCase));

                if (managedFile == null)
                {
                    managedFile = new ManagedFile { RelativePath = item.Key };
                    if (File.Exists(destinationPath))
                    {
                        managedFile.BackupRelativePath = Path.Combine("backups", item.Key);
                        string backupPath = GetContainedPath(trackingDirectory, managedFile.BackupRelativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                        File.Copy(destinationPath, backupPath, overwrite: false);
                    }

                    manifest.Files.Add(managedFile);
                }

                managedFile.Category = item.Value.Category;
                SaveManifest(trackingDirectory, manifest);
                EnsureParentDirectories(normalizedSteamRoot, item.Key, manifest, trackingDirectory);
                File.Copy(item.Value.SourcePath, destinationPath, overwrite: true);
            }

            var staleFiles = manifest.Files
                .Where(file =>
                    !desiredFiles.ContainsKey(file.RelativePath) &&
                    (file.Category == ArchiveCategory || synchronizeSupplementalFiles))
                .ToList();

            foreach (var staleFile in staleFiles)
            {
                RestoreManagedFile(normalizedSteamRoot, trackingDirectory, staleFile);
                manifest.Files.Remove(staleFile);
                SaveManifest(trackingDirectory, manifest);
                DeleteBackupIfPresent(trackingDirectory, staleFile);
            }

            RemoveUnusedCreatedDirectories(normalizedSteamRoot, manifest, trackingDirectory);
            SaveManifest(trackingDirectory, manifest);
            PruneUnusedBackups(trackingDirectory, manifest);
        }
    }

    public static bool RestoreOriginalState(string steamRoot)
    {
        lock (DeploymentLock)
        {
            string normalizedSteamRoot = NormalizeRoot(steamRoot);
            string trackingDirectory = GetTrackingDirectory(normalizedSteamRoot);
            string manifestPath = Path.Combine(trackingDirectory, "manifest.json");
            if (!File.Exists(manifestPath))
                return false;

            DeploymentManifest manifest = LoadManifest(trackingDirectory, normalizedSteamRoot);
            foreach (var managedFile in manifest.Files.ToList())
            {
                RestoreManagedFile(normalizedSteamRoot, trackingDirectory, managedFile);
                manifest.Files.Remove(managedFile);
                SaveManifest(trackingDirectory, manifest);
            }

            RemoveCreatedDirectories(normalizedSteamRoot, manifest.CreatedDirectories);
            Directory.Delete(trackingDirectory, recursive: true);
            return true;
        }
    }

    public static bool HasDeploymentRecord(string steamRoot)
    {
        string normalizedSteamRoot = NormalizeRoot(steamRoot);
        return File.Exists(Path.Combine(GetTrackingDirectory(normalizedSteamRoot), "manifest.json"));
    }

    public static bool TryMoveManagedFile(string steamRoot, string sourceRelativePath, string destinationRelativePath)
    {
        lock (DeploymentLock)
        {
            string normalizedSteamRoot = NormalizeRoot(steamRoot);
            string trackingDirectory = GetTrackingDirectory(normalizedSteamRoot);
            DeploymentManifest manifest = LoadManifest(trackingDirectory, normalizedSteamRoot);
            string sourceRelative = NormalizeRelativePath(sourceRelativePath);
            string destinationRelative = NormalizeRelativePath(destinationRelativePath);
            if (string.Equals(sourceRelative, destinationRelative, StringComparison.OrdinalIgnoreCase) ||
                !manifest.Files.Any(file => string.Equals(file.RelativePath, sourceRelative, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            string sourcePath = GetContainedPath(normalizedSteamRoot, sourceRelative);
            string destinationPath = GetContainedPath(normalizedSteamRoot, destinationRelative);
            EnsureNoReparsePoints(normalizedSteamRoot, sourceRelative);
            EnsureNoReparsePoints(normalizedSteamRoot, destinationRelative);
            if (!File.Exists(sourcePath))
                return false;
            if (Directory.Exists(destinationPath))
                throw new IOException($"A directory exists where the managed file should be moved: {destinationPath}");

            ManagedFile? destinationEntry = manifest.Files.FirstOrDefault(
                file => string.Equals(file.RelativePath, destinationRelative, StringComparison.OrdinalIgnoreCase));
            if (destinationEntry == null)
            {
                destinationEntry = new ManagedFile
                {
                    RelativePath = destinationRelative,
                    Category = ArchiveCategory
                };
                if (File.Exists(destinationPath))
                {
                    destinationEntry.BackupRelativePath = Path.Combine("backups", destinationRelative);
                    string backupPath = GetContainedPath(trackingDirectory, destinationEntry.BackupRelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    File.Copy(destinationPath, backupPath, overwrite: false);
                }

                manifest.Files.Add(destinationEntry);
            }

            destinationEntry.Category = ArchiveCategory;
            SaveManifest(trackingDirectory, manifest);
            File.Move(sourcePath, destinationPath, overwrite: true);
            return true;
        }
    }

    private static Dictionary<string, DeploymentSource> CollectDesiredFiles(
        string archiveRoot,
        string? supplementalSourceDirectory,
        string? supplementalDestinationDirectory,
        bool synchronizeSupplementalFiles)
    {
        var files = new Dictionary<string, DeploymentSource>(StringComparer.OrdinalIgnoreCase);
        foreach (string sourcePath in EnumerateRegularFiles(archiveRoot))
        {
            string relativePath = NormalizeRelativePath(Path.GetRelativePath(archiveRoot, sourcePath));
            files.Add(relativePath, new DeploymentSource(sourcePath, ArchiveCategory));
        }

        if (synchronizeSupplementalFiles && !string.IsNullOrWhiteSpace(supplementalSourceDirectory))
        {
            if (string.IsNullOrWhiteSpace(supplementalDestinationDirectory))
                throw new ArgumentException("A destination directory is required when synchronizing supplemental files.", nameof(supplementalDestinationDirectory));

            string sourceRoot = NormalizeRoot(supplementalSourceDirectory);
            string destinationRoot = NormalizeRelativePath(supplementalDestinationDirectory);
            if (Directory.Exists(sourceRoot))
            {
                foreach (string sourcePath in EnumerateRegularFiles(sourceRoot))
                {
                    string relativeSourcePath = Path.GetRelativePath(sourceRoot, sourcePath);
                    string relativePath = NormalizeRelativePath(Path.Combine(destinationRoot, relativeSourcePath));
                    files[relativePath] = new DeploymentSource(sourcePath, SupplementalCategory);
                }
            }
        }

        return files;
    }

    private static IEnumerable<string> EnumerateRegularFiles(string root)
    {
        RejectReparsePoint(root);
        var directories = new Stack<string>();
        directories.Push(root);

        while (directories.Count > 0)
        {
            string directory = directories.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Refusing to install a reparse point: {entry}");

                if ((attributes & FileAttributes.Directory) != 0)
                    directories.Push(entry);
                else
                    yield return entry;
            }
        }
    }

    private static DeploymentManifest LoadManifest(string trackingDirectory, string steamRoot)
    {
        string manifestPath = Path.Combine(trackingDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
            return new DeploymentManifest { SteamRoot = steamRoot };

        string json = File.ReadAllText(manifestPath);
        DeploymentManifest? manifest = JsonSerializer.Deserialize<DeploymentManifest>(json);
        if (manifest == null ||
            !string.Equals(NormalizeRoot(manifest.SteamRoot), steamRoot, StringComparison.OrdinalIgnoreCase) ||
            manifest.Files == null ||
            manifest.CreatedDirectories == null)
        {
            throw new InvalidDataException($"The OpenSteamTools deployment manifest is invalid: {manifestPath}");
        }

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var managedFile in manifest.Files)
        {
            managedFile.RelativePath = NormalizeRelativePath(managedFile.RelativePath);
            if (!seenPaths.Add(managedFile.RelativePath))
                throw new InvalidDataException($"The OpenSteamTools deployment manifest contains duplicate paths: {managedFile.RelativePath}");
            if (managedFile.BackupRelativePath != null)
                managedFile.BackupRelativePath = NormalizeRelativePath(managedFile.BackupRelativePath);
        }

        manifest.CreatedDirectories = manifest.CreatedDirectories
            .Select(NormalizeRelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return manifest;
    }

    private static void SaveManifest(string trackingDirectory, DeploymentManifest manifest)
    {
        string manifestPath = Path.Combine(trackingDirectory, "manifest.json");
        string temporaryPath = manifestPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(manifest, JsonOptions));
        File.Move(temporaryPath, manifestPath, overwrite: true);
    }

    private static void EnsureParentDirectories(
        string steamRoot,
        string relativeFilePath,
        DeploymentManifest manifest,
        string trackingDirectory)
    {
        string? relativeDirectory = Path.GetDirectoryName(relativeFilePath);
        if (string.IsNullOrEmpty(relativeDirectory))
            return;

        string currentPath = steamRoot;
        string accumulatedRelativePath = string.Empty;
        foreach (string segment in relativeDirectory.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            accumulatedRelativePath = string.IsNullOrEmpty(accumulatedRelativePath)
                ? segment
                : Path.Combine(accumulatedRelativePath, segment);

            EnsureNoReparsePoints(steamRoot, accumulatedRelativePath);
            if (File.Exists(currentPath))
                throw new IOException($"A file already exists where a release directory should be created: {currentPath}");

            if (!Directory.Exists(currentPath))
            {
                if (!manifest.CreatedDirectories.Contains(accumulatedRelativePath, StringComparer.OrdinalIgnoreCase))
                {
                    manifest.CreatedDirectories.Add(accumulatedRelativePath);
                    SaveManifest(trackingDirectory, manifest);
                }
                Directory.CreateDirectory(currentPath);
            }
        }
    }

    private static void RestoreManagedFile(string steamRoot, string trackingDirectory, ManagedFile managedFile)
    {
        string destinationPath = GetContainedPath(steamRoot, managedFile.RelativePath);
        EnsureNoReparsePoints(steamRoot, managedFile.RelativePath);

        if (string.Equals(managedFile.RelativePath, "OpenSteamTool.dll", StringComparison.OrdinalIgnoreCase) &&
            !File.Exists(destinationPath))
        {
            string disabledDllPath = GetContainedPath(steamRoot, "OpenSteamTool.dll.bak");
            EnsureNoReparsePoints(steamRoot, "OpenSteamTool.dll.bak");
            if (File.Exists(disabledDllPath))
                File.Move(disabledDllPath, destinationPath);
        }

        if (managedFile.BackupRelativePath != null)
        {
            string backupPath = GetContainedPath(trackingDirectory, managedFile.BackupRelativePath);
            if (!File.Exists(backupPath))
                throw new FileNotFoundException($"A Steam backup needed for uninstall is missing: {backupPath}");
            if (Directory.Exists(destinationPath))
                throw new IOException($"A directory exists where the original Steam file should be restored: {destinationPath}");

            string? parentDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(parentDirectory))
                Directory.CreateDirectory(parentDirectory);
            File.Copy(backupPath, destinationPath, overwrite: true);
        }
        else if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }
        else if (Directory.Exists(destinationPath))
        {
            throw new IOException($"A directory exists where an installed Steam file should be removed: {destinationPath}");
        }
    }

    private static void RemoveUnusedCreatedDirectories(
        string steamRoot,
        DeploymentManifest manifest,
        string trackingDirectory)
    {
        var requiredDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var managedFile in manifest.Files)
        {
            string? directory = Path.GetDirectoryName(managedFile.RelativePath);
            while (!string.IsNullOrEmpty(directory))
            {
                requiredDirectories.Add(directory);
                directory = Path.GetDirectoryName(directory);
            }
        }

        var unusedDirectories = manifest.CreatedDirectories
            .Where(directory => !requiredDirectories.Contains(directory))
            .OrderByDescending(directory => directory.Length)
            .ToList();

        foreach (string relativeDirectory in unusedDirectories)
        {
            string directoryPath = GetContainedPath(steamRoot, relativeDirectory);
            EnsureNoReparsePoints(steamRoot, relativeDirectory);
            if (Directory.Exists(directoryPath) && !Directory.EnumerateFileSystemEntries(directoryPath).Any())
                Directory.Delete(directoryPath);
            manifest.CreatedDirectories.Remove(relativeDirectory);
            SaveManifest(trackingDirectory, manifest);
        }
    }

    private static void RemoveCreatedDirectories(string steamRoot, IEnumerable<string> relativeDirectories)
    {
        foreach (string relativeDirectory in relativeDirectories.OrderByDescending(path => path.Length))
        {
            string directoryPath = GetContainedPath(steamRoot, relativeDirectory);
            EnsureNoReparsePoints(steamRoot, relativeDirectory);
            if (Directory.Exists(directoryPath) && !Directory.EnumerateFileSystemEntries(directoryPath).Any())
                Directory.Delete(directoryPath);
        }
    }

    private static void DeleteBackupIfPresent(string trackingDirectory, ManagedFile managedFile)
    {
        if (managedFile.BackupRelativePath == null)
            return;

        string backupPath = GetContainedPath(trackingDirectory, managedFile.BackupRelativePath);
        if (File.Exists(backupPath))
            File.Delete(backupPath);
    }

    private static void PruneUnusedBackups(string trackingDirectory, DeploymentManifest manifest)
    {
        string backupDirectory = Path.Combine(trackingDirectory, "backups");
        if (!Directory.Exists(backupDirectory))
            return;

        var usedBackups = new HashSet<string>(
            manifest.Files
                .Where(file => file.BackupRelativePath != null)
                .Select(file => GetContainedPath(trackingDirectory, file.BackupRelativePath!)),
            StringComparer.OrdinalIgnoreCase);

        foreach (string backupPath in Directory.EnumerateFiles(backupDirectory, "*", SearchOption.AllDirectories))
        {
            if (!usedBackups.Contains(Path.GetFullPath(backupPath)))
                File.Delete(backupPath);
        }

        foreach (string directoryPath in Directory.EnumerateDirectories(backupDirectory, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directoryPath).Any())
                Directory.Delete(directoryPath);
        }
    }

    private static string GetTrackingDirectory(string steamRoot)
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            throw new InvalidOperationException("The local application data directory is not available.");

        byte[] pathHash = SHA256.HashData(Encoding.UTF8.GetBytes(steamRoot.ToUpperInvariant()));
        string directoryName = Convert.ToHexString(pathHash);
        return Path.Combine(localAppData, "SteamPluginManager", "OpenSteamToolDeployments", directoryName);
    }

    private static string NormalizeRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A directory path is required.", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException($"Invalid relative path: {relativePath}");

        string normalized = relativePath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);
        string[] segments = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment == "." || segment == ".." || segment.Contains(':')))
            throw new InvalidDataException($"Invalid relative path: {relativePath}");

        return Path.Combine(segments);
    }

    private static string GetContainedPath(string root, string relativePath)
    {
        string normalizedRoot = NormalizeRoot(root);
        string normalizedRelativePath = NormalizeRelativePath(relativePath);
        string fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, normalizedRelativePath));
        string rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Path escapes its expected directory: {relativePath}");
        return fullPath;
    }

    private static void EnsureNoReparsePoints(string root, string relativePath)
    {
        string currentPath = NormalizeRoot(root);
        foreach (string segment in NormalizeRelativePath(relativePath).Split(Path.DirectorySeparatorChar))
        {
            currentPath = Path.Combine(currentPath, segment);
            if ((File.Exists(currentPath) || Directory.Exists(currentPath)) &&
                (File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"Refusing to modify a reparse point in the Steam directory: {currentPath}");
            }
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Refusing to install a reparse point from the release: {path}");
    }

    private sealed class DeploymentSource(string sourcePath, string category)
    {
        public string SourcePath { get; } = sourcePath;
        public string Category { get; } = category;
    }

    private sealed class DeploymentManifest
    {
        public DeploymentManifest()
        {
        }

        public string SteamRoot { get; set; } = string.Empty;
        public List<ManagedFile> Files { get; set; } = [];
        public List<string> CreatedDirectories { get; set; } = [];
    }

    private sealed class ManagedFile
    {
        public ManagedFile()
        {
        }

        public string RelativePath { get; set; } = string.Empty;
        public string Category { get; set; } = ArchiveCategory;
        public string? BackupRelativePath { get; set; }
    }
}
