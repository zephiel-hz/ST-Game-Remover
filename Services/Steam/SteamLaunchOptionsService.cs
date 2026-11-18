using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using SteamPluginManager.Models;

namespace SteamPluginManager.Services.Steam
{
    public sealed record SteamLaunchState(
        int AppId,
        uint ChangeNumber,
        IReadOnlyList<SteamLaunchOption> Current,
        string? InstallDirectory,
        bool IsModified);

    public sealed record SteamLaunchApplyResult(
        bool Success,
        string? Error = null,
        bool SteamWasRunning = false,
        string? RestartWarning = null);

    public sealed record SteamLaunchProgress(
        int StepNumber,
        bool Completed,
        string Status,
        string Detail,
        bool Failed = false);

    public sealed record SteamLaunchMigrationResult(
        int Imported,
        IReadOnlyList<int> SkippedAppIds,
        SteamLaunchApplyResult ApplyResult);

    public sealed class SteamLaunchOptionsService
    {
        private static readonly string StorePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SteamPluginManager",
            "steam_launch_mods.json");

        private static readonly SteamLaunchOptionsService _shared = new(StorePath);
        private readonly LaunchModStore _store;

        public static SteamLaunchOptionsService Shared => _shared;

        internal SteamLaunchOptionsService(string storePath)
        {
            _store = new LaunchModStore(storePath);
        }

        public string? AppInfoPath
        {
            get
            {
                string? steamPath = SteamHelper.GetSteamPath();
                return string.IsNullOrWhiteSpace(steamPath)
                    ? null
                    : Path.Combine(steamPath, "appcache", "appinfo.vdf");
            }
        }

        public bool IsModified(int appId) => _store.Get(appId) != null;

        public IReadOnlyList<SteamLaunchOption>? GetStagedDesired(int appId) =>
            _store.Get(appId)?.Desired;

        public int GetLegacyConfigurationCount() => ReadLegacyConfigurations().Count;

        public SteamLaunchMigrationResult MigrateLegacyConfigurations()
        {
            List<KeyValuePair<int, LegacyLaunchConfiguration>> legacy;
            try
            {
                legacy = ReadLegacyConfigurations();
            }
            catch (Exception ex)
            {
                return new SteamLaunchMigrationResult(
                    0,
                    Array.Empty<int>(),
                    new SteamLaunchApplyResult(false, $"Legacy launch settings could not be read: {ex.Message}"));
            }

            if (legacy.Count == 0)
                return new SteamLaunchMigrationResult(0, Array.Empty<int>(), new SteamLaunchApplyResult(true));

            var edits = new Dictionary<int, IReadOnlyList<SteamLaunchOption>>();
            var skipped = new List<int>();
            string path;
            try
            {
                path = GetAppInfoPathOrThrow();
                using var cache = new SteamAppInfoCache(path);
                foreach (var (appId, config) in legacy)
                {
                    if (string.IsNullOrWhiteSpace(config.ExecutablePath) || !File.Exists(config.ExecutablePath))
                    {
                        skipped.Add(appId);
                        continue;
                    }

                    var app = cache.ReadApplication(appId);
                    var body = app?.Root.FindObject("appinfo");
                    if (app is null || body is null)
                    {
                        skipped.Add(appId);
                        continue;
                    }

                    var original = ReadOptions(body);
                    string executable = config.ExecutablePath;
                    string arguments = config.Arguments ?? string.Empty;
                    string workingDirectory = config.WorkingDirectory ?? string.Empty;
                    string? installPath = SteamHelper.GetGameInstallationPath(appId, null);
                    if (!string.IsNullOrWhiteSpace(installPath) && Directory.Exists(installPath))
                    {
                        string relative = !string.IsNullOrWhiteSpace(config.RelativePath)
                            ? config.RelativePath
                            : Path.GetRelativePath(installPath, config.ExecutablePath);
                        string resolved = Path.GetFullPath(Path.Combine(installPath, relative));
                        if (File.Exists(resolved))
                        {
                            executable = relative;
                            if (string.IsNullOrWhiteSpace(workingDirectory))
                                workingDirectory = installPath;
                        }
                    }

                    string extension = Path.GetExtension(config.ExecutablePath);
                    if (extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
                    {
                        string batchPath = Path.GetFullPath(config.ExecutablePath);
                        executable = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                        string batchArguments = string.IsNullOrWhiteSpace(arguments) ? string.Empty : $" {arguments}";
                        arguments = $"/D /S /C \"\"{batchPath}\"{batchArguments}\"";
                        if (string.IsNullOrWhiteSpace(workingDirectory))
                            workingDirectory = Path.GetDirectoryName(batchPath) ?? string.Empty;
                    }

                    var desired = new List<SteamLaunchOption>
                    {
                        new()
                        {
                            Index = "0",
                            Executable = executable,
                            Arguments = arguments,
                            WorkingDirectory = workingDirectory,
                            Type = "default"
                        }
                    };
                    _store.Save(appId, app.Value.ChangeNumber, original, desired);
                    edits[appId] = desired;
                }
            }
            catch (Exception ex)
            {
                return new SteamLaunchMigrationResult(
                    0,
                    skipped,
                    new SteamLaunchApplyResult(false, ex.Message));
            }

            if (edits.Count == 0)
            {
                ArchiveLegacySettings();
                return new SteamLaunchMigrationResult(
                    0,
                    skipped,
                    new SteamLaunchApplyResult(false, "No legacy launch settings matched an installed Steam app."));
            }

            var applyResult = Apply(edits);
            if (applyResult.Success)
                ArchiveLegacySettings();

            return new SteamLaunchMigrationResult(edits.Count, skipped, applyResult);
        }

        public SteamLaunchState? Read(int appId)
        {
            if (appId <= 0)
                throw new ArgumentOutOfRangeException(nameof(appId));

            string path = GetAppInfoPathOrThrow();
            using var cache = new SteamAppInfoCache(path);
            var result = cache.ReadApplication(appId);
            if (result is null)
                return null;

            var body = result.Value.Root.FindObject("appinfo");
            if (body is null)
                throw new InvalidDataException($"Steam appinfo entry {appId} has no appinfo section.");

            List<SteamLaunchOption> current = ReadOptions(body);
            _store.Rebase(appId, result.Value.ChangeNumber, current);
            string? installDirectory = body.FindObject("config")?.FindText("installdir");

            return new SteamLaunchState(
                appId,
                result.Value.ChangeNumber,
                current,
                installDirectory,
                _store.Get(appId) != null);
        }

        public void Stage(
            int appId,
            uint changeNumber,
            IReadOnlyList<SteamLaunchOption> current,
            IReadOnlyList<SteamLaunchOption> desired)
        {
            _store.Save(appId, changeNumber, current, Normalize(desired));
        }

        public SteamLaunchApplyResult ApplyStaged(int appId, Action<SteamLaunchProgress>? onProgress = null)
        {
            var modification = _store.Get(appId);
            if (modification == null)
                return new SteamLaunchApplyResult(false, "There are no staged launch options for this game.");

            var result = Apply(new Dictionary<int, IReadOnlyList<SteamLaunchOption>>
            {
                [appId] = modification.Desired
            }, onProgress: onProgress);
            return result;
        }

        public SteamLaunchApplyResult Restore(int appId)
        {
            var modification = _store.Get(appId);
            if (modification == null)
                return new SteamLaunchApplyResult(false, "There are no saved launch options to restore.");

            var result = Apply(new Dictionary<int, IReadOnlyList<SteamLaunchOption>>
            {
                [appId] = modification.Original
            });
            if (result.Success)
                _store.Remove(appId);
            return result;
        }

        public SteamLaunchApplyResult Reapply(IReadOnlyCollection<int> appIds)
        {
            var edits = new Dictionary<int, IReadOnlyList<SteamLaunchOption>>();
            foreach (int appId in appIds.Distinct())
            {
                if (_store.Get(appId) is { } modification)
                    edits[appId] = modification.Desired;
            }
            return Apply(edits);
        }

        public IReadOnlyList<int> FindDrifted()
        {
            if (_store.IsEmpty)
                return Array.Empty<int>();

            string path = GetAppInfoPathOrThrow();
            using var cache = new SteamAppInfoCache(path);
            var drifted = new List<int>();
            foreach (var modification in _store.All)
            {
                var result = cache.ReadApplication(modification.AppId);
                if (result is null)
                    continue;

                var body = result.Value.Root.FindObject("appinfo");
                if (body == null)
                    continue;

                List<SteamLaunchOption> current = ReadOptions(body);
                _store.Rebase(modification.AppId, result.Value.ChangeNumber, current);
                if (LaunchModStore.Differs(current, modification.Desired))
                    drifted.Add(modification.AppId);
            }
            return drifted;
        }

        public SteamLaunchApplyResult Apply(
            IReadOnlyDictionary<int, IReadOnlyList<SteamLaunchOption>> edits,
            bool restartSteam = true,
            Action<SteamLaunchProgress>? onProgress = null)
        {
            if (edits.Count == 0)
                return new SteamLaunchApplyResult(true);

            string path;
            try
            {
                path = GetAppInfoPathOrThrow();
            }
            catch (Exception ex)
            {
                onProgress?.Invoke(new SteamLaunchProgress(2, false, "Could not apply custom launch", ex.Message, Failed: true));
                return new SteamLaunchApplyResult(false, ex.Message);
            }

            bool steamWasRunning = SteamHelper.IsSteamRunning();
            if (steamWasRunning)
            {
                onProgress?.Invoke(new SteamLaunchProgress(1, false, "Closing Steam", "Waiting for Steam to exit before updating launch options."));
                if (!CloseSteam(out string closeError))
                {
                    onProgress?.Invoke(new SteamLaunchProgress(1, false, "Could not close Steam", closeError, Failed: true));
                    return new SteamLaunchApplyResult(false, closeError, SteamWasRunning: true);
                }

                onProgress?.Invoke(new SteamLaunchProgress(1, true, "Steam closed", "Steam has exited. The custom launch can now be saved."));
            }
            else
            {
                onProgress?.Invoke(new SteamLaunchProgress(1, true, "Steam is already closed", "Continuing without stopping or restarting Steam."));
            }

            onProgress?.Invoke(new SteamLaunchProgress(2, false, "Applying custom launch", "Writing the configured launch options to Steam's appinfo cache."));

            string temporaryPath = path + ".sgr.tmp";
            string? error = null;
            string? restartWarning = null;
            bool wroteFile = false;
            try
            {
                SaveBackup(path);
                var replacements = new Dictionary<int, KeyValueObject>();
                using (var cache = new SteamAppInfoCache(path))
                {
                    foreach (var (appId, options) in edits)
                    {
                        var application = cache.ReadApplication(appId);
                        var body = application?.Root.FindObject("appinfo");
                        if (application is null || body is null)
                            continue;

                        WriteOptions(body, options.Select(option => option.Copy()).ToList());
                        replacements.Add(appId, application.Value.Root);
                    }

                    if (replacements.Count == 0)
                        throw new InvalidDataException("None of the selected games were found in Steam's appinfo cache.");

                    cache.WriteCopy(temporaryPath, replacements);
                }

                File.Move(temporaryPath, path, overwrite: true);
                wroteFile = true;
                onProgress?.Invoke(new SteamLaunchProgress(2, true, "Custom launch saved", "Steam's appinfo cache now contains the configured launch options."));
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch (Exception ex)
                {
                    Logger.Log($"[SteamLaunchOptionsService] Could not remove temporary cache file: {ex.Message}");
                }

                if (steamWasRunning && restartSteam)
                {
                    onProgress?.Invoke(new SteamLaunchProgress(3, false, "Starting Steam", "Reopening Steam after saving the custom launch."));
                    try
                    {
                        StartSteam();
                        onProgress?.Invoke(new SteamLaunchProgress(3, false, "Waiting for Steam to start", "Steam was launched. Waiting for its process to become available."));
                        WaitForSteamToStart();
                        onProgress?.Invoke(new SteamLaunchProgress(3, true, "Steam restarted", "Steam has been started with the updated launch options."));
                    }
                    catch (Exception ex)
                    {
                        restartWarning = ex.Message;
                        onProgress?.Invoke(new SteamLaunchProgress(3, false, "Steam could not be restarted", restartWarning, Failed: true));
                    }
                }
            }

            if (!wroteFile)
            {
                string writeError = error ?? "Steam launch options could not be written.";
                onProgress?.Invoke(new SteamLaunchProgress(2, false, "Could not save custom launch", writeError, Failed: true));
                return new SteamLaunchApplyResult(false, writeError, steamWasRunning);
            }

            if (restartWarning == null)
            {
                onProgress?.Invoke(new SteamLaunchProgress(
                    4,
                    true,
                    "Custom launch complete",
                    steamWasRunning && restartSteam
                        ? "Launch options are saved and Steam is running again."
                        : "Launch options are saved. Steam was not running, so it remains closed."));
            }

            return new SteamLaunchApplyResult(true, SteamWasRunning: steamWasRunning, RestartWarning: restartWarning);
        }

        private string GetAppInfoPathOrThrow()
        {
            string? path = AppInfoPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("Steam's appinfo.vdf cache was not found. Open Steam and try again.", path);
            return path;
        }

        private List<KeyValuePair<int, LegacyLaunchConfiguration>> ReadLegacyConfigurations()
        {
            string path = GetLegacyConfigurationPath();
            if (!File.Exists(path))
                return new List<KeyValuePair<int, LegacyLaunchConfiguration>>();

            var records = JsonSerializer.Deserialize<Dictionary<string, LegacyLaunchConfiguration>>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (records == null)
                throw new InvalidDataException("The legacy launch settings file is empty or invalid.");

            var result = new List<KeyValuePair<int, LegacyLaunchConfiguration>>();
            foreach (var pair in records)
            {
                if (!int.TryParse(pair.Key, out int appId) || appId <= 0
                    || pair.Value == null || !pair.Value.IsEnabled || _store.Get(appId) != null)
                    continue;
                result.Add(new KeyValuePair<int, LegacyLaunchConfiguration>(appId, pair.Value));
            }
            return result;
        }

        private static string GetLegacyConfigurationPath() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamPluginManager",
            "custom_launch_configs.json");

        private static void ArchiveLegacySettings()
        {
            string path = GetLegacyConfigurationPath();
            if (!File.Exists(path))
                return;

            try
            {
                string archivePath = path + $".archived-{DateTime.UtcNow:yyyyMMddHHmmss}";
                File.Move(path, archivePath);
            }
            catch (Exception ex)
            {
                Logger.Log($"[SteamLaunchOptionsService] Legacy settings were retained at the original path because they could not be archived: {ex.Message}");
            }
        }

        private static bool CloseSteam(out string error)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName("steam");
            }
            catch (Exception ex)
            {
                error = $"Could not inspect Steam processes: {ex.Message}";
                return false;
            }

            string? terminationError = null;
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                            if (!process.WaitForExit(8000))
                                terminationError ??= "Steam did not exit after its process tree was stopped.";
                        }
                    }
                    catch (InvalidOperationException)
                    {
                    }
                    catch (Exception ex)
                    {
                        terminationError ??= ex.Message;
                        Logger.Log($"[SteamLaunchOptionsService] Could not stop a Steam process: {ex.Message}");
                    }
                }
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }

            DateTime deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                if (!SteamHelper.IsSteamRunning())
                {
                    Thread.Sleep(1200);
                    error = string.Empty;
                    return true;
                }
                Thread.Sleep(500);
            }

            error = terminationError is null
                ? "Steam did not exit after its process tree was stopped."
                : $"Steam could not be stopped: {terminationError}";
            return false;
        }

        private static void StartSteam()
        {
            string? steamPath = SteamHelper.GetSteamPath();
            string executable = string.IsNullOrWhiteSpace(steamPath)
                ? string.Empty
                : Path.Combine(steamPath, "steam.exe");
            if (!File.Exists(executable))
                throw new FileNotFoundException("Steam could not be restarted because steam.exe was not found.", executable);
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
        }

        private static void WaitForSteamToStart()
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                if (SteamHelper.IsSteamRunning())
                    return;

                Thread.Sleep(500);
            }

            throw new TimeoutException("Steam was launched but did not return to a running state within 30 seconds.");
        }

        private static void SaveBackup(string path)
        {
            try
            {
                long sourceSize = new FileInfo(path).Length;
                string root = Path.GetPathRoot(Path.GetFullPath(path))
                    ?? throw new IOException("Could not resolve Steam's drive.");
                if (new DriveInfo(root).AvailableFreeSpace < sourceSize * 2)
                {
                    Logger.Log("[SteamLaunchOptionsService] Backup skipped because there is not enough free disk space.");
                    return;
                }

                string backupDirectory = Path.Combine(Path.GetDirectoryName(path)!, "sgr-launch-backup");
                Directory.CreateDirectory(backupDirectory);
                string backup = Path.Combine(backupDirectory, "appinfo.vdf");
                File.Copy(path, backup, overwrite: true);
            }
            catch (Exception ex)
            {
                Logger.Log($"[SteamLaunchOptionsService] Backup could not be created: {ex.Message}");
            }
        }

        private static List<SteamLaunchOption> ReadOptions(KeyValueObject body)
        {
            var launchTable = body.FindObject("config")?.FindObject("launch");
            if (launchTable == null)
                return new List<SteamLaunchOption>();

            return launchTable.Properties
                .Where(property => property.Kind == 0x00 && property.Value is KeyValueObject)
                .Select(property => FromObject(property.Name, (KeyValueObject)property.Value))
                .OrderBy(option => int.TryParse(option.Index, out int index) ? index : int.MaxValue)
                .ThenBy(option => option.Index, StringComparer.Ordinal)
                .ToList();
        }

        private static SteamLaunchOption FromObject(string index, KeyValueObject table)
        {
            var config = table.FindObject("config");
            return new SteamLaunchOption
            {
                Index = index,
                SourceIndex = index,
                Executable = table.FindText("executable") ?? string.Empty,
                Arguments = table.FindText("arguments") ?? string.Empty,
                WorkingDirectory = table.FindText("workingdir") ?? string.Empty,
                Description = table.FindText("description") ?? string.Empty,
                Type = table.FindText("type") ?? string.Empty,
                OperatingSystems = config?.FindText("oslist") ?? string.Empty,
                Architecture = config?.FindText("osarch") ?? string.Empty,
                BetaBranch = config?.FindText("BetaKey") ?? string.Empty,
                OwnsDlc = config?.FindText("ownsdlc") ?? string.Empty
            };
        }

        private static void WriteOptions(KeyValueObject body, IReadOnlyList<SteamLaunchOption> options)
        {
            var config = body.GetOrAddObject("config");
            var existingLaunch = config.FindObject("launch");
            var newLaunch = new KeyValueObject();

            foreach (var option in options)
            {
                string sourceIndex = string.IsNullOrWhiteSpace(option.SourceIndex)
                    ? option.Index
                    : option.SourceIndex;
                KeyValueObject? oldOption = existingLaunch?.Properties
                    .FirstOrDefault(property => property.Kind == 0x00 &&
                        string.Equals(property.Name, sourceIndex, StringComparison.OrdinalIgnoreCase))
                    ?.Value as KeyValueObject;

                var table = oldOption?.DeepCopy() ?? new KeyValueObject();
                table.SetText("executable", option.Executable);
                table.SetText("arguments", option.Arguments);
                table.SetText("workingdir", option.WorkingDirectory);
                table.SetText("description", option.Description);
                table.SetText("type", option.Type);

                bool hasConfiguration = !string.IsNullOrWhiteSpace(option.OperatingSystems)
                    || !string.IsNullOrWhiteSpace(option.Architecture)
                    || !string.IsNullOrWhiteSpace(option.BetaBranch)
                    || !string.IsNullOrWhiteSpace(option.OwnsDlc);
                var configTable = table.FindObject("config");
                if (hasConfiguration)
                {
                    configTable ??= table.GetOrAddObject("config");
                    configTable.SetText("oslist", option.OperatingSystems);
                    configTable.SetText("osarch", option.Architecture);
                    configTable.SetText("BetaKey", option.BetaBranch);
                    configTable.SetText("ownsdlc", option.OwnsDlc);
                }
                else if (configTable != null)
                {
                    configTable.Remove("oslist");
                    configTable.Remove("osarch");
                    configTable.Remove("BetaKey");
                    configTable.Remove("ownsdlc");
                    if (configTable.Properties.Count == 0)
                        table.Remove("config");
                }

                newLaunch.Properties.Add(new KeyValueProperty(option.Index, 0x00, table));
            }

            config.Remove("launch");
            config.Properties.Add(new KeyValueProperty("launch", 0x00, newLaunch));
        }

        private static List<SteamLaunchOption> Normalize(IEnumerable<SteamLaunchOption> options)
        {
            var normalized = options.Select(option =>
            {
                var copy = option.Copy();
                copy.Executable = copy.Executable.Trim();
                copy.Arguments = copy.Arguments.Trim();
                copy.WorkingDirectory = copy.WorkingDirectory.Trim();
                copy.Description = copy.Description.Trim();
                copy.Type = copy.Type.Trim();
                copy.OperatingSystems = copy.OperatingSystems.Trim();
                copy.Architecture = copy.Architecture.Trim();
                copy.BetaBranch = copy.BetaBranch.Trim();
                copy.OwnsDlc = copy.OwnsDlc.Trim();
                return copy;
            }).ToList();

            for (int i = 0; i < normalized.Count; i++)
                normalized[i].Index = i.ToString();
            return normalized;
        }

        private sealed class LaunchModStore
        {
            private readonly string _path;
            private readonly object _gate = new();
            private readonly Dictionary<int, LaunchModification> _modifications = new();
            private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

            public bool IsEmpty
            {
                get { lock (_gate) return _modifications.Count == 0; }
            }

            public IReadOnlyList<LaunchModification> All
            {
                get
                {
                    lock (_gate)
                        return _modifications.Values.Select(Clone).ToList();
                }
            }

            public LaunchModStore(string path)
            {
                _path = path;
                Load();
            }

            public LaunchModification? Get(int appId)
            {
                lock (_gate)
                    return _modifications.TryGetValue(appId, out var item) ? Clone(item) : null;
            }

            public void Save(
                int appId,
                uint changeNumber,
                IReadOnlyList<SteamLaunchOption> original,
                IReadOnlyList<SteamLaunchOption> desired)
            {
                lock (_gate)
                {
                    if (!_modifications.TryGetValue(appId, out var modification))
                    {
                        modification = new LaunchModification
                        {
                            AppId = appId,
                            ChangeNumber = changeNumber,
                            Original = original.Select(option => option.Copy()).ToList()
                        };
                        _modifications.Add(appId, modification);
                    }

                    modification.Desired = desired.Select(option => option.Copy()).ToList();
                    modification.SavedAt = DateTime.UtcNow;
                    Persist();
                }
            }

            public bool Rebase(int appId, uint changeNumber, IReadOnlyList<SteamLaunchOption> current)
            {
                lock (_gate)
                {
                    if (!_modifications.TryGetValue(appId, out var modification)
                        || modification.ChangeNumber == changeNumber)
                        return false;

                    modification.ChangeNumber = changeNumber;
                    modification.Original = current.Select(option => option.Copy()).ToList();
                    Persist();
                    return true;
                }
            }

            public void Remove(int appId)
            {
                lock (_gate)
                {
                    if (_modifications.Remove(appId))
                        Persist();
                }
            }

            private void Load()
            {
                if (!File.Exists(_path))
                    return;

                try
                {
                    var records = JsonSerializer.Deserialize<List<LaunchModification>>(
                        File.ReadAllText(_path), JsonOptions);
                    if (records != null)
                        foreach (var record in records)
                            _modifications[record.AppId] = record;
                }
                catch (Exception ex)
                {
                    Logger.Log($"[SteamLaunchOptionsService] Saved launch records could not be loaded: {ex.Message}");
                    throw new InvalidDataException(
                        $"Saved Steam launch records are unreadable: {_path}. The file was left unchanged.", ex);
                }
            }

            private void Persist()
            {
                string? directory = Path.GetDirectoryName(_path);
                if (string.IsNullOrWhiteSpace(directory))
                    throw new IOException("Could not resolve the Steam launch-record directory.");

                Directory.CreateDirectory(directory);
                string temporaryPath = _path + ".tmp";
                string? backupPath = _path + ".bak";
                try
                {
                    File.WriteAllText(temporaryPath,
                        JsonSerializer.Serialize(_modifications.Values.ToList(), JsonOptions));
                    if (File.Exists(_path))
                        File.Copy(_path, backupPath, overwrite: true);
                    File.Move(temporaryPath, _path, overwrite: true);
                }
                catch
                {
                    try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
                    throw;
                }
            }

            private static LaunchModification Clone(LaunchModification source) => new()
            {
                AppId = source.AppId,
                ChangeNumber = source.ChangeNumber,
                Original = source.Original.Select(option => option.Copy()).ToList(),
                Desired = source.Desired.Select(option => option.Copy()).ToList(),
                SavedAt = source.SavedAt
            };

            public static bool Differs(
                IReadOnlyList<SteamLaunchOption> left,
                IReadOnlyList<SteamLaunchOption> right)
            {
                if (left.Count != right.Count)
                    return true;
                for (int i = 0; i < left.Count; i++)
                {
                    var a = left[i];
                    var b = right[i];
                    if (a.Index != b.Index || a.Executable != b.Executable
                        || a.Arguments != b.Arguments || a.WorkingDirectory != b.WorkingDirectory
                        || a.Description != b.Description || a.Type != b.Type
                        || a.OperatingSystems != b.OperatingSystems || a.Architecture != b.Architecture
                        || a.BetaBranch != b.BetaBranch || a.OwnsDlc != b.OwnsDlc)
                        return true;
                }
                return false;
            }
        }

        private sealed class LaunchModification
        {
            public int AppId { get; set; }
            public uint ChangeNumber { get; set; }
            public List<SteamLaunchOption> Original { get; set; } = new();
            public List<SteamLaunchOption> Desired { get; set; } = new();
            public DateTime SavedAt { get; set; } = DateTime.UtcNow;
        }

        private sealed class LegacyLaunchConfiguration
        {
            [JsonPropertyName("is_enabled")]
            public bool IsEnabled { get; set; }

            [JsonPropertyName("executable_path")]
            public string ExecutablePath { get; set; } = string.Empty;

            [JsonPropertyName("relative_path")]
            public string RelativePath { get; set; } = string.Empty;

            [JsonPropertyName("arguments")]
            public string Arguments { get; set; } = string.Empty;

            [JsonPropertyName("working_directory")]
            public string WorkingDirectory { get; set; } = string.Empty;
        }
    }
}
