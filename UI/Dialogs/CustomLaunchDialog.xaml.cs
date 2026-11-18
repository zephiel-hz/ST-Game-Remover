using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using SteamPluginManager.Models;
using SteamPluginManager.Services.Steam;

namespace SteamPluginManager.UI.Dialogs
{
    public partial class CustomLaunchDialog : Window, INotifyPropertyChanged
    {
        private readonly GameEntry _game;
        private readonly SteamLaunchOptionsService _service = SteamLaunchOptionsService.Shared;
        private SteamLaunchState? _state;
        private List<SteamLaunchOption> _baseline = new();
        private bool _isLoading = true;
        private bool _isDirty;
        private bool _isReapplyPending;
        private LaunchEntryEditor? _selected;
        private readonly List<LaunchEntryEditor> _watchedEntries = new();

        public event PropertyChangedEventHandler? PropertyChanged;
        public bool ConfigChanged { get; private set; }
        public ObservableCollection<LaunchEntryEditor> Entries { get; } = new();
        public IReadOnlyList<string> TypeOptions { get; } = new[] { "default", "none", "option1", "option2", "vr" };

        public LaunchEntryEditor? Selected
        {
            get => _selected;
            set
            {
                if (ReferenceEquals(_selected, value))
                    return;
                _selected = value;
                OnPropertyChanged();
                UpdateCommands();
            }
        }

        public bool CanEdit => !_isLoading && _state != null;
        public bool CanRemove => CanEdit && Selected != null;
        public bool CanMoveUp => CanEdit && Selected != null && Entries.IndexOf(Selected) > 0;
        public bool CanMoveDown => CanEdit && Selected != null && Entries.IndexOf(Selected) < Entries.Count - 1;
        public bool CanSave => CanEdit && (_isDirty || _isReapplyPending);
        public Visibility RestoreVisibility => _state?.IsModified == true ? Visibility.Visible : Visibility.Collapsed;

        public CustomLaunchDialog(GameEntry game)
        {
            InitializeComponent();
            _game = game ?? throw new ArgumentNullException(nameof(game));
            DataContext = this;
            GameNameText.Text = $"{_game.Name}  ·  AppID {_game.AppId}";
            Entries.CollectionChanged += (_, _) =>
            {
                HookEntries();
                RecomputeDirty();
                UpdateCommands();
            };
            Loaded += CustomLaunchDialog_Loaded;
        }

        private async void CustomLaunchDialog_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                SetStatus("Reading Steam's appinfo cache...");
                var state = await Task.Run(() => _service.Read(_game.AppId));
                if (state == null)
                {
                    ShowError($"AppID {_game.AppId} was not found in Steam's appinfo cache.");
                    return;
                }

                _state = state;
                var staged = _service.GetStagedDesired(_game.AppId);
                var displayed = staged ?? state.Current;
                Entries.Clear();
                foreach (var option in displayed)
                    Entries.Add(new LaunchEntryEditor(option));

                Selected = Entries.FirstOrDefault();
                _baseline = Normalize(displayed);
                _isDirty = false;
                _isReapplyPending = staged != null && SteamLaunchOptionComparer.Differs(
                    Normalize(state.Current), Normalize(staged));
                OnPropertyChanged(nameof(RestoreVisibility));
                SetStatus(state.InstallDirectory is null
                    ? "Steam launch options loaded."
                    : $"Steam install directory: {state.InstallDirectory}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[CustomLaunchDialog] Could not load Steam launch options for {_game.AppId}: {ex}");
                ShowError(ex.Message);
            }
            finally
            {
                _isLoading = false;
                UpdateCommands();
            }
        }

        private void AddEntry_Click(object sender, RoutedEventArgs e)
        {
            var entry = new LaunchEntryEditor(new SteamLaunchOption
            {
                Index = Entries.Count.ToString(),
                Type = "default",
                OperatingSystems = "windows"
            });
            Entries.Add(entry);
            Selected = entry;
        }

        private void RemoveEntry_Click(object sender, RoutedEventArgs e)
        {
            if (Selected == null)
                return;
            int position = Entries.IndexOf(Selected);
            Entries.Remove(Selected);
            Selected = Entries.Count == 0 ? null : Entries[Math.Min(position, Entries.Count - 1)];
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
        private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

        private void MoveSelected(int direction)
        {
            if (Selected == null)
                return;
            int oldIndex = Entries.IndexOf(Selected);
            int newIndex = oldIndex + direction;
            if (newIndex < 0 || newIndex >= Entries.Count)
                return;
            Entries.Move(oldIndex, newIndex);
            UpdateCommands();
        }

        private void BrowseExecutable_Click(object sender, RoutedEventArgs e)
        {
            string? gameInstallPath = !string.IsNullOrWhiteSpace(_game.InstallationPath)
                && Directory.Exists(_game.InstallationPath)
                    ? _game.InstallationPath
                    : SteamHelper.GetGameInstallationPath(_game.AppId, _game.Name);
            var dialog = new OpenFileDialog
            {
                Title = $"Select launch file for {_game.Name}",
                Filter = "Launch files (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd|Executable files (*.exe)|*.exe|Batch files (*.bat;*.cmd)|*.bat;*.cmd|All files (*.*)|*.*",
                CheckFileExists = true
            };
            if (!string.IsNullOrWhiteSpace(gameInstallPath) && Directory.Exists(gameInstallPath))
                dialog.InitialDirectory = gameInstallPath;

            if (dialog.ShowDialog(this) != true || Selected == null)
                return;

            bool isBatchFile = Path.GetExtension(dialog.FileName).Equals(".bat", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(dialog.FileName).Equals(".cmd", StringComparison.OrdinalIgnoreCase);
            string arguments = Selected.Arguments;
            bool wasBatchLaunch = !string.IsNullOrWhiteSpace(gameInstallPath)
                && SteamBatchLaunchCommand.TryExtractArguments(
                    Selected.Executable,
                    Selected.Arguments,
                    gameInstallPath,
                    out arguments);

            if (!isBatchFile)
            {
                Selected.Executable = dialog.FileName;
                Selected.Arguments = arguments;
                if (wasBatchLaunch)
                    Selected.WorkingDirectory = string.Empty;
                return;
            }

            if (string.IsNullOrWhiteSpace(gameInstallPath) || !Directory.Exists(gameInstallPath))
            {
                ShowError("The game must be installed before a batch file can be converted to a Steam launch entry.");
                return;
            }

            try
            {
                string relativeExecutable = Path.GetRelativePath(gameInstallPath, dialog.FileName);
                var launchCommand = SteamBatchLaunchCommand.Create(
                    dialog.FileName,
                    relativeExecutable,
                    arguments,
                    gameInstallPath);
                Selected.Executable = launchCommand.Executable;
                Selected.Arguments = launchCommand.Arguments;
                Selected.WorkingDirectory = launchCommand.WorkingDirectory;
            }
            catch (Exception ex)
            {
                Logger.Log($"[CustomLaunchDialog] Could not configure batch launch file for {_game.AppId}: {ex}");
                ShowError(ex.Message);
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_state == null || !CanSave)
                return;

            var desired = Normalize(Entries.Select(entry => entry.ToOption()));
            if (desired.Any(option => string.IsNullOrWhiteSpace(option.Executable)))
            {
                ShowError("Each launch entry needs an executable.");
                return;
            }

            try
            {
                _service.Stage(_game.AppId, _state.ChangeNumber, _state.Current, desired);
                ConfigChanged = true;
                _state = _state with { IsModified = true };
                _isReapplyPending = false;
                OnPropertyChanged(nameof(RestoreVisibility));

                var answer = ModernMessageBox.Show(
                    "Steam must close before its launch options can be changed. Steam will reopen after the update. Apply now?",
                    "Apply Steam Launch Options",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (answer == MessageBoxResult.Yes)
                {
                    SetStatus("Updating Steam's launch options...");
                    SaveBtn.IsEnabled = false;
                    Task.Run(() => _service.ApplyStaged(_game.AppId)).ContinueWith(task =>
                    {
                        Dispatcher.Invoke(() => CompleteApply(task));
                    });
                    return;
                }

                SetStatus("Changes staged. Reopen this editor and use Save & Apply when you are ready.");
                DialogResult = true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[CustomLaunchDialog] Could not save launch options for {_game.AppId}: {ex}");
                ShowError(ex.Message);
            }
        }

        private void CompleteApply(Task<SteamLaunchApplyResult> task)
        {
            if (task.IsFaulted)
            {
                ShowError(task.Exception?.GetBaseException().Message ?? "Steam launch options could not be applied.");
                SaveBtn.IsEnabled = CanSave;
                return;
            }

            SteamLaunchApplyResult result = task.Result;
            if (!result.Success)
            {
                ShowError(result.Error ?? "Steam launch options could not be applied.");
                SaveBtn.IsEnabled = CanSave;
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.RestartWarning))
                SetStatus($"Options were applied, but Steam could not be restarted: {result.RestartWarning}");
            else
                SetStatus(result.SteamWasRunning
                    ? "Options applied. Steam has been restarted."
                    : "Options applied. Start Steam to use them.");
            DialogResult = true;
        }

        private void Restore_Click(object sender, RoutedEventArgs e)
        {
            if (_state?.IsModified != true)
                return;

            var answer = ModernMessageBox.Show(
                "Restore the launch entries saved before this app first changed them? Steam must close and reopen.",
                "Restore Original Launch Options",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
                return;

            RestoreButton.IsEnabled = false;
            SetStatus("Restoring original Steam launch options...");
            Task.Run(() => _service.Restore(_game.AppId)).ContinueWith(task =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (task.IsFaulted)
                    {
                        ShowError(task.Exception?.GetBaseException().Message ?? "Restore failed.");
                        RestoreButton.IsEnabled = true;
                    }
                    else if (!task.Result.Success)
                    {
                        ShowError(task.Result.Error ?? "Restore failed.");
                        RestoreButton.IsEnabled = true;
                    }
                    else
                    {
                        ConfigChanged = true;
                        SetStatus(task.Result.SteamWasRunning
                            ? "Original launch options restored. Steam has been restarted."
                            : "Original launch options restored.");
                        DialogResult = true;
                    }
                });
            });
        }

        private void HookEntries()
        {
            foreach (var oldEntry in _watchedEntries)
                oldEntry.PropertyChanged -= Entry_PropertyChanged;
            _watchedEntries.Clear();
            foreach (var entry in Entries)
            {
                entry.PropertyChanged += Entry_PropertyChanged;
                _watchedEntries.Add(entry);
            }
        }

        private void Entry_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            RecomputeDirty();
            UpdateCommands();
        }

        private void RecomputeDirty()
        {
            if (_state == null)
                return;
            var current = Normalize(Entries.Select(entry => entry.ToOption()));
            bool dirty = SteamLaunchOptionComparer.Differs(_baseline, current);
            if (_isDirty == dirty)
                return;
            _isDirty = dirty;
            OnPropertyChanged(nameof(CanSave));
        }

        private static List<SteamLaunchOption> Normalize(IEnumerable<SteamLaunchOption> options)
        {
            var result = options.Select(option => option.Copy()).ToList();
            for (int i = 0; i < result.Count; i++)
                result[i].Index = i.ToString();
            return result;
        }

        private void UpdateCommands()
        {
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(CanRemove));
            OnPropertyChanged(nameof(CanMoveUp));
            OnPropertyChanged(nameof(CanMoveDown));
            OnPropertyChanged(nameof(CanSave));
        }

        private void SetStatus(string text)
        {
            StatusText.Text = text;
            ErrorText.Visibility = Visibility.Collapsed;
        }

        private void ShowError(string text)
        {
            ErrorText.Text = text;
            ErrorText.Visibility = Visibility.Visible;
            StatusText.Text = "Launch options could not be loaded or applied.";
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                DragMove();
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public sealed class LaunchEntryEditor : INotifyPropertyChanged
        {
            private readonly SteamLaunchOption _option;
            private string _executable = string.Empty;
            private string _arguments = string.Empty;
            private string _workingDirectory = string.Empty;
            private string _description = string.Empty;
            private string _type = string.Empty;
            private string _operatingSystems = string.Empty;
            private string _architecture = string.Empty;
            private string _betaBranch = string.Empty;
            private string _ownsDlc = string.Empty;
            public event PropertyChangedEventHandler? PropertyChanged;

            public string Title => string.IsNullOrWhiteSpace(Description) ? Executable : Description;
            public string Summary => string.Join("  ·  ", new[]
            {
                Executable,
                Arguments,
                OperatingSystems
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

            public string Executable { get => _executable; set => Set(ref _executable, value); }
            public string Arguments { get => _arguments; set => Set(ref _arguments, value); }
            public string WorkingDirectory { get => _workingDirectory; set => Set(ref _workingDirectory, value); }
            public string Description { get => _description; set => Set(ref _description, value, nameof(Title)); }
            public string Type { get => _type; set => Set(ref _type, value); }
            public string OperatingSystems { get => _operatingSystems; set => Set(ref _operatingSystems, value); }
            public string Architecture { get => _architecture; set => Set(ref _architecture, value); }
            public string BetaBranch { get => _betaBranch; set => Set(ref _betaBranch, value); }
            public string OwnsDlc { get => _ownsDlc; set => Set(ref _ownsDlc, value); }

            public LaunchEntryEditor(SteamLaunchOption option)
            {
                _option = option.Copy();
                _executable = _option.Executable;
                _arguments = _option.Arguments;
                _workingDirectory = _option.WorkingDirectory;
                _description = _option.Description;
                _type = _option.Type;
                _operatingSystems = _option.OperatingSystems;
                _architecture = _option.Architecture;
                _betaBranch = _option.BetaBranch;
                _ownsDlc = _option.OwnsDlc;
            }

            public SteamLaunchOption ToOption() => new()
            {
                Index = _option.Index,
                SourceIndex = _option.SourceIndex,
                Executable = Executable,
                Arguments = Arguments,
                WorkingDirectory = WorkingDirectory,
                Description = Description,
                Type = Type,
                OperatingSystems = OperatingSystems,
                Architecture = Architecture,
                BetaBranch = BetaBranch,
                OwnsDlc = OwnsDlc
            };

            private void Set(ref string field, string? value, string? related = null)
            {
                value ??= string.Empty;
                if (field == value)
                    return;
                field = value;
                if (related == nameof(Title)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
            }
        }

        private static class SteamLaunchOptionComparer
        {
            public static bool Differs(IReadOnlyList<SteamLaunchOption> left, IReadOnlyList<SteamLaunchOption> right)
            {
                if (left.Count != right.Count)
                    return true;
                for (int i = 0; i < left.Count; i++)
                {
                    var a = left[i];
                    var b = right[i];
                    if (a.Executable != b.Executable || a.Arguments != b.Arguments
                        || a.WorkingDirectory != b.WorkingDirectory || a.Description != b.Description
                        || a.Type != b.Type || a.OperatingSystems != b.OperatingSystems
                        || a.Architecture != b.Architecture || a.BetaBranch != b.BetaBranch
                        || a.OwnsDlc != b.OwnsDlc)
                        return true;
                }
                return false;
            }
        }
    }
}
