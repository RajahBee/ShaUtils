using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;


namespace ShaUtils
{
    // --- NEW: Progress Reporting Classes for Incremental Hashing ---

    public class WorkerSlot : INotifyPropertyChanged
    {
        private string _fileName = "Idle";
        private string _fileSize = string.Empty;
        private string _statusText = string.Empty;
        private int _progressPercentage;
        private bool _showProgressBar;
        private Visibility _progressBarVisibility = Visibility.Collapsed;

        public int SlotId { get; set; }

        public string FileName
        {
            get => _fileName;
            set { _fileName = value; OnPropertyChanged(); }
        }

        public string FileSize
        {
            get => _fileSize;
            set { _fileSize = value; OnPropertyChanged(); }
        }

        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        public int ProgressPercentage
        {
            get => _progressPercentage;
            set { _progressPercentage = value; OnPropertyChanged(); }
        }

        public bool ShowProgressBar
        {
            get => _showProgressBar;
            set
            {
                _showProgressBar = value;
                ProgressBarVisibility = value ? Visibility.Visible : Visibility.Collapsed;
                OnPropertyChanged();
            }
        }

        public Visibility ProgressBarVisibility
        {
            get => _progressBarVisibility;
            private set { _progressBarVisibility = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public void Clear()
        {
            FileName = "Idle";
            FileSize = string.Empty;
            StatusText = string.Empty;
            ProgressPercentage = 0;
            ShowProgressBar = false;
        }
    }

    public class ProgressReport
    {
        public enum ReportType { OverallFileCompleted, SlotUpdate, StatusMessage }
        public enum SlotUpdateType { Started, InProgress, Finished }

        public ReportType Type { get; set; }
        public int SlotIndex { get; set; }

        // For OverallFileCompleted
        public int OverallProgress { get; set; }

        // For SlotUpdate
        public SlotUpdateType UpdateType { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string FileSize { get; set; } = string.Empty;
        public long FullFileSize { get; set; }
        public int ProgressPercentage { get; set; }
        public string StatusText { get; set; } = string.Empty;
        // For StatusMessage
        public string Message { get; set; } = string.Empty;
    }


    // --- Existing Classes (With Modifications) ---
    public class FileSystemNodeData
    {
        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
        public bool IsExplicitlySelected { get; set; }
        public bool ShouldBeExpanded { get; set; }
        public List<FileSystemNodeData> ChildrenData { get; set; } = [];
    }

    public class FileSystemItem : DependencyObject
    {
        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public ObservableCollection<FileSystemItem> Children { get; set; }
        public bool IsDirectory { get; set; }
        public bool HasDummyChild { get; set; }

        public bool? IsChecked { get { return (bool?)GetValue(IsCheckedProperty); } set { SetValue(IsCheckedProperty, value); } }
        public static readonly DependencyProperty IsCheckedProperty = DependencyProperty.Register("IsChecked", typeof(bool?), typeof(FileSystemItem), new PropertyMetadata(false, OnIsCheckedChanged));
        public bool IsExpanded { get { return (bool)GetValue(IsExpandedProperty); } set { SetValue(IsExpandedProperty, value); } }
        public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register("IsExpanded", typeof(bool), typeof(FileSystemItem), new PropertyMetadata(false));
        public FileSystemItem? Parent { get; set; }
        public FileSystemItem() { Children = []; }

        internal static bool _isUpdatingCheckState = false;
        private static void OnIsCheckedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FileSystemItem item) return;
            if (item.IsUpdatingParentCheckState) return;
            if (_isUpdatingCheckState) return;

            _isUpdatingCheckState = true;
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                bool? newValue = (bool?)e.NewValue;
                if (newValue.HasValue && MainWindow.AutoSelectDescendants)
                {
                    var stack = new Stack<FileSystemItem>(item.Children);
                    while (stack.Count > 0)
                    {
                        var current = stack.Pop();
                        if (current.IsChecked != newValue)
                        {
                            current.IsChecked = newValue;
                        }

                        if (current.Children.Any() && !current.HasDummyChild)
                        {
                            foreach (var child in current.Children)
                            {
                                stack.Push(child);
                            }
                        }
                    }
                }

                var parent = item.Parent;
                while (parent != null)
                {
                    parent.UpdateParentCheckState();
                    parent = parent.Parent;
                }
            }
            finally
            {
                Mouse.OverrideCursor = null;
                _isUpdatingCheckState = false;
            }
        }


        internal bool IsUpdatingParentCheckState { get; set; } = false;

        internal void UpdateParentCheckState()
        {
            if (this.IsDirectory && this.HasDummyChild) return;
            if (!Children.Any()) return;
            bool? allChildrenChecked = true;
            bool? allChildrenUnchecked = true;
            foreach (var child in Children)
            {
                if (child.IsChecked == true) allChildrenUnchecked = false;
                else if (child.IsChecked == false) allChildrenChecked = false;
                else { allChildrenChecked = false; allChildrenUnchecked = false; break; }
            }
            bool? newState = allChildrenChecked == true ? true : (allChildrenUnchecked == true ? false : (bool?)null);
            if (IsChecked != newState)
            {
                IsUpdatingParentCheckState = true;
                IsChecked = newState;
                IsUpdatingParentCheckState = false;
            }
        }
    }

    public class Sha256Entry(string hash, string fileName)
    {
        public string Hash { get; set; } = hash; public string FileName { get; set; } = fileName;

        public override string ToString() => $"{Hash} {FileName}";
    }


    public partial class MainWindow : Window
    {
        public static bool IsProgrammaticallyUpdatingTree { get; set; } = false;

        private readonly IEnumerable<string> _initialPaths;
        private readonly HashSet<string> _explicitlySelectedPaths;
        private bool _isInitialLoadComplete = false;
        public static bool AutoSelectDescendants { get; set; } = true;

        private CancellationTokenSource? _cancellationTokenSource;
        private CancellationTokenSource? _forceCancellationTokenSource;
        private enum CancellationState { NotCancelled, GracefulCancelRequested, ForcedCancelRequested }
        private CancellationState _cancellationState = CancellationState.NotCancelled;
        private readonly ObservableCollection<WorkerSlot> _workerSlots = [];
        private int _totalFilesProcessed = 0;
        private static readonly long LargeFileThreshold = 20 * 1024 * 1024; // 20 MB

        private static readonly HashSet<string> ExcludedFolderNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "$RECYCLE.BIN", "System Volume Information", "Config.Msi", "MSOCache",
            "Windows", "Program Files", "Program Files (x86)", "ProgramData", "AppData",
            "OneDrive", "Dropbox", "Google Drive", "iCloudDrive",
            "node_modules", ".cache", "Recovery"
        };
        private static readonly HashSet<string> ExcludedFileNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "pagefile.sys", "swapfile.sys", "hiberfil.sys"
        };
        private static readonly HashSet<string> LargeFileExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".tibx", ".mrimg", ".vhd", ".vhdx", ".vmdk", ".vdi"
        };
        public MainWindow() : this([]) { }

        public MainWindow(IEnumerable<string> selectedPaths)
        {
            InitializeComponent();
            _initialPaths = selectedPaths;
            _explicitlySelectedPaths = new HashSet<string>(selectedPaths.Select(p => p.Trim('"').TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), StringComparer.OrdinalIgnoreCase);
            ActiveTasksItemsControl.ItemsSource = _workerSlots;

            ExistingShaFileActionsComboBox.SelectedIndex = 0;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            AutoSelectDescendants = AutoSelectDescendantsCheckBox.IsChecked ?? true;
            AutoSelectDescendantsCheckBox.Checked += AutoSelectDescendantsCheckBox_Checked;
            AutoSelectDescendantsCheckBox.Unchecked += AutoSelectDescendantsCheckBox_Unchecked;

            InitializeThreadCountComboBox();

            var drives = DriveInfo.GetDrives().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var driveItems = drives.Select(d => new DriveComboBoxItem(d)).ToList();
            DriveSelectComboBox.ItemsSource = driveItems;
            DriveSelectComboBox.DisplayMemberPath = nameof(DriveComboBoxItem.DisplayName);
            DriveSelectComboBox.SelectionChanged += DriveSelectComboBox_SelectionChanged;

            if (_initialPaths.Any())
            {
                string initialDrive = Path.GetPathRoot(_initialPaths.First()) ?? string.Empty;
                var driveToSelect = driveItems.FirstOrDefault(d => d.Drive.Name.Equals(initialDrive, StringComparison.OrdinalIgnoreCase));
                if (driveToSelect != null)
                {
                    DriveSelectComboBox.SelectedItem = driveToSelect;
                }
                else if (drives.Count != 0)
                {
                    DriveSelectComboBox.SelectedIndex = 0;
                }
            }
            else if (drives.Count != 0)
            {
                DriveSelectComboBox.SelectedIndex = 0;
            }
        }

        private sealed class DriveComboBoxItem
        {
            public DriveInfo Drive { get; }
            public string DisplayName { get; }

            public DriveComboBoxItem(DriveInfo drive)
            {
                Drive = drive;

                string name = drive.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string display = name;
                try
                {
                    if (drive.IsReady && !string.IsNullOrWhiteSpace(drive.VolumeLabel))
                    {
                        display = $"{name} ({drive.VolumeLabel})";
                    }
                }
                catch
                {
                }

                DisplayName = display;
            }
        }

        private async void DriveSelectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DriveSelectComboBox.SelectedItem is DriveComboBoxItem selected)
            {
                var selectedDrive = selected.Drive;
                LogThreadGuidance(selectedDrive);

                if (!selectedDrive.IsReady)
                {
                    LogMessage($"Drive '{selectedDrive.Name}' is not ready or not accessible.");
                    SelectedItemsTreeView.ItemsSource = null;
                    return;
                }

                SelectedItemsTreeView.ItemsSource = null;
                await RefreshTreeView(selectedDrive.Name);
            }
        }

        private static int GetSystemMaxThreads()
        {
            return Math.Max(1, Environment.ProcessorCount - 2);
        }

        private void InitializeThreadCountComboBox()
        {
            int maxThreads = GetSystemMaxThreads();

            ThreadCountComboBox.ItemsSource = Enumerable.Range(1, maxThreads).ToList();
            ThreadCountComboBox.SelectedItem = maxThreads;
        }

        private int GetSelectedThreadCountClamped()
        {
            int maxThreads = GetSystemMaxThreads();

            if (ThreadCountComboBox.SelectedItem is int selected)
            {
                if (selected < 1) return 1;
                if (selected > maxThreads) return maxThreads;
                return selected;
            }

            if (ThreadCountComboBox.SelectedItem is string s && int.TryParse(s, out selected))
            {
                if (selected < 1) return 1;
                if (selected > maxThreads) return maxThreads;
                return selected;
            }

            return maxThreads;
        }

        private void LogThreadGuidance(DriveInfo selectedDrive)
        {
            int systemMax = GetSystemMaxThreads();
            int nvmeSuggested = systemMax;
            int sataSsdSuggested = Math.Min(6, systemMax);
            int hddSuggested = 1;

            string message = $"{selectedDrive.DriveType} detected. Suggested maximum threads: NVMe={nvmeSuggested}, SATA SSD={sataSsdSuggested}, HDD={hddSuggested}. System max={systemMax}.";
            LogMessageBold(message);
        }

        private void LogMessageBold(string message)
        {
            Dispatcher.Invoke(() =>
            {
                var item = new ListBoxItem
                {
                    Content = $"{DateTime.Now:HH:mm:ss}: {message}",
                    FontWeight = FontWeights.Bold
                };
                StatusLogListBox.Items.Add(item);
                StatusLogListBox.ScrollIntoView(item);
            });
        }

        private void SetUiForOperationState(bool isRunning, int workerCount = 0)
        {
            if (isRunning)
            {
                DriveSelectComboBox.IsEnabled = false;
                _totalFilesProcessed = 0;
                _workerSlots.Clear();
                for (int i = 0; i < workerCount; i++)
                {
                    _workerSlots.Add(new WorkerSlot { SlotId = i });
                }
                OverallProgressBar.Value = 0;
                OverallProgressText.Text = "0/0";
                SelectedItemsTreeView.Visibility = Visibility.Collapsed;
                ProgressViewGrid.Visibility = Visibility.Visible;
                CreateButton.IsEnabled = false;
                VerifyButton.IsEnabled = false;
                CompareFoldersButton.IsEnabled = false;
                CountEntriesButton.IsEnabled = false;
                NewFileActionComboBox.IsEnabled = false;
                ExistingShaFileActionsComboBox.IsEnabled = false;
                CancelOperationButton.IsEnabled = true;
            }
            else
            {
                DriveSelectComboBox.IsEnabled = true;
                ProgressViewGrid.Visibility = Visibility.Collapsed;
                SelectedItemsTreeView.Visibility = Visibility.Visible;
                CreateButton.IsEnabled = true;
                VerifyButton.IsEnabled = true;
                CompareFoldersButton.IsEnabled = true;
                CountEntriesButton.IsEnabled = true;
                NewFileActionComboBox.IsEnabled = true;
                ExistingShaFileActionsComboBox.IsEnabled = true;
                CancelOperationButton.IsEnabled = false;
                CancelOperationButton.Content = "Cancel Operation";
                CancelOperationButton.ClearValue(BackgroundProperty);
            }
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        }

        private async void CreateButton_Click(object sender, RoutedEventArgs e)
        {
            _cancellationState = CancellationState.NotCancelled;
            _cancellationTokenSource = new CancellationTokenSource();
            _forceCancellationTokenSource = new CancellationTokenSource();
            var gracefulToken = _cancellationTokenSource.Token;
            var forceToken = _forceCancellationTokenSource.Token;
            var stopwatch = new Stopwatch();
            var hashingResults = new ConcurrentDictionary<string, string>();
            var hashingErrorDetails = new ConcurrentBag<string>();
            IProgress<ProgressReport> progress = new Progress<ProgressReport>(report =>
            {
                switch (report.Type)
                {
                    case ProgressReport.ReportType.OverallFileCompleted:
                        OverallProgressBar.Value = report.OverallProgress;
                        OverallProgressText.Text = $"{report.OverallProgress}/{OverallProgressBar.Maximum}";
                        break;

                    case ProgressReport.ReportType.SlotUpdate:
                        if (report.SlotIndex < _workerSlots.Count)
                        {
                            var slot = _workerSlots[report.SlotIndex];
                            switch (report.UpdateType)
                            {
                                case ProgressReport.SlotUpdateType.Started:
                                    slot.FileName = report.FileName;
                                    slot.FileSize = report.FileSize;
                                    slot.ShowProgressBar = report.FullFileSize > LargeFileThreshold;
                                    slot.ProgressPercentage = 0;
                                    slot.StatusText = "Starting...";
                                    break;
                                case ProgressReport.SlotUpdateType.InProgress:
                                    slot.ProgressPercentage = report.ProgressPercentage;
                                    slot.StatusText = report.StatusText;
                                    break;
                                case ProgressReport.SlotUpdateType.Finished:
                                    slot.Clear();
                                    break;
                            }
                        }
                        break;
                    case ProgressReport.ReportType.StatusMessage:
                        LogMessage(report.Message);
                        break;
                }
            });
            try
            {
                stopwatch.Start();
                LogMessage("Collecting files for Create/Update operation...");
                var rootItems = SelectedItemsTreeView.ItemsSource as ObservableCollection<FileSystemItem> ?? [];
                var checkedItemsList = new List<FileSystemItem>();
                IsProgrammaticallyUpdatingTree = true;
                try
                {
                    await foreach (var item in GetCheckedItems(rootItems, gracefulToken))
                    {
                        checkedItemsList.Add(item);
                    }
                }
                finally
                {
                    IsProgrammaticallyUpdatingTree = false;
                }
                gracefulToken.ThrowIfCancellationRequested();
                var checkedFiles = checkedItemsList
                    .Where(item => !item.IsDirectory && !item.FullPath.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (checkedFiles.Count == 0)
                {
                    LogMessage("No eligible files selected for Create/Update operation.");
                    return;
                }

                int workerThreads = GetSelectedThreadCountClamped();
                SetUiForOperationState(true, workerThreads);

                OverallProgressBar.Maximum = checkedFiles.Count;
                OverallProgressText.Text = $"0/{checkedFiles.Count}";
                LogMessage($"Found {checkedFiles.Count} files to process. Using {workerThreads} worker threads.");
                var fileQueue = new ConcurrentQueue<FileSystemItem>(checkedFiles);

                var consumerTasks = new List<Task>();
                for (int i = 0; i < workerThreads; i++)
                {
                    int slotIndex = i;
                    consumerTasks.Add(Task.Run(async () =>
                    {
                        while (fileQueue.TryDequeue(out var fileItem))
                        {
                            gracefulToken.ThrowIfCancellationRequested();
                            var fileInfo = new FileInfo(fileItem.FullPath);
                            long fileSize = fileInfo.Length;

                            progress.Report(new ProgressReport
                            {
                                Type = ProgressReport.ReportType.SlotUpdate,
                                UpdateType = ProgressReport.SlotUpdateType.Started,
                                SlotIndex = slotIndex,
                                FileName = fileItem.Name,
                                FileSize = FormatFileSize(fileSize),
                                FullFileSize = fileSize
                            });

                            try
                            {
                                string hash = await CalculateSha256(fileItem.FullPath, forceToken, new Progress<ProgressReport>(p =>
                                {
                                    p.SlotIndex = slotIndex;
                                    progress.Report(p);
                                }));
                                hashingResults[fileItem.FullPath] = hash;

                                progress.Report(new ProgressReport
                                {
                                    Type = ProgressReport.ReportType.SlotUpdate,
                                    UpdateType = ProgressReport.SlotUpdateType.InProgress,
                                    SlotIndex = slotIndex,
                                    ProgressPercentage = 100,
                                    StatusText = "Done"
                                });
                                await Task.Delay(250, gracefulToken);
                            }
                            catch (Exception fileEx) when (fileEx is not OperationCanceledException)
                            {
                                hashingErrorDetails.Add($"{fileItem.FullPath}: {fileEx.Message}");
                                var slot = _workerSlots[slotIndex];
                                progress.Report(new ProgressReport
                                {
                                    Type = ProgressReport.ReportType.SlotUpdate,
                                    UpdateType = ProgressReport.SlotUpdateType.InProgress,
                                    SlotIndex = slotIndex,
                                    ProgressPercentage = slot.ProgressPercentage,
                                    StatusText = "Error"
                                });
                                await Task.Delay(500, gracefulToken);
                            }
                            finally
                            {
                                int currentProgress = Interlocked.Increment(ref _totalFilesProcessed);
                                progress.Report(new ProgressReport { Type = ProgressReport.ReportType.OverallFileCompleted, OverallProgress = currentProgress });
                            }
                        }
                        progress.Report(new ProgressReport { Type = ProgressReport.ReportType.SlotUpdate, UpdateType = ProgressReport.SlotUpdateType.Finished, SlotIndex = slotIndex });
                    }, gracefulToken));
                }
                await Task.WhenAll(consumerTasks);
                SaveHashingResults(hashingResults, hashingErrorDetails);

                LogMessage($"Create/Update operation complete. Files processed: {_totalFilesProcessed}.");
                ReportErrors("Processing", hashingErrorDetails);
                if (DriveSelectComboBox.SelectedItem is DriveInfo selectedDrive)
                {
                    await RefreshTreeView(selectedDrive.Name);
                }
            }
            catch (OperationCanceledException)
            {
                if (_cancellationState == CancellationState.ForcedCancelRequested)
                {
                    LogMessage("Operation was aborted by the user. No partial results were saved.");
                }
                else
                {
                    LogMessage("Operation was canceled by the user. Saving partial results...");
                    SaveHashingResults(hashingResults, hashingErrorDetails);
                }
                LogMessage($"Summary: {_totalFilesProcessed} files were processed before cancellation.");
                ReportErrors("Processing", hashingErrorDetails);
            }
            catch (Exception ex)
            {
                LogMessage($"Error during Create/Update operation: {ex.Message}");
            }
            finally
            {
                stopwatch.Stop();
                LogMessage($"Total elapsed time: {stopwatch.Elapsed:g}");
                SetUiForOperationState(false);
                _cancellationState = CancellationState.NotCancelled;
                _cancellationTokenSource?.Dispose();
                _forceCancellationTokenSource?.Dispose();
            }
        }

        // --- NEW: Helper method to save hash results to disk ---
        private void SaveHashingResults(ConcurrentDictionary<string, string> hashingResults, ConcurrentBag<string> errorBag)
        {
            if (hashingResults.IsEmpty)
            {
                return;
            }

            LogMessage($"Writing {hashingResults.Count} hash entries to disk...");
            bool makeHidden = false;
            Dispatcher.Invoke(() => makeHidden = NewFileActionComboBox.SelectedIndex == 0);

            var resultsByDirectory = hashingResults.Keys.GroupBy(path => Path.GetDirectoryName(path) ?? Path.GetPathRoot(path)!);
            foreach (var dirGroup in resultsByDirectory)
            {
                string directoryPath = dirGroup.Key;
                string sha256FileName = Path.GetPathRoot(directoryPath) == directoryPath ? ".sha256" : Path.GetFileName(directoryPath) + ".sha256";
                string sha256FilePath = Path.Combine(directoryPath, sha256FileName);
                try
                {
                    Dictionary<string, Sha256Entry> shaEntries = ReadSha256File(sha256FilePath);
                    foreach (string filePath in dirGroup)
                    {
                        if (hashingResults.TryGetValue(filePath, out var hash))
                        {
                            shaEntries[Path.GetFileName(filePath)] = new Sha256Entry(hash, Path.GetFileName(filePath));
                        }
                    }

                    if (File.Exists(sha256FilePath)) { File.SetAttributes(sha256FilePath, File.GetAttributes(sha256FilePath) & ~FileAttributes.ReadOnly & ~FileAttributes.Hidden); }
                    WriteSha256File(sha256FilePath, [.. shaEntries.Values]);
                    if (makeHidden) { File.SetAttributes(sha256FilePath, File.GetAttributes(sha256FilePath) | FileAttributes.Hidden); }
                }
                catch (Exception ex)
                {
                    errorBag.Add($"Error writing {sha256FilePath}: {ex.Message}");
                }
            }
            LogMessage("Finished writing entries.");
        }


        private async void VerifyButton_Click(object sender, RoutedEventArgs e)
        {
            _cancellationState = CancellationState.NotCancelled;
            _cancellationTokenSource = new CancellationTokenSource();
            _forceCancellationTokenSource = new CancellationTokenSource();
            var gracefulToken = _cancellationTokenSource.Token;
            var forceToken = _forceCancellationTokenSource.Token;
            var stopwatch = new Stopwatch();

            IProgress<ProgressReport> progress = new Progress<ProgressReport>(report =>
            {
                switch (report.Type)
                {
                    case ProgressReport.ReportType.OverallFileCompleted:
                        OverallProgressBar.Value = report.OverallProgress;
                        OverallProgressText.Text = $"{report.OverallProgress}/{OverallProgressBar.Maximum}";
                        break;

                    case ProgressReport.ReportType.SlotUpdate:
                        if (report.SlotIndex < _workerSlots.Count)
                        {
                            var slot = _workerSlots[report.SlotIndex];
                            switch (report.UpdateType)
                            {
                                case ProgressReport.SlotUpdateType.Started:
                                    slot.FileName = report.FileName;
                                    slot.FileSize = report.FileSize;
                                    slot.ShowProgressBar = report.FullFileSize > LargeFileThreshold;
                                    slot.ProgressPercentage = 0;
                                    slot.StatusText = "Verifying...";
                                    break;
                                case ProgressReport.SlotUpdateType.InProgress:
                                    slot.ProgressPercentage = report.ProgressPercentage;
                                    slot.StatusText = report.StatusText;
                                    break;
                                case ProgressReport.SlotUpdateType.Finished:
                                    slot.Clear();
                                    break;
                            }
                        }
                        break;
                }
            });

            int totalMatches = 0;
            var mismatchDetails = new ConcurrentBag<string>();
            var missingFromShaDetails = new ConcurrentBag<string>();
            var missingOnDiskDetails = new ConcurrentBag<string>();
            try
            {
                stopwatch.Start();
                LogMessage("Collecting files for Verify operation...");
                var rootItems = SelectedItemsTreeView.ItemsSource as ObservableCollection<FileSystemItem> ?? [];
                var checkedItemsList = new List<FileSystemItem>();
                IsProgrammaticallyUpdatingTree = true;
                try
                {
                    await foreach (var item in GetCheckedItems(rootItems, gracefulToken))
                    {
                        checkedItemsList.Add(item);
                    }
                }
                finally
                {
                    IsProgrammaticallyUpdatingTree = false;
                }
                gracefulToken.ThrowIfCancellationRequested();

                bool isFullDriveVerification = false;
                if (rootItems.Count == 1 && rootItems[0].IsChecked == true)
                {
                    isFullDriveVerification = true;
                }

                var checkedFiles = checkedItemsList
                    .Where(item => !item.IsDirectory && !item.FullPath.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (checkedFiles.Count == 0)
                {
                    LogMessage("No eligible files selected for Verify operation.");
                    return;
                }

                int workerThreads = GetSelectedThreadCountClamped();
                SetUiForOperationState(true, workerThreads);

                OverallProgressBar.Maximum = checkedFiles.Count;
                OverallProgressText.Text = $"0/{checkedFiles.Count}";
                LogMessage($"Found {checkedFiles.Count} files to verify. Using {workerThreads} worker threads.");
                var fileQueue = new ConcurrentQueue<FileSystemItem>(checkedFiles);

                var consumerTasks = new List<Task>();
                for (int i = 0; i < workerThreads; i++)
                {
                    int slotIndex = i;
                    consumerTasks.Add(Task.Run(async () =>
                    {
                        var shaFileCache = new Dictionary<string, Dictionary<string, Sha256Entry>>(StringComparer.OrdinalIgnoreCase);

                        while (fileQueue.TryDequeue(out var fileItem))
                        {
                            gracefulToken.ThrowIfCancellationRequested();
                            var fileInfo = new FileInfo(fileItem.FullPath);
                            long fileSize = fileInfo.Length;

                            progress.Report(new ProgressReport
                            {
                                Type = ProgressReport.ReportType.SlotUpdate,
                                UpdateType = ProgressReport.SlotUpdateType.Started,
                                SlotIndex = slotIndex,
                                FileName = fileItem.Name,
                                FileSize = FormatFileSize(fileSize),
                                FullFileSize = fileSize
                            });
                            try
                            {
                                string directoryOrRootPath = Path.GetDirectoryName(fileItem.FullPath) ?? Path.GetPathRoot(fileItem.FullPath)!;
                                if (!shaFileCache.TryGetValue(directoryOrRootPath, out Dictionary<string, Sha256Entry>? existingShaEntries))
                                {
                                    string sha256FileName = Path.GetPathRoot(directoryOrRootPath) == directoryOrRootPath ? ".sha256" : Path.GetFileName(directoryOrRootPath) + ".sha256";
                                    string sha256FilePath = Path.Combine(directoryOrRootPath, sha256FileName);
                                    existingShaEntries = ReadSha256File(sha256FilePath);
                                    shaFileCache[directoryOrRootPath] = existingShaEntries;
                                }

                                if (existingShaEntries.TryGetValue(fileItem.Name, out Sha256Entry? storedEntry))
                                {
                                    if (File.Exists(fileItem.FullPath))
                                    {
                                        string currentHash = await CalculateSha256(fileItem.FullPath, forceToken, new Progress<ProgressReport>(p =>
                                        {
                                            p.SlotIndex = slotIndex;
                                            progress.Report(p);
                                        }));
                                        if (string.Equals(currentHash, storedEntry.Hash, StringComparison.OrdinalIgnoreCase))
                                        {
                                            Interlocked.Increment(ref totalMatches);
                                            progress.Report(new ProgressReport { Type = ProgressReport.ReportType.SlotUpdate, UpdateType = ProgressReport.SlotUpdateType.InProgress, SlotIndex = slotIndex, ProgressPercentage = 100, StatusText = "Match" });
                                        }
                                        else
                                        {
                                            mismatchDetails.Add($"{fileItem.FullPath} (Hash mismatch)");
                                            progress.Report(new ProgressReport { Type = ProgressReport.ReportType.SlotUpdate, UpdateType = ProgressReport.SlotUpdateType.InProgress, SlotIndex = slotIndex, ProgressPercentage = 100, StatusText = "Mismatch" });
                                        }
                                    }
                                    else
                                    {
                                        missingOnDiskDetails.Add($"{fileItem.FullPath} (File not found but in .sha256)");
                                        progress.Report(new ProgressReport { Type = ProgressReport.ReportType.SlotUpdate, UpdateType = ProgressReport.SlotUpdateType.InProgress, SlotIndex = slotIndex, ProgressPercentage = 100, StatusText = "Missing" });
                                    }
                                }
                                else
                                {
                                    missingFromShaDetails.Add($"{fileItem.FullPath} (Not found in .sha256 file)");
                                    progress.Report(new ProgressReport { Type = ProgressReport.ReportType.SlotUpdate, UpdateType = ProgressReport.SlotUpdateType.InProgress, SlotIndex = slotIndex, ProgressPercentage = 100, StatusText = "Not in .sha" });
                                }
                                await Task.Delay(250, gracefulToken);
                            }
                            catch (Exception fileEx) when (fileEx is not OperationCanceledException)
                            {
                                mismatchDetails.Add($"{fileItem.FullPath}: {fileEx.Message}");
                                var slot = _workerSlots[slotIndex];
                                progress.Report(new ProgressReport { Type = ProgressReport.ReportType.SlotUpdate, UpdateType = ProgressReport.SlotUpdateType.InProgress, SlotIndex = slotIndex, ProgressPercentage = slot.ProgressPercentage, StatusText = "Error" });
                                await Task.Delay(500, gracefulToken);
                            }
                            finally
                            {
                                int currentProgress = Interlocked.Increment(ref _totalFilesProcessed);
                                progress.Report(new ProgressReport { Type = ProgressReport.ReportType.OverallFileCompleted, OverallProgress = currentProgress });
                            }
                        }
                        progress.Report(new ProgressReport { Type = ProgressReport.ReportType.SlotUpdate, UpdateType = ProgressReport.SlotUpdateType.Finished, SlotIndex = slotIndex });
                    }, gracefulToken));
                }

                await Task.WhenAll(consumerTasks);
                int totalMismatches = mismatchDetails.Count;
                int totalMissingFromSha = missingFromShaDetails.Count;
                int totalMissingOnDisk = missingOnDiskDetails.Count;
                LogMessage($"Verify operation complete. Total files verified: {_totalFilesProcessed}, Matches: {totalMatches}, Mismatches: {totalMismatches}, Missing from .sha256: {totalMissingFromSha}, Missing on disk: {totalMissingOnDisk}.");
                ReportErrors("Mismatched Hashes", mismatchDetails);
                ReportErrors("Missing from .sha256", missingFromShaDetails);
                ReportErrors("Missing on Disk", missingOnDiskDetails);

                if (totalMismatches == 0 && totalMissingFromSha == 0 && totalMissingOnDisk == 0)
                {
                    LogMessage("Successful comparison. No errors.");
                }

                if (isFullDriveVerification)
                {
                    try
                    {
                        bool isAllGood = totalMatches == _totalFilesProcessed &&
                                         totalMismatches == 0 &&
                                         totalMissingFromSha == 0 &&
                                         totalMissingOnDisk == 0;

                        string status = isAllGood ? "ALL GOOD" : "ERRORS";
                        string dateString = DateTime.Now.ToString("yyyy-MM-dd");
                        string fileName = $"{dateString} - LastFullSHA256Verify - {status}.txt";

                        string driveRootPath = Path.GetPathRoot(rootItems.First().FullPath)!;
                        string filePath = Path.Combine(driveRootPath, fileName);

                        var logContent = new StringBuilder();
                        foreach (var item in StatusLogListBox.Items)
                        {
                            logContent.AppendLine(GetLogItemText(item));
                        }

                        File.WriteAllText(filePath, logContent.ToString());
                        LogMessage($"Full drive verification log saved to: {filePath}");
                    }
                    catch (Exception logEx)
                    {
                        LogMessage($"Failed to save verification log file: {logEx.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (_cancellationState == CancellationState.ForcedCancelRequested)
                {
                    LogMessage("Operation was aborted by the user.");
                }
                else
                {
                    LogMessage("Operation was canceled by the user.");
                }
                int totalMismatches = mismatchDetails.Count;
                int totalMissingFromSha = missingFromShaDetails.Count;
                int totalMissingOnDisk = missingOnDiskDetails.Count;
                LogMessage($"Summary: {_totalFilesProcessed} files verified, {totalMatches} matches, {totalMismatches} mismatches before cancellation.");
            }
            catch (Exception ex)
            {
                LogMessage($"Error during Verify operation: {ex.Message}");
            }
            finally
            {
                stopwatch.Stop();
                LogMessage($"Total elapsed time: {stopwatch.Elapsed:g}");
                SetUiForOperationState(false);
                _cancellationState = CancellationState.NotCancelled;
                _cancellationTokenSource?.Dispose();
                _forceCancellationTokenSource?.Dispose();
            }
        }

        private async void ExistingShaFileActionsComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ExistingShaFileActionsComboBox.SelectedIndex <= 0) return;
            var selectedActionIndex = ExistingShaFileActionsComboBox.SelectedIndex;
            var actionText = ((ComboBoxItem)ExistingShaFileActionsComboBox.SelectedItem).Content.ToString() ?? "perform this action";
            var result = MessageBox.Show($"Are you sure you want to {actionText.ToLower()} for all selected items?",
                                         "Confirm Action", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (result != MessageBoxResult.OK)
            {
                LogMessage("Action canceled by user.");
                ExistingShaFileActionsComboBox.SelectedIndex = 0;
                return;
            }

            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;
            var stopwatch = new Stopwatch();
            int filesAffected = 0;
            try
            {
                stopwatch.Start();
                LogMessage($"Collecting items for: {actionText}...");
                var rootItems = SelectedItemsTreeView.ItemsSource as ObservableCollection<FileSystemItem> ?? [];
                var checkedItemsList = new List<FileSystemItem>();
                await foreach (var item in GetCheckedItems(rootItems, token))
                {
                    checkedItemsList.Add(item);
                }
                token.ThrowIfCancellationRequested();
                var directoriesToProcess = checkedItemsList
                    .Select(item => item.IsDirectory ? item.FullPath : Path.GetDirectoryName(item.FullPath))
                    .Where(dir => !string.IsNullOrEmpty(dir))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (directoriesToProcess.Count == 0)
                {
                    LogMessage("No eligible files or directories selected for this action.");
                    return;
                }

                SetUiForOperationState(true, 1);
                OverallProgressBar.Maximum = directoriesToProcess.Count;
                OverallProgressText.Text = $"0/{directoriesToProcess.Count}";
                LogMessage($"Found {directoriesToProcess.Count} directories to check. Starting...");

                var errorDetails = new List<string>();
                await Task.Run(() =>
                {
                    for (int i = 0; i < directoriesToProcess.Count; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        var directoryPath = directoriesToProcess[i];

                        string sha256FileName = Path.GetPathRoot(directoryPath) == directoryPath ? ".sha256" : Path.GetFileName(directoryPath) + ".sha256";
                        string sha256FilePath = Path.Combine(directoryPath!, sha256FileName);

                        _workerSlots[0].FileName = sha256FileName;
                        _workerSlots[0].FileSize = "";

                        if (File.Exists(sha256FilePath))
                        {
                            try
                            {
                                switch (selectedActionIndex)
                                {
                                    case 1: // Make HIDDEN
                                        File.SetAttributes(sha256FilePath, File.GetAttributes(sha256FilePath) | FileAttributes.Hidden);
                                        filesAffected++;
                                        break;
                                    case 2: // Make VISIBLE
                                        File.SetAttributes(sha256FilePath, File.GetAttributes(sha256FilePath) & ~FileAttributes.Hidden);
                                        filesAffected++;
                                        break;
                                    case 3: // DELETE
                                        File.SetAttributes(sha256FilePath, File.GetAttributes(sha256FilePath) & ~FileAttributes.ReadOnly & ~FileAttributes.Hidden);
                                        File.Delete(sha256FilePath);
                                        filesAffected++;
                                        break;
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                errorDetails.Add($"{sha256FilePath}: {ex.Message}");
                            }
                        }
                        int currentProgress = i + 1;
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            OverallProgressBar.Value = currentProgress;
                            OverallProgressText.Text = $"{currentProgress}/{directoriesToProcess.Count}";
                        });
                    }
                }, token);
                LogMessage($"Action '{actionText}' complete. Total .SHA256 files affected: {filesAffected}.");
                ReportErrors("File Action", errorDetails);
                if (DriveSelectComboBox.SelectedItem is DriveInfo selectedDrive)
                {
                    await RefreshTreeView(selectedDrive.Name);
                }
            }
            catch (OperationCanceledException)
            {
                LogMessage($"Action '{actionText}' was canceled by the user.");
                LogMessage($"Summary: {filesAffected} .sha256 files affected before cancellation.");
            }
            catch (Exception ex)
            {
                LogMessage($"Error during '{actionText}' operation: {ex.Message}");
            }
            finally
            {
                stopwatch.Stop();
                LogMessage($"Total elapsed time: {stopwatch.Elapsed:g}");
                SetUiForOperationState(false);
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                ExistingShaFileActionsComboBox.SelectedIndex = 0;
            }
        }

        private async void CountEntriesButton_Click(object sender, RoutedEventArgs e)
        {
            this.IsEnabled = false;
            Mouse.OverrideCursor = Cursors.Wait;

            try
            {
                LogMessage("Counting .sha256 entries for selected files...");
                var rootItems = SelectedItemsTreeView.ItemsSource as ObservableCollection<FileSystemItem> ?? [];
                var checkedItemsList = new List<FileSystemItem>();
                await foreach (var item in GetCheckedItems(rootItems, CancellationToken.None))
                {
                    checkedItemsList.Add(item);
                }

                var checkedFiles = checkedItemsList
                    .Where(item => !item.IsDirectory && !item.FullPath.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (checkedFiles.Count == 0)
                {
                    LogMessage("No eligible files selected to count.");
                    MessageBox.Show("No eligible files were selected.", "Count Entries", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                int filesWithEntry = 0;
                int filesWithoutEntry = 0;

                await Task.Run(() =>
                {
                    var shaFileCache = new Dictionary<string, Dictionary<string, Sha256Entry>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var fileItem in checkedFiles)
                    {
                        string directoryPath = Path.GetDirectoryName(fileItem.FullPath) ?? string.Empty;
                        if (string.IsNullOrEmpty(directoryPath)) continue;

                        if (!shaFileCache.TryGetValue(directoryPath, out Dictionary<string, Sha256Entry>? value))
                        {
                            string sha256FileName = Path.GetPathRoot(directoryPath) == directoryPath ? ".sha256" : Path.GetFileName(directoryPath) + ".sha256";
                            string sha256FilePath = Path.Combine(directoryPath, sha256FileName);
                            value = ReadSha256File(sha256FilePath);
                            shaFileCache[directoryPath] = value;
                        }

                        if (value.ContainsKey(fileItem.Name))
                        {
                            filesWithEntry++;
                        }
                        else
                        {
                            filesWithoutEntry++;
                        }
                    }
                });
                LogMessage($"Counting complete. Total files checked: {checkedFiles.Count}");
                LogMessage($"Files WITH an entry in a .sha256 file: {filesWithEntry}");
                LogMessage($"Files WITHOUT an entry in a .sha256 file: {filesWithoutEntry}");

                string message = $"Counting complete for {checkedFiles.Count} selected files.\n\n" +
                                 $"Files with an entry: {filesWithEntry}\n" +
                                 $"Files without an entry: {filesWithoutEntry}\n\n" +
                                 "IMPORTANT: This operation did NOT verify the hash values. It only checked for the presence of an entry in the corresponding .sha256 file.";
                MessageBox.Show(this, message, "Count .sha256 Entries Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally
            {
                this.IsEnabled = true;
                Mouse.OverrideCursor = null;
            }
        }

        private async void CompareFoldersButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog1 = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Choose the first directory that you wish to compare against or click cancel to cancel the entire compare operation"
            };

            if (dialog1.ShowDialog() != true)
            {
                LogMessage("Folder comparison cancelled. First directory selection was cancelled.");
                return;
            }

            string firstFolder = dialog1.FolderName;

            var dialog2 = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Choose the directory to compare to the previously chosen directory or click cancel to cancel the entire compare operation"
            };

            if (dialog2.ShowDialog() != true)
            {
                LogMessage("Folder comparison cancelled. Second directory selection was cancelled.");
                return;
            }

            string secondFolder = dialog2.FolderName;

            if (string.Equals(firstFolder, secondFolder, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Please choose two different folders to compare.", "Invalid Comparison", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var reviewDialog = new CompareFoldersDialog(firstFolder, secondFolder)
            {
                Owner = this
            };

            if (reviewDialog.ShowDialog() == true)
            {
                var type = reviewDialog.SelectedComparisonType;
                var action = reviewDialog.SelectedSha256Action;

                if (type != ComparisonType.NamesSizesAndDates && type != ComparisonType.Crc64 && type != ComparisonType.Sha256)
                {
                    MessageBox.Show("This comparison type is not yet implemented.", "Compare Folders", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _cancellationState = CancellationState.NotCancelled;
                _cancellationTokenSource = new CancellationTokenSource();
                _forceCancellationTokenSource = new CancellationTokenSource();
                var gracefulToken = _cancellationTokenSource.Token;
                var forceToken = _forceCancellationTokenSource.Token;
                var stopwatch = new Stopwatch();

                int workerThreads = (type == ComparisonType.Crc64 || type == ComparisonType.Sha256) ? GetSelectedThreadCountClamped() : 0;
                SetUiForOperationState(true, workerThreads);

                IProgress<ProgressReport> progress = new Progress<ProgressReport>(report =>
                {
                    switch (report.Type)
                    {
                        case ProgressReport.ReportType.OverallFileCompleted:
                            OverallProgressBar.Value = report.OverallProgress;
                            OverallProgressText.Text = $"{report.OverallProgress}/{OverallProgressBar.Maximum}";
                            break;
                        case ProgressReport.ReportType.SlotUpdate:
                            if (report.SlotIndex < _workerSlots.Count)
                            {
                                var slot = _workerSlots[report.SlotIndex];
                                switch (report.UpdateType)
                                {
                                    case ProgressReport.SlotUpdateType.Started:
                                        slot.FileName = report.FileName;
                                        slot.FileSize = report.FileSize;
                                        slot.ShowProgressBar = report.FullFileSize > LargeFileThreshold;
                                        slot.ProgressPercentage = 0;
                                        slot.StatusText = "Starting...";
                                        break;
                                    case ProgressReport.SlotUpdateType.InProgress:
                                        slot.ProgressPercentage = report.ProgressPercentage;
                                        slot.StatusText = report.StatusText;
                                        break;
                                    case ProgressReport.SlotUpdateType.Finished:
                                        slot.Clear();
                                        break;
                                }
                            }
                            break;
                        case ProgressReport.ReportType.StatusMessage:
                            LogMessage(report.Message);
                            break;
                    }
                });

                try
                {
                    stopwatch.Start();
                    LogMessage("Starting folder comparison...");
                    LogMessage($"Folder 1: {firstFolder}");
                    LogMessage($"Folder 2: {secondFolder}");
                    LogMessage($"Comparison Type: {type}");

                    bool skipLargeFiles = false;
                    Application.Current.Dispatcher.Invoke(() => skipLargeFiles = SkipLargeFilesCheckBox.IsChecked ?? false);

                    await Task.Run(async () =>
                    {
                        // 1. Scan Folder A
                        progress.Report(new ProgressReport { Type = ProgressReport.ReportType.StatusMessage, Message = $"Scanning '{firstFolder}'..." });
                        var filesA = await ScanFolderAsync(firstFolder, skipLargeFiles, gracefulToken);
                        progress.Report(new ProgressReport { Type = ProgressReport.ReportType.StatusMessage, Message = $"Found {filesA.Count} files in '{firstFolder}'." });

                        // 2. Scan Folder B
                        progress.Report(new ProgressReport { Type = ProgressReport.ReportType.StatusMessage, Message = $"Scanning '{secondFolder}'..." });
                        var filesB = await ScanFolderAsync(secondFolder, skipLargeFiles, gracefulToken);
                        progress.Report(new ProgressReport { Type = ProgressReport.ReportType.StatusMessage, Message = $"Found {filesB.Count} files in '{secondFolder}'." });

                        int matches = 0;
                        var onlyInA = new List<string>();
                        var onlyInB = new List<string>();

                        if (type == ComparisonType.NamesSizesAndDates)
                        {
                            var mismatches = new List<string>();

                            // Setup progress bar maximum
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                OverallProgressBar.Maximum = filesA.Count + filesB.Count;
                                OverallProgressText.Text = $"0/{OverallProgressBar.Maximum}";
                            });

                            int processedCount = 0;
                            progress.Report(new ProgressReport { Type = ProgressReport.ReportType.StatusMessage, Message = "Comparing file names, sizes, and modification dates..." });

                            // Phase 1: Compare A to B
                            foreach (var kvp in filesA)
                            {
                                gracefulToken.ThrowIfCancellationRequested();
                                string relativePath = kvp.Key;
                                var metaA = kvp.Value;

                                if (filesB.TryGetValue(relativePath, out var metaB))
                                {
                                    bool sizeMatch = metaA.Size == metaB.Size;
                                    bool dateMatch = Math.Abs((metaA.LastWriteTimeUtc - metaB.LastWriteTimeUtc).TotalSeconds) <= 2;

                                    if (sizeMatch && dateMatch)
                                    {
                                        matches++;
                                    }
                                    else
                                    {
                                        var reasons = new List<string>();
                                        if (!sizeMatch) reasons.Add($"size differs ({FormatFileSize(metaA.Size)} vs {FormatFileSize(metaB.Size)})");
                                        if (!dateMatch) reasons.Add($"date differs ({metaA.LastWriteTimeUtc.ToLocalTime()} vs {metaB.LastWriteTimeUtc.ToLocalTime()})");
                                        mismatches.Add($"{relativePath} ({string.Join(", ", reasons)})");
                                    }
                                }
                                else
                                {
                                    onlyInA.Add(relativePath);
                                }

                                processedCount++;
                                progress.Report(new ProgressReport { Type = ProgressReport.ReportType.OverallFileCompleted, OverallProgress = processedCount });
                            }

                            // Phase 2: Check for files in B only
                            foreach (var kvp in filesB)
                            {
                                gracefulToken.ThrowIfCancellationRequested();
                                string relativePath = kvp.Key;

                                if (!filesA.ContainsKey(relativePath))
                                {
                                    onlyInB.Add(relativePath);
                                }

                                processedCount++;
                                progress.Report(new ProgressReport { Type = ProgressReport.ReportType.OverallFileCompleted, OverallProgress = processedCount });
                            }

                            // Logging results
                            progress.Report(new ProgressReport { Type = ProgressReport.ReportType.StatusMessage, Message = "Folder comparison complete." });
                            progress.Report(new ProgressReport { Type = ProgressReport.ReportType.StatusMessage, Message = $"Matches: {matches}" });
                            progress.Report(new ProgressReport { Type = ProgressReport.ReportType.StatusMessage, Message = $"Mismatches (Size/Date): {mismatches.Count}" });
                            progress.Report(new ProgressReport { Type = ProgressReport.ReportType.StatusMessage, Message = $"Only in Folder A: {onlyInA.Count}" });
                            progress.Report(new ProgressReport { Type = ProgressReport.ReportType.StatusMessage, Message = $"Only in Folder B: {onlyInB.Count}" });

                            if (mismatches.Count == 0 && onlyInA.Count == 0 && onlyInB.Count == 0)
                            {
                                progress.Report(new ProgressReport { Type = ProgressReport.ReportType.StatusMessage, Message = "Successful comparison. No errors." });
                            }

                            // Report detailed errors (up to 5 samples each)
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                ReportErrors("Mismatched (Size/Date)", mismatches);
                                ReportErrors("Only in Folder A", onlyInA);
                                ReportErrors("Only in Folder B", onlyInB);
                            });

                            // Show MessageBox summary on UI thread
                            string summaryMessage = $"Comparison complete for folders:\n" +
                                                   $"1: {firstFolder}\n" +
                                                   $"2: {secondFolder}\n\n" +
                                                   $"Matches: {matches}\n" +
                                                   $"Mismatches (Size/Date): {mismatches.Count}\n" +
                                                   $"Only in Folder A: {onlyInA.Count}\n" +
                                                   $"Only in Folder B: {onlyInB.Count}";
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                MessageBox.Show(this, summaryMessage, "Comparison Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                            });
                        }
                        else if (type == ComparisonType.Crc64)
                        {
                            var mismatchesCrc = new ConcurrentBag<string>();
                            var equalSizePairs = new List<(FileComparisonMetadata A, FileComparisonMetadata B)>();

                            progress.Report(new ProgressReport { Type = ProgressReport.ReportType.StatusMessage, Message = "Matching files and running size pre-checks..." });

                            // Phase 1: Match A to B and do size pre-checks
                            foreach (var kvp in filesA)
                            {
                                gracefulToken.ThrowIfCancellationRequested();
                                string relativePath = kvp.Key;
                                var metaA = kvp.Value;

                                if (filesB.TryGetValue(relativePath, out var metaB))
                                {
                                    if (metaA.Size == metaB.Size)
                                    {
                                        equalSizePairs.Add((metaA, metaB));
                                    }
                                    else
                                    {
                                        mismatchesCrc.Add($"{relativePath} (size differs: {FormatFileSize(metaA.Size)} vs {FormatFileSize(metaB.Size)})");
                                    }
                                }
                                else
                                {
                                    onlyInA.Add(relativePath);
                                }
                            }

                            // Phase 2: Check for files in B only
                            foreach (var kvp in filesB)
                            {
                                gracefulToken.ThrowIfCancellationRequested();
                                string relativePath = kvp.Key;

                                if (!filesA.ContainsKey(relativePath))
                                {
                                    onlyInB.Add(relativePath);
                                }
                            }

                            if (equalSizePairs.Count > 0)
                            {
                                progress.Report(new ProgressReport { Type = ProgressReport.ReportType.StatusMessage, Message = $"Calculating and comparing CRC64 checksums for {equalSizePairs.Count} files using {workerThreads} worker threads..." });

                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    OverallProgressBar.Maximum = equalSizePairs.Count;
                                    OverallProgressText.Text = $"0/{OverallProgressBar.Maximum}";
                                });

                                int processedCount = 0;
                                var pairQueue = new ConcurrentQueue<(FileComparisonMetadata A, FileComparisonMetadata B)>(equalSizePairs);
                                var consumerTasks = new List<Task>();

                                for (int i = 0; i < workerThreads; i++)
                                {
                                    int slotIndex = i;
                                    consumerTasks.Add(Task.Run(async () =>
                                    {
                                        while (pairQueue.TryDequeue(out var pair))
                                        {
                                            gracefulToken.ThrowIfCancellationRequested();
                                            var metaA = pair.A;
                                            var metaB = pair.B;

                                            progress.Report(new ProgressReport
                                            {
                                                Type = ProgressReport.ReportType.SlotUpdate,
                                                UpdateType = ProgressReport.SlotUpdateType.Started,
                                                SlotIndex = slotIndex,
                                                FileName = metaA.RelativePath,
                                                FileSize = FormatFileSize(metaA.Size),
                                                FullFileSize = metaA.Size
                                            });

                                            try
                                            {
                                                progress.Report(new ProgressReport
                                                {
                                                    Type = ProgressReport.ReportType.SlotUpdate,
                                                    UpdateType = ProgressReport.SlotUpdateType.InProgress,
                                                    SlotIndex = slotIndex,
                                                    ProgressPercentage = 0,
                                                    StatusText = "CRC64 A..."
                                                });
                                                ulong crcA = await Crc64.CalculateAsync(metaA.FullPath, forceToken, progress, slotIndex);

                                                progress.Report(new ProgressReport
                                                {
                                                    Type = ProgressReport.ReportType.SlotUpdate,
                                                    UpdateType = ProgressReport.SlotUpdateType.InProgress,
                                                    SlotIndex = slotIndex,
                                                    ProgressPercentage = 0,
                                                    StatusText = "CRC64 B..."
                                                });
                                                ulong crcB = await Crc64.CalculateAsync(metaB.FullPath, forceToken, progress, slotIndex);

                                                if (crcA == crcB)
                                                {
                                                    Interlocked.Increment(ref matches);
                                                    progress.Report(new ProgressReport
                                                    {
                                                        Type = ProgressReport.ReportType.SlotUpdate,
                                                        UpdateType = ProgressReport.SlotUpdateType.InProgress,
                                                        SlotIndex = slotIndex,
                                                        ProgressPercentage = 100,
                                                        StatusText = "Match"
                                                    });
                                                }
                                                else
                                                {
                                                    mismatchesCrc.Add($"{metaA.RelativePath} (CRC64 mismatch: {crcA:X16} vs {crcB:X16})");
                                                    progress.Report(new ProgressReport
                                                    {
                                                        Type = ProgressReport.ReportType.SlotUpdate,
                                                        UpdateType = ProgressReport.SlotUpdateType.InProgress,
                                                        SlotIndex = slotIndex,
                                                        ProgressPercentage = 100,
                                                        StatusText = "Mismatch"
                                                    });
                                                }

                                                await Task.Delay(250, gracefulToken);
                                            }
                                            catch (Exception fileEx) when (fileEx is not OperationCanceledException)
                                            {
                                                mismatchesCrc.Add($"{metaA.RelativePath} (Error: {fileEx.Message})");
                                                progress.Report(new ProgressReport
                                                {
                                                    Type = ProgressReport.ReportType.SlotUpdate,
                                                    UpdateType = ProgressReport.SlotUpdateType.InProgress,
                                                    SlotIndex = slotIndex,
                                                    ProgressPercentage = 100,
                                                    StatusText = "Error"
                                                });
                                                await Task.Delay(500, gracefulToken);
                                            }
                                            finally
                                            {
                                                int currentProgress = Interlocked.Increment(ref processedCount);
                                                progress.Report(new ProgressReport
                                                {
                                                    Type = ProgressReport.ReportType.OverallFileCompleted,
                                                    OverallProgress = currentProgress
                                                });
                                            }
                                        }

                                        progress.Report(new ProgressReport
                                        {
                                            Type = ProgressReport.ReportType.SlotUpdate,
                                            UpdateType = ProgressReport.SlotUpdateType.Finished,
                                            SlotIndex = slotIndex
                                        });
                                    }, gracefulToken));
                                }

                                await Task.WhenAll(consumerTasks);
                            }
                            else
                            {
                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    OverallProgressBar.Maximum = 1;
                                    OverallProgressBar.Value = 1;
                                    OverallProgressText.Text = "1/1";
                                });
                            }

                            // Logging results
                            progress.Report(new ProgressReport { Type = ProgressReport.ReportType.StatusMessage, Message = "CRC64 folder comparison complete." });
                            progress.Report(new ProgressReport { Type = ProgressReport.ReportType.StatusMessage, Message = $"Matches: {matches}" });
                            progress.Report(new ProgressReport { Type = ProgressReport.ReportType.StatusMessage, Message = $"Mismatches (Size/CRC64): {mismatchesCrc.Count}" });
                            progress.Report(new ProgressReport { Type = ProgressReport.ReportType.StatusMessage, Message = $"Only in Folder A: {onlyInA.Count}" });
                            progress.Report(new ProgressReport { Type = ProgressReport.ReportType.StatusMessage, Message = $"Only in Folder B: {onlyInB.Count}" });

                            if (mismatchesCrc.IsEmpty && onlyInA.Count == 0 && onlyInB.Count == 0)
                            {
                                progress.Report(new ProgressReport { Type = ProgressReport.ReportType.StatusMessage, Message = "Successful comparison. No errors." });
                            }

                            // Report detailed errors (up to 5 samples each)
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                ReportErrors("Mismatched (Size/CRC64)", mismatchesCrc);
                                ReportErrors("Only in Folder A", onlyInA);
                                ReportErrors("Only in Folder B", onlyInB);
                            });

                            // Show MessageBox summary on UI thread
                            string summaryMessage = $"CRC64 Comparison complete for folders:\n" +
                                                   $"1: {firstFolder}\n" +
                                                   $"2: {secondFolder}\n\n" +
                                                   $"Matches: {matches}\n" +
                                                   $"Mismatches (Size/CRC64): {mismatchesCrc.Count}\n" +
                                                   $"Only in Folder A: {onlyInA.Count}\n" +
                                                   $"Only in Folder B: {onlyInB.Count}";
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                MessageBox.Show(this, summaryMessage, "CRC64 Comparison Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                            });
                        }
                        else if (type == ComparisonType.Sha256)
                        {
                            var mismatchesSha = new ConcurrentBag<string>();
                            var filesToHash = new List<FileComparisonMetadata>();

                            progress.Report(new ProgressReport { Type = ProgressReport.ReportType.StatusMessage, Message = "Matching files and running size pre-checks..." });

                            // Phase 1: Match A to B and determine what needs hashing
                            foreach (var kvp in filesA)
                            {
                                gracefulToken.ThrowIfCancellationRequested();
                                string relativePath = kvp.Key;
                                var metaA = kvp.Value;

                                if (filesB.TryGetValue(relativePath, out var metaB))
                                {
                                    if (metaA.Size == metaB.Size)
                                    {
                                        filesToHash.Add(metaA);
                                        filesToHash.Add(metaB);
                                    }
                                    else
                                    {
                                        mismatchesSha.Add($"{relativePath} (size differs: {FormatFileSize(metaA.Size)} vs {FormatFileSize(metaB.Size)})");
                                        if (action != Sha256Action.CompareOnly)
                                        {
                                            filesToHash.Add(metaA);
                                            filesToHash.Add(metaB);
                                        }
                                    }
                                }
                                else
                                {
                                    onlyInA.Add(relativePath);
                                    if (action != Sha256Action.CompareOnly)
                                    {
                                        filesToHash.Add(metaA);
                                    }
                                }
                            }

                            // Phase 2: Check for files in B only
                            foreach (var kvp in filesB)
                            {
                                gracefulToken.ThrowIfCancellationRequested();
                                string relativePath = kvp.Key;
                                var metaB = kvp.Value;

                                if (!filesA.ContainsKey(relativePath))
                                {
                                    onlyInB.Add(relativePath);
                                    if (action != Sha256Action.CompareOnly)
                                    {
                                        filesToHash.Add(metaB);
                                    }
                                }
                            }

                            var uniqueFilesToHash = filesToHash.Distinct().ToList();

                            if (uniqueFilesToHash.Count > 0)
                            {
                                progress.Report(new ProgressReport { Type = ProgressReport.ReportType.StatusMessage, Message = $"Calculating SHA256 hashes for {uniqueFilesToHash.Count} files using {workerThreads} worker threads..." });

                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    OverallProgressBar.Maximum = uniqueFilesToHash.Count;
                                    OverallProgressText.Text = $"0/{OverallProgressBar.Maximum}";
                                });

                                int processedCount = 0;
                                var fileQueue = new ConcurrentQueue<FileComparisonMetadata>(uniqueFilesToHash);
                                var consumerTasks = new List<Task>();

                                for (int i = 0; i < workerThreads; i++)
                                {
                                    int slotIndex = i;
                                    consumerTasks.Add(Task.Run(async () =>
                                    {
                                        while (fileQueue.TryDequeue(out var meta))
                                        {
                                            gracefulToken.ThrowIfCancellationRequested();

                                            progress.Report(new ProgressReport
                                            {
                                                Type = ProgressReport.ReportType.SlotUpdate,
                                                UpdateType = ProgressReport.SlotUpdateType.Started,
                                                SlotIndex = slotIndex,
                                                FileName = meta.RelativePath,
                                                FileSize = FormatFileSize(meta.Size),
                                                FullFileSize = meta.Size
                                            });

                                            try
                                            {
                                                string hash = await CalculateSha256(meta.FullPath, forceToken, new Progress<ProgressReport>(p =>
                                                {
                                                    p.SlotIndex = slotIndex;
                                                    progress.Report(p);
                                                }));

                                                meta.Sha256Hash = hash;

                                                progress.Report(new ProgressReport
                                                {
                                                    Type = ProgressReport.ReportType.SlotUpdate,
                                                    UpdateType = ProgressReport.SlotUpdateType.InProgress,
                                                    SlotIndex = slotIndex,
                                                    ProgressPercentage = 100,
                                                    StatusText = "Done"
                                                });

                                                await Task.Delay(250, gracefulToken);
                                            }
                                            catch (Exception fileEx) when (fileEx is not OperationCanceledException)
                                            {
                                                mismatchesSha.Add($"{meta.RelativePath} (Error: {fileEx.Message})");
                                                progress.Report(new ProgressReport
                                                {
                                                    Type = ProgressReport.ReportType.SlotUpdate,
                                                    UpdateType = ProgressReport.SlotUpdateType.InProgress,
                                                    SlotIndex = slotIndex,
                                                    ProgressPercentage = 100,
                                                    StatusText = "Error"
                                                });
                                                await Task.Delay(500, gracefulToken);
                                            }
                                            finally
                                            {
                                                int currentProgress = Interlocked.Increment(ref processedCount);
                                                progress.Report(new ProgressReport
                                                {
                                                    Type = ProgressReport.ReportType.OverallFileCompleted,
                                                    OverallProgress = currentProgress
                                                });
                                            }
                                        }

                                        progress.Report(new ProgressReport
                                        {
                                            Type = ProgressReport.ReportType.SlotUpdate,
                                            UpdateType = ProgressReport.SlotUpdateType.Finished,
                                            SlotIndex = slotIndex
                                        });
                                    }, gracefulToken));
                                }

                                await Task.WhenAll(consumerTasks);
                            }
                            else
                            {
                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    OverallProgressBar.Maximum = 1;
                                    OverallProgressBar.Value = 1;
                                    OverallProgressText.Text = "1/1";
                                });
                            }

                            if (action != Sha256Action.CompareOnly)
                            {
                                progress.Report(new ProgressReport { Type = ProgressReport.ReportType.StatusMessage, Message = "Saving/updating .sha256 files..." });
                                bool makeHidden = action == Sha256Action.CreateHiddenAndCompare;
                                SaveSha256Files(filesA, makeHidden);
                                SaveSha256Files(filesB, makeHidden);
                            }

                            progress.Report(new ProgressReport { Type = ProgressReport.ReportType.StatusMessage, Message = "Comparing SHA256 hashes..." });

                            // Phase 3: Compare results
                            foreach (var kvp in filesA)
                            {
                                gracefulToken.ThrowIfCancellationRequested();
                                string relativePath = kvp.Key;
                                var metaA = kvp.Value;

                                if (filesB.TryGetValue(relativePath, out var metaB))
                                {
                                    if (metaA.Size == metaB.Size)
                                    {
                                        if (string.Equals(metaA.Sha256Hash, metaB.Sha256Hash, StringComparison.OrdinalIgnoreCase))
                                        {
                                            matches++;
                                        }
                                        else
                                        {
                                            mismatchesSha.Add($"{relativePath} (SHA256 mismatch: {metaA.Sha256Hash} vs {metaB.Sha256Hash})");
                                        }
                                    }
                                }
                            }

                            // Logging results
                            progress.Report(new ProgressReport { Type = ProgressReport.ReportType.StatusMessage, Message = "SHA256 folder comparison complete." });
                            progress.Report(new ProgressReport { Type = ProgressReport.ReportType.StatusMessage, Message = $"Matches: {matches}" });
                            progress.Report(new ProgressReport { Type = ProgressReport.ReportType.StatusMessage, Message = $"Mismatches (Size/SHA256): {mismatchesSha.Count}" });
                            progress.Report(new ProgressReport { Type = ProgressReport.ReportType.StatusMessage, Message = $"Only in Folder A: {onlyInA.Count}" });
                            progress.Report(new ProgressReport { Type = ProgressReport.ReportType.StatusMessage, Message = $"Only in Folder B: {onlyInB.Count}" });

                            if (mismatchesSha.IsEmpty && onlyInA.Count == 0 && onlyInB.Count == 0)
                            {
                                progress.Report(new ProgressReport { Type = ProgressReport.ReportType.StatusMessage, Message = "Successful comparison. No errors." });
                            }

                            // Report detailed errors (up to 5 samples each)
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                ReportErrors("Mismatched (Size/SHA256)", mismatchesSha);
                                ReportErrors("Only in Folder A", onlyInA);
                                ReportErrors("Only in Folder B", onlyInB);
                            });

                            // Show MessageBox summary on UI thread
                            string summaryMessage = $"SHA256 Comparison complete for folders:\n" +
                                                   $"1: {firstFolder}\n" +
                                                   $"2: {secondFolder}\n\n" +
                                                   $"Matches: {matches}\n" +
                                                   $"Mismatches (Size/SHA256): {mismatchesSha.Count}\n" +
                                                   $"Only in Folder A: {onlyInA.Count}\n" +
                                                   $"Only in Folder B: {onlyInB.Count}";
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                MessageBox.Show(this, summaryMessage, "SHA256 Comparison Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                            });
                        }
                    }, gracefulToken);
                }
                catch (OperationCanceledException)
                {
                    if (_cancellationState == CancellationState.ForcedCancelRequested)
                    {
                        LogMessage("Folder comparison aborted by the user.");
                    }
                    else
                    {
                        LogMessage("Folder comparison cancelled by the user.");
                    }
                }
                catch (Exception ex)
                {
                    LogMessage($"Error during folder comparison: {ex.Message}");
                }
                finally
                {
                    stopwatch.Stop();
                    LogMessage($"Total elapsed time: {stopwatch.Elapsed:g}");
                    SetUiForOperationState(false);
                    _cancellationState = CancellationState.NotCancelled;
                    _cancellationTokenSource?.Dispose();
                    _forceCancellationTokenSource?.Dispose();
                }
            }
            else
            {
                LogMessage("Folder comparison cancelled from review dialog.");
            }
        }

        private static async Task<string> CalculateSha256(string filePath, CancellationToken token, IProgress<ProgressReport> progress)
        {
            const int bufferSize = 1024 * 64; // 64KB buffer
            var buffer = new byte[bufferSize];
            long totalBytesRead = 0;
            var stopwatch = new Stopwatch();
            var lastReportTime = DateTime.MinValue;

            using var sha256 = SHA256.Create();
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
            long fileLength = stream.Length;

            int bytesRead;
            stopwatch.Start();
            while ((bytesRead = await stream.ReadAsync(buffer, token)) > 0)
            {
                sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
                totalBytesRead += bytesRead;

                if (DateTime.UtcNow - lastReportTime > TimeSpan.FromMilliseconds(250))
                {
                    var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
                    if (elapsedSeconds > 0)
                    {
                        var speed = (long)(totalBytesRead / elapsedSeconds);
                        var remainingBytes = fileLength - totalBytesRead;
                        var estimatedSecondsRemaining = speed > 0 ? (double)remainingBytes / speed : 0;
                        var percentage = (int)((double)totalBytesRead / fileLength * 100);

                        string statusText = $"{FormatFileSize(speed)}/s";
                        if (estimatedSecondsRemaining > 3)
                        {
                            statusText += $" {FormatTimeSpan(TimeSpan.FromSeconds(estimatedSecondsRemaining))}";
                        }

                        progress.Report(new ProgressReport
                        {
                            Type = ProgressReport.ReportType.SlotUpdate,
                            UpdateType = ProgressReport.SlotUpdateType.InProgress,
                            ProgressPercentage = percentage,
                            StatusText = statusText
                        });
                        lastReportTime = DateTime.UtcNow;
                    }
                }
            }
            sha256.TransformFinalBlock(buffer, 0, 0);
            return Convert.ToHexStringLower(sha256.Hash!);
        }

        private void CancelOperationButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cancellationState == CancellationState.NotCancelled)
            {
                _cancellationState = CancellationState.GracefulCancelRequested;
                _cancellationTokenSource?.Cancel();
                CancelOperationButton.Content = "Cancel Again to Abort";
                CancelOperationButton.Background = Brushes.Yellow;
                LogMessage("Cancellation requested. Finishing active files...");
                LogMessage("Press cancel again to abort all operations immediately.");
            }
            else if (_cancellationState == CancellationState.GracefulCancelRequested)
            {
                _cancellationState = CancellationState.ForcedCancelRequested;
                _forceCancellationTokenSource?.Cancel();
                CancelOperationButton.Content = "Aborting...";
                CancelOperationButton.IsEnabled = false;
                LogMessage("Forced cancellation requested. Aborting all active files...");
            }
        }

        internal static string FormatTimeSpan(TimeSpan t)
        {
            string formattedTime;
            if (t.TotalDays >= 1)
                formattedTime = $"{t.Days}d {t.Hours}h {t.Minutes}m {t.Seconds}s";
            else if (t.TotalHours >= 1)
                formattedTime = $"{t.Hours}h {t.Minutes}m {t.Seconds}s";
            else if (t.TotalMinutes >= 1)
                formattedTime = $"{t.Minutes}m {t.Seconds}s";
            else
                formattedTime = $"{Math.Ceiling(t.TotalSeconds)}s";
            return $"(est. {formattedTime})";
        }

        internal static string FormatFileSize(long bytes)
        {
            var suf = new[] { "B", "KB", "MB", "GB", "TB", "PB", "EB" };
            if (bytes == 0) return "0" + suf[0];
            long absoluteBytes = Math.Abs(bytes);
            int place = Convert.ToInt32(Math.Floor(Math.Log(absoluteBytes, 1024)));
            double num = Math.Round(absoluteBytes / Math.Pow(1024, place), 1);
            return (Math.Sign(bytes) * num).ToString() + " " + suf[place];
        }

        private void ReportErrors(string category, IEnumerable<string> errors)
        {
            if (!errors.Any()) return;
            const int MAX_SAMPLES = 5;
            int errorCount = errors.Count();
            LogMessage($"--- {category} Errors ({errorCount} total) ---");
            foreach (var detail in errors.Take(MAX_SAMPLES))
            {
                LogMessage($"{category.ToUpper()} ERROR: {detail}");
            }
            if (errorCount > MAX_SAMPLES)
            {
                LogMessage($"... {errorCount - MAX_SAMPLES} more {category.ToLower()} errors.");
            }
        }

        private async IAsyncEnumerable<FileSystemItem> GetCheckedItems(ObservableCollection<FileSystemItem> items, [EnumeratorCancellation] CancellationToken token)
        {
            var stack = new Stack<FileSystemItem>(items.Reverse());
            while (stack.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var item = stack.Pop();
                if (item.IsChecked == true)
                {
                    yield return item;
                }
                if (item.IsDirectory && (item.IsChecked == true || item.IsChecked == null))
                {
                    if (item.HasDummyChild)
                    {
                        await Application.Current.Dispatcher.Invoke(async () =>
                        {
                            await LoadChildrenOnDemand(item, token);
                        });
                    }
                    foreach (var child in item.Children.Reverse())
                    {
                        stack.Push(child);
                    }
                }
            }
        }

        private async Task LoadChildrenOnDemand(FileSystemItem fsi, CancellationToken token)
        {
            if (!fsi.IsDirectory || !fsi.HasDummyChild) return;
            fsi.Children.Clear();
            fsi.HasDummyChild = false;

            try
            {
                string[] dirs = [];
                try
                {
                    dirs = await Task.Run(() => Directory.GetDirectories(fsi.FullPath), token);
                    Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                }
                catch (UnauthorizedAccessException)
                {
                    LogMessage($"Access denied to directory: {fsi.FullPath}");
                }
                catch (Exception ex)
                {
                    LogMessage($"Error reading subdirectories for '{fsi.FullPath}': {ex.Message}");
                }

                foreach (var dir in dirs)
                {
                    token.ThrowIfCancellationRequested();
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint) continue;
                    var subDirItem = new FileSystemItem { Name = Path.GetFileName(dir), FullPath = dir, IsDirectory = true, HasDummyChild = true, IsChecked = fsi.IsChecked, Parent = fsi };
                    subDirItem.Children.Add(new FileSystemItem());
                    fsi.Children.Add(subDirItem);
                }

                string[] files = [];
                try
                {
                    files = await Task.Run(() => Directory.GetFiles(fsi.FullPath), token);
                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                }
                catch (UnauthorizedAccessException)
                {
                    LogMessage($"Access denied to files in directory: {fsi.FullPath}");
                }
                catch (Exception ex)
                {
                    LogMessage($"Error reading files for '{fsi.FullPath}': {ex.Message}");
                }

                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    var fileItem = new FileSystemItem { Name = Path.GetFileName(file), FullPath = file, IsDirectory = false, IsChecked = fsi.IsChecked, Parent = fsi };
                    fsi.Children.Add(fileItem);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LogMessage($"An unexpected error occurred while loading children for '{fsi.FullPath}': {ex.Message}");
            }
            finally
            {
                fsi.UpdateParentCheckState();
            }
        }


        private async void TreeViewItem_Expanded(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is TreeViewItem item && item.Header is FileSystemItem fsi)
            {
                e.Handled = true;
                try
                {
                    await LoadChildrenOnDemand(fsi, CancellationToken.None);
                }
                finally
                {
                }
            }
        }

        private async Task RefreshTreeView(string drivePath)
        {
            try
            {
                bool skipLargeFiles = Application.Current.Dispatcher.Invoke(() => SkipLargeFilesCheckBox.IsChecked ?? false);
                List<FileSystemNodeData> rawDataRoots = await Task.Run(() => BuildDriveData(drivePath, skipLargeFiles));

                ObservableCollection<FileSystemItem> uiTreeRoots = [];
                Dictionary<string, FileSystemItem> uiNodes = new(StringComparer.OrdinalIgnoreCase);
                foreach (var dataNode in rawDataRoots)
                {
                    CreateUITreeNode(dataNode, null, uiTreeRoots, uiNodes);
                }
                SelectedItemsTreeView.ItemsSource = uiTreeRoots;
                foreach (var uiNode in uiNodes.Values.OrderByDescending(n => n.FullPath.Length))
                {
                    if (_explicitlySelectedPaths.Contains(uiNode.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
                    {
                        if (uiNode.IsChecked != true)
                        {
                            uiNode.IsChecked = true;
                        }
                    }
                }

                foreach (var rootItem in uiTreeRoots)
                {
                    UpdateChildrenAndParentCheckStates(rootItem);
                }

                if (!_isInitialLoadComplete)
                {
                    LogMessage("Initial paths have been pre-selected. Ready.");
                    _isInitialLoadComplete = true;
                }
            }
            catch (Exception ex)
            {
                LogMessage($"An error occurred during TreeView loading: {ex.Message}");
            }
        }

        private static FileSystemItem CreateUITreeNode(FileSystemNodeData dataNode, FileSystemItem? parentItem, ObservableCollection<FileSystemItem> collectionToAddInto, Dictionary<string, FileSystemItem> uiNodes)
        {
            if (uiNodes.TryGetValue(dataNode.FullPath, out var existingUiItem))
            {
                return existingUiItem;
            }
            FileSystemItem uiItem = new()
            {
                Name = dataNode.Name,
                FullPath = dataNode.FullPath,
                IsDirectory = dataNode.IsDirectory,
                IsExpanded = dataNode.ShouldBeExpanded,
                Parent = parentItem
            };
            uiNodes[dataNode.FullPath] = uiItem;
            if (dataNode.IsDirectory)
            {
                if (dataNode.ChildrenData.Count != 0)
                {
                    foreach (var childData in dataNode.ChildrenData)
                    {
                        CreateUITreeNode(childData, uiItem, uiItem.Children, uiNodes);
                    }
                    uiItem.HasDummyChild = false;
                }
                else if (dataNode.ChildrenData.Count == 0 && !dataNode.ShouldBeExpanded)
                {
                    uiItem.Children.Add(new FileSystemItem());
                    uiItem.HasDummyChild = true;
                }
            }
            collectionToAddInto.Add(uiItem);
            return uiItem;
        }

        private List<FileSystemNodeData> BuildDriveData(string drivePath, bool skipLargeFiles)
        {
            var rootDataNodes = new List<FileSystemNodeData>();
            var allDataNodes = new Dictionary<string, FileSystemNodeData>(StringComparer.OrdinalIgnoreCase);

            if (Directory.Exists(drivePath))
            {
                string normalizedDrivePath = drivePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var driveNode = new FileSystemNodeData
                {
                    Name = drivePath,
                    FullPath = drivePath,
                    IsDirectory = true,
                    IsExplicitlySelected = _explicitlySelectedPaths.Contains(normalizedDrivePath),
                    ShouldBeExpanded = _explicitlySelectedPaths.Any(p => p.StartsWith(normalizedDrivePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) || _explicitlySelectedPaths.Contains(normalizedDrivePath)
                };
                allDataNodes[drivePath] = driveNode;
                rootDataNodes.Add(driveNode);
                PopulateDirectoryData(drivePath, driveNode, allDataNodes, skipLargeFiles);
            }
            else
            {
                LogMessage($"Background: Drive '{drivePath}' does not exist or is not ready.");
            }
            return rootDataNodes;
        }

        private void PopulateDirectoryData(string currentPath, FileSystemNodeData parentDataNode, Dictionary<string, FileSystemNodeData> allDataNodes, bool skipLargeFiles)
        {
            try
            {
                string[] dirs = Directory.GetDirectories(currentPath);
                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);

                foreach (var dirPath in dirs)
                {
                    string dirName = Path.GetFileName(dirPath);
                    if (string.IsNullOrEmpty(dirName) && Directory.Exists(dirPath)) dirName = new DirectoryInfo(dirPath).Name;
                    if (string.IsNullOrEmpty(dirName) && Path.GetPathRoot(dirPath) == dirPath) dirName = dirPath;
                    if (ExcludedFolderNames.Contains(dirName))
                    {
                        continue;
                    }
                    if ((File.GetAttributes(dirPath) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint) continue;
                    string normalizedDirPath = dirPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var dirNode = new FileSystemNodeData
                    {
                        Name = dirName,
                        FullPath = dirPath,
                        IsDirectory = true,
                        IsExplicitlySelected = _explicitlySelectedPaths.Contains(normalizedDirPath),
                        ShouldBeExpanded = _explicitlySelectedPaths.Any(p => p.StartsWith(normalizedDirPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) || _explicitlySelectedPaths.Contains(normalizedDirPath)
                    };
                    allDataNodes[dirPath] = dirNode;
                    parentDataNode.ChildrenData.Add(dirNode);
                    if (dirNode.ShouldBeExpanded)
                    {
                        PopulateDirectoryData(dirPath, dirNode, allDataNodes, skipLargeFiles);
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                Application.Current.Dispatcher.Invoke(() => LogMessage($"Access denied to directory: {currentPath}"));
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() => LogMessage($"Error reading directories in '{currentPath}': {ex.Message}"));
            }

            try
            {
                string[] files = Directory.GetFiles(currentPath);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);

                foreach (var filePath in files)
                {
                    string fileName = Path.GetFileName(filePath);
                    if (ExcludedFileNames.Contains(fileName))
                    {
                        continue;
                    }
                    if (skipLargeFiles && LargeFileExtensions.Contains(Path.GetExtension(fileName)))
                    {
                        continue;
                    }

                    string normalizedFilePath = filePath;
                    var fileNode = new FileSystemNodeData
                    {
                        Name = fileName,
                        FullPath = filePath,
                        IsDirectory = false,
                        IsExplicitlySelected = _explicitlySelectedPaths.Contains(normalizedFilePath),
                        ShouldBeExpanded = false
                    };
                    allDataNodes[filePath] = fileNode;
                    parentDataNode.ChildrenData.Add(fileNode);
                }
            }
            catch (UnauthorizedAccessException)
            {
                Application.Current.Dispatcher.Invoke(() => LogMessage($"Access denied to files in directory: {currentPath}"));
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() => LogMessage($"Error reading files in '{currentPath}': {ex.Message}"));
            }
        }


        private static void UpdateChildrenAndParentCheckStates(FileSystemItem item)
        {
            if (item.IsDirectory && item.Children.Any() && !item.HasDummyChild)
            {
                foreach (var child in item.Children)
                {
                    UpdateChildrenAndParentCheckStates(child);
                }
            }
            item.UpdateParentCheckState();
        }

        private void AutoSelectDescendantsCheckBox_Checked(object sender, RoutedEventArgs e) { AutoSelectDescendants = true; LogMessage("Automatic selection/deselection of descendants is ON."); }
        private void AutoSelectDescendantsCheckBox_Unchecked(object sender, RoutedEventArgs e) { AutoSelectDescendants = false; LogMessage("Automatic selection/deselection of descendants is OFF."); }

        private void CopyAllToClipboard_Click(object sender, RoutedEventArgs e)
        {
            StringBuilder logContent = new();
            foreach (var item in StatusLogListBox.Items)
            {
                logContent.AppendLine(GetLogItemText(item));
            }
            try
            {
                Clipboard.SetText(logContent.ToString());
                LogMessage("All status log messages copied to clipboard.");
            }
            catch (Exception ex)
            {
                LogMessage($"Failed to copy all log messages to clipboard: {ex.Message}");
            }
        }

        private void CopySelectedRowsToClipboard_Click(object sender, RoutedEventArgs e)
        {
            if (StatusLogListBox.SelectedItems.Count == 0)
            {
                LogMessage("No status log rows selected to copy.");
                return;
            }
            StringBuilder logContent = new();
            foreach (var item in StatusLogListBox.SelectedItems)
            {
                logContent.AppendLine(GetLogItemText(item));
            }
            try
            {
                Clipboard.SetText(logContent.ToString());
                LogMessage($"Copied {StatusLogListBox.SelectedItems.Count} selected status log row(s) to clipboard.");
            }
            catch (Exception ex)
            {
                LogMessage($"Failed to copy selected log messages to clipboard: {ex.Message}");
            }
        }

        private void ClearStatusLog_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                StatusLogListBox.Items.Clear();
                LogMessage("Status log cleared.");
            });
        }

        private static string GetLogItemText(object? item)
        {
            if (item is ListBoxItem lbi)
            {
                if (lbi.Content is string s) return s;
                return lbi.Content?.ToString() ?? string.Empty;
            }
            return item?.ToString() ?? string.Empty;
        }

        private void LogMessage(string message, Brush? foreground = null)
        {
            Dispatcher.Invoke(() =>
            {
                bool isBold = false;
                if (foreground == null)
                {
                    if (message.Contains("Successful comparison. No errors."))
                    {
                        foreground = Brushes.DarkCyan;
                        isBold = true;
                    }
                    else if (message.Contains("ERROR:") || message.Contains("Error:") ||
                             ((message.Contains("mismatch") || message.Contains("Mismatch")) && !message.Trim().EndsWith(": 0")))
                    {
                        foreground = Brushes.Red;
                    }
                }

                var item = new ListBoxItem
                {
                    Content = $"{DateTime.Now:HH:mm:ss}: {message}"
                };
                if (foreground != null)
                {
                    item.Foreground = foreground;
                }
                if (isBold)
                {
                    item.FontWeight = FontWeights.Bold;
                }
                StatusLogListBox.Items.Add(item);
                StatusLogListBox.ScrollIntoView(item);
            });
        }

        private Dictionary<string, Sha256Entry> ReadSha256File(string sha256FilePath)
        {
            var entries = new Dictionary<string, Sha256Entry>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(sha256FilePath)) return entries;
            try
            {
                foreach (string line in File.ReadLines(sha256FilePath))
                {
                    string trimmedLine = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmedLine)) continue;
                    int firstSpaceIndex = trimmedLine.IndexOf(' ');
                    if (firstSpaceIndex > 0 && trimmedLine.Length > firstSpaceIndex + 1)
                    {
                        string hash = trimmedLine[..firstSpaceIndex].Trim();
                        string fileName = trimmedLine[(firstSpaceIndex + 1)..].Trim();
                        if (!string.IsNullOrEmpty(hash) && !string.IsNullOrEmpty(fileName))
                        {
                            entries[fileName] = new Sha256Entry(hash, fileName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Error reading .sha256 file '{sha256FilePath}': {ex.Message}");
            }
            return entries;
        }

        private void WriteSha256File(string sha256FilePath, List<Sha256Entry> entries)
        {
            try
            {
                var sortedEntries = entries.OrderBy(e => e.FileName, StringComparer.OrdinalIgnoreCase).ToList();
                StringBuilder sb = new();
                foreach (var entry in sortedEntries)
                {
                    sb.AppendLine(entry.ToString());
                }
                if (sb.Length > 0 && (sb.Length >= Environment.NewLine.Length))
                {
                    sb.Length -= Environment.NewLine.Length;
                }
                File.WriteAllText(sha256FilePath, sb.ToString());
            }
            catch (Exception ex)
            {
                LogMessage($"Error writing .sha256 file '{sha256FilePath}': {ex.Message}");
            }
        }

        private void SaveSha256Files(Dictionary<string, FileComparisonMetadata> files, bool makeHidden)
        {
            var filesByDir = files.Values.GroupBy(f => Path.GetDirectoryName(f.FullPath) ?? string.Empty);
            foreach (var group in filesByDir)
            {
                if (string.IsNullOrEmpty(group.Key)) continue;
                string dirPath = group.Key;
                string sha256FileName = Path.GetPathRoot(dirPath) == dirPath ? ".sha256" : Path.GetFileName(dirPath) + ".sha256";
                string sha256FilePath = Path.Combine(dirPath, sha256FileName);

                var shaEntries = ReadSha256File(sha256FilePath);
                foreach (var file in group)
                {
                    if (!string.IsNullOrEmpty(file.Sha256Hash))
                    {
                        string fileName = Path.GetFileName(file.FullPath);
                        shaEntries[fileName] = new Sha256Entry(file.Sha256Hash, fileName);
                    }
                }

                try
                {
                    if (File.Exists(sha256FilePath))
                    {
                        File.SetAttributes(sha256FilePath, File.GetAttributes(sha256FilePath) & ~FileAttributes.ReadOnly & ~FileAttributes.Hidden);
                    }
                    WriteSha256File(sha256FilePath, [.. shaEntries.Values]);
                    if (makeHidden)
                    {
                        File.SetAttributes(sha256FilePath, File.GetAttributes(sha256FilePath) | FileAttributes.Hidden);
                    }
                }
                catch (Exception ex)
                {
                    LogMessage($"Error writing SHA256 file '{sha256FilePath}': {ex.Message}");
                }
            }
        }

        private class FileComparisonMetadata
        {
            public string RelativePath { get; set; } = string.Empty;
            public string FullPath { get; set; } = string.Empty;
            public long Size { get; set; }
            public DateTime LastWriteTimeUtc { get; set; }
            public string? Sha256Hash { get; set; }
        }

        private async Task<Dictionary<string, FileComparisonMetadata>> ScanFolderAsync(string folderPath, bool skipLargeFiles, CancellationToken token)
        {
            var metadataMap = new Dictionary<string, FileComparisonMetadata>(StringComparer.OrdinalIgnoreCase);
            var dirsToProcess = new Stack<string>();
            dirsToProcess.Push(folderPath);

            while (dirsToProcess.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string currentDir = dirsToProcess.Pop();

                // 1. Process files in currentDir
                try
                {
                    var files = Directory.GetFiles(currentDir);
                    foreach (var file in files)
                    {
                        token.ThrowIfCancellationRequested();
                        string fileName = Path.GetFileName(file);

                        // Exclude system files/names
                        if (ExcludedFileNames.Contains(fileName)) continue;
                        if (skipLargeFiles && LargeFileExtensions.Contains(Path.GetExtension(fileName))) continue;

                        var fileInfo = new FileInfo(file);
                        string relativePath = Path.GetRelativePath(folderPath, file);

                        metadataMap[relativePath] = new FileComparisonMetadata
                        {
                            RelativePath = relativePath,
                            FullPath = file,
                            Size = fileInfo.Length,
                            LastWriteTimeUtc = fileInfo.LastWriteTimeUtc
                        };
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    LogMessage($"Access denied to files in directory: {currentDir}");
                }
                catch (Exception ex)
                {
                    LogMessage($"Error scanning files in '{currentDir}': {ex.Message}");
                }

                // 2. Process subdirectories in currentDir
                try
                {
                    var subDirs = Directory.GetDirectories(currentDir);
                    foreach (var subDir in subDirs)
                    {
                        token.ThrowIfCancellationRequested();
                        string dirName = Path.GetFileName(subDir);
                        if (ExcludedFolderNames.Contains(dirName)) continue;
                        dirsToProcess.Push(subDir);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    LogMessage($"Access denied to directories in: {currentDir}");
                }
                catch (Exception ex)
                {
                    LogMessage($"Error listing subdirectories in '{currentDir}': {ex.Message}");
                }

                // Yield to keep UI responsive
                await Task.Yield();
            }

            return metadataMap;
        }
    }
}