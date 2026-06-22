using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using PokeSwitch.Models;
using PokeSwitch.Services;
using Wpf.Ui.Controls;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;
using WpfMessageBox = System.Windows.MessageBox;

namespace PokeSwitch
{
    public partial class MainWindow : FluentWindow, INotifyPropertyChanged
    {
        private const int MaxRecentFailures = 10;

        private readonly ConfigManager _configManager;
        private readonly DockerManager _dockerManager;
        private GpuManager _gpuManager;
        private readonly DispatcherTimer _statusTimer;
        private readonly SemaphoreSlim _dashboardRefreshGate = new(1, 1);
        private readonly CancellationTokenSource _shutdownCts = new();
        private readonly Queue<string> _logLines = new();
        private readonly Queue<string> _recentFailures = new();
        private readonly Forms.NotifyIcon _notifyIcon;

        public AppConfig Config => _configManager.CurrentConfig;

        public ObservableCollection<HardwareDeviceDescriptor> DisplayDevices { get; } = new();

        public ToggleCardState WslToggle { get; } = new("Play24", "Start WSL Keep-Alive", "Headless Server Mode (sleep infinity)", "#0078D4");
        public ToggleCardState DockerToggle { get; } = new("ArrowSync24", "Boot Docker Engine", "Full spin-up of Docker & all containers", "#107C10");
        public ToggleCardState GpuToggle { get; } = new("DeveloperBoard24", "Checking GPU...", "Configured display device status", "Gray");
        public ToggleCardState NuclearToggle { get; } = new("Power24", "Nuclear Shutdown", "Purge all Docker + WSL processes", "#D13438");

        private string _dockerStatusText = "Checking...";
        public string DockerStatusText { get => _dockerStatusText; set { _dockerStatusText = value; OnPropertyChanged(); } }

        private string _dockerStatusColor = "Gray";
        public string DockerStatusColor { get => _dockerStatusColor; set { _dockerStatusColor = value; OnPropertyChanged(); } }

        private string _wslStatusText = "Checking...";
        public string WslStatusText { get => _wslStatusText; set { _wslStatusText = value; OnPropertyChanged(); } }

        private string _wslStatusColor = "Gray";
        public string WslStatusColor { get => _wslStatusColor; set { _wslStatusColor = value; OnPropertyChanged(); } }

        private string _containerText = "0 / 0";
        public string ContainerText { get => _containerText; set { _containerText = value; OnPropertyChanged(); } }

        private string _ramUsageText = "0 MB";
        public string RamUsageText { get => _ramUsageText; set { _ramUsageText = value; OnPropertyChanged(); } }

        private string _diagnosticsText = "Collecting diagnostics...";
        public string DiagnosticsText { get => _diagnosticsText; set { _diagnosticsText = value; OnPropertyChanged(); } }

        private bool _isDockerRunning;
        private bool _isWslRunning;
        private bool _isWslKeepAliveRunning;
        private GpuStatus _gpuStatus = new(false, false, null, null, null, false, "GPU status has not been checked yet.");

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            _configManager = new ConfigManager();
            _configManager.Load();

            _dockerManager = new DockerManager(Config);
            _gpuManager = CreateGpuManager();
            _notifyIcon = CreateNotifyIcon();

            _statusTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(GetPollIntervalSeconds())
            };
            _statusTimer.Tick += StatusTimer_Tick;
            _statusTimer.Start();

            ConfigureTray();

            AppendLog("Application started.");
            RunBackground(RefreshGpuDevicesAsync, "GPU device refresh");
            RunBackground(() => UpdateDashboardAsync(_shutdownCts.Token), "Initial dashboard refresh");

            if (Config.AutoStartWslOnLaunch)
            {
                AppendLog("Auto-starting WSL (config).");
                RunBackground(() => _dockerManager.StartWslKeepAliveAsync(AppendLog, _shutdownCts.Token), "Auto-start WSL");
            }

            if (Config.AutoStartDockerOnLaunch)
            {
                AppendLog("Auto-starting Docker (config).");
                RunBackground(() => _dockerManager.BootDockerEngineAsync(AppendLog, _shutdownCts.Token), "Auto-start Docker");
            }
        }

        private GpuManager CreateGpuManager()
        {
            return new GpuManager(Config.Hardware, new ProcessRunner());
        }

        private Forms.NotifyIcon CreateNotifyIcon()
        {
            var icon = new Forms.NotifyIcon
            {
                Text = "PokeSwitch",
                Icon = ResolveTrayIcon(),
                Visible = false,
                ContextMenuStrip = new Forms.ContextMenuStrip()
            };

            icon.DoubleClick += (_, _) => RestoreFromTray();
            icon.ContextMenuStrip.Items.Add("Open PokeSwitch", null, (_, _) => RestoreFromTray());
            icon.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
            icon.ContextMenuStrip.Items.Add("Toggle WSL Keep-Alive", null, (_, _) => Dispatcher.Invoke(() => _ = ToggleWslAsync()));
            icon.ContextMenuStrip.Items.Add("Toggle Docker Engine", null, (_, _) => Dispatcher.Invoke(() => _ = ToggleDockerAsync()));
            icon.ContextMenuStrip.Items.Add("Toggle NVIDIA GPU", null, (_, _) => Dispatcher.Invoke(() => _ = ToggleGpuAsync()));
            icon.ContextMenuStrip.Items.Add("Nuclear Shutdown", null, (_, _) => Dispatcher.Invoke(() => _ = NuclearShutdownAsync()));
            icon.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
            icon.ContextMenuStrip.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(CloseForExit));

            return icon;
        }

        private static Drawing.Icon ResolveTrayIcon()
        {
            try
            {
                string? processPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                return processPath == null
                    ? Drawing.SystemIcons.Application
                    : Drawing.Icon.ExtractAssociatedIcon(processPath) ?? Drawing.SystemIcons.Application;
            }
            catch
            {
                return Drawing.SystemIcons.Application;
            }
        }

        private void ConfigureTray()
        {
            _notifyIcon.Visible = Config.Tray?.Enabled == true;
        }

        private async void StatusTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                await UpdateDashboardAsync(_shutdownCts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppendLog($"Dashboard refresh failed: {ex.Message}");
                RecordFailure($"Dashboard refresh: {ex.Message}");
            }
        }

        private async Task UpdateDashboardAsync(CancellationToken cancellationToken = default)
        {
            if (!await _dashboardRefreshGate.WaitAsync(0, cancellationToken))
            {
                return;
            }

            try
            {
                Task<bool> dockerTask = Task.Run(() => _dockerManager.IsDockerDesktopRunning(), cancellationToken);
                Task<bool> wslTask = Task.Run(() => _dockerManager.IsWslRunning(), cancellationToken);
                Task<bool> wslKeepAliveTask = Task.Run(() => _dockerManager.IsWslKeepAliveRunning(), cancellationToken);
                Task<(int running, int total)> containersTask = _dockerManager.GetRunningContainerCountAsync(cancellationToken);
                Task<int> ramTask = Task.Run(() => _dockerManager.GetVmMemUsageMB(), cancellationToken);
                Task<GpuStatus> gpuTask = _gpuManager.GetStatusAsync(cancellationToken);

                await Task.WhenAll(dockerTask, wslTask, wslKeepAliveTask, containersTask, ramTask, gpuTask);

                await Dispatcher.InvokeAsync(() =>
                {
                    ApplyDashboardState(
                        dockerTask.Result,
                        wslTask.Result,
                        wslKeepAliveTask.Result,
                        containersTask.Result,
                        ramTask.Result,
                        gpuTask.Result);
                });
            }
            finally
            {
                _dashboardRefreshGate.Release();
            }
        }

        private void ApplyDashboardState(
            bool isDockerRunning,
            bool isWslRunning,
            bool isWslKeepAliveRunning,
            (int running, int total) containers,
            int ram,
            GpuStatus gpuStatus)
        {
            _isDockerRunning = isDockerRunning;
            _isWslRunning = isWslRunning;
            _isWslKeepAliveRunning = isWslKeepAliveRunning;
            _gpuStatus = gpuStatus;

            DockerStatusText = isDockerRunning ? "Running" : "Stopped";
            DockerStatusColor = isDockerRunning ? "#107C10" : "#D13438";
            WslStatusText = isWslRunning ? "Active" : "Inactive";
            WslStatusColor = isWslRunning ? "#0078D4" : "Gray";
            ContainerText = isDockerRunning ? $"{containers.running} / {containers.total}" : "-";
            RamUsageText = ram > 0 ? $"{ram} MB" : "- (idle)";

            ApplyWslCardState(isWslKeepAliveRunning);
            ApplyDockerCardState(isDockerRunning, containers.running);
            ApplyGpuCardState(gpuStatus);
            if (!NuclearToggle.IsRunning)
            {
                NuclearToggle.MarkReady(NuclearToggle.LastResult == "Running" ? "Ready" : NuclearToggle.LastResult);
            }
            DiagnosticsText = BuildDiagnosticsText(CreateDiagnosticsSnapshot());
        }

        private void ApplyWslCardState(bool isWslKeepAliveRunning)
        {
            if (WslToggle.IsRunning)
            {
                return;
            }

            if (isWslKeepAliveRunning)
            {
                WslToggle.Label = "Stop WSL Keep-Alive";
                WslToggle.Icon = "Stop24";
                WslToggle.Accent = "#D83B01";
                WslToggle.Subtitle = "Running in headless mode";
            }
            else
            {
                WslToggle.Label = "Start WSL Keep-Alive";
                WslToggle.Icon = "Play24";
                WslToggle.Accent = "#0078D4";
                WslToggle.Subtitle = "Headless Server Mode (sleep infinity)";
            }

            WslToggle.MarkReady(WslToggle.LastResult == "Running" ? "Ready" : WslToggle.LastResult);
        }

        private void ApplyDockerCardState(bool isDockerRunning, int runningContainers)
        {
            if (DockerToggle.IsRunning)
            {
                return;
            }

            if (isDockerRunning)
            {
                DockerToggle.Label = "Stop Docker Engine";
                DockerToggle.Icon = "Stop24";
                DockerToggle.Accent = "#D83B01";
                DockerToggle.Subtitle = $"Running ({runningContainers} containers active)";
            }
            else
            {
                DockerToggle.Label = "Boot Docker Engine";
                DockerToggle.Icon = "ArrowSync24";
                DockerToggle.Accent = "#107C10";
                DockerToggle.Subtitle = "Full spin-up of Docker & all containers";
            }

            DockerToggle.MarkReady(DockerToggle.LastResult == "Running" ? "Ready" : DockerToggle.LastResult);
        }

        private void ApplyGpuCardState(GpuStatus gpuStatus)
        {
            if (GpuToggle.IsRunning)
            {
                return;
            }

            GpuToggle.Icon = "DeveloperBoard24";

            if (gpuStatus.Multiple)
            {
                GpuToggle.Label = "GPU Toggle Unavailable";
                GpuToggle.Accent = "#D13438";
                GpuToggle.Subtitle = gpuStatus.Message;
                GpuToggle.MarkFailed("Select a GPU in Settings");
                return;
            }

            if (!gpuStatus.Found)
            {
                GpuToggle.Label = "GPU Not Found";
                GpuToggle.Accent = "Gray";
                GpuToggle.Subtitle = gpuStatus.Message;
                GpuToggle.MarkReady("Not found");
                return;
            }

            if (gpuStatus.IsEnabled)
            {
                GpuToggle.Label = "Disable NVIDIA GPU";
                GpuToggle.Accent = "#D83B01";
                GpuToggle.Subtitle = $"{gpuStatus.FriendlyName} is enabled";
            }
            else
            {
                GpuToggle.Label = "Enable NVIDIA GPU";
                GpuToggle.Accent = "#107C10";
                GpuToggle.Subtitle = $"{gpuStatus.FriendlyName} status: {gpuStatus.Status ?? "Unknown"}";
            }

            GpuToggle.MarkReady(GpuToggle.LastResult is "Not found" or "Failed" or "Running" ? "Ready" : GpuToggle.LastResult);
        }

        private void AppendLog(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string formatted = $"[{timestamp}] {message}";

            if (Config.Logging?.FileEnabled == true)
            {
                WriteLogFile(formatted);
            }

            if (Config.Logging?.Enabled != true || Dispatcher.HasShutdownStarted)
            {
                return;
            }

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AppendLog(message));
                return;
            }

            _logLines.Enqueue(formatted);
            int maxLines = Math.Clamp(Config.Logging.MaxLines, 50, 5000);
            while (_logLines.Count > maxLines)
            {
                _logLines.Dequeue();
            }

            TxtTerminal.Text = string.Join(Environment.NewLine, _logLines);
            TerminalScrollViewer.ScrollToEnd();
        }

        private static void WriteLogFile(string line)
        {
            try
            {
                string directory = ConfigManager.GetLogDirectory();
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, $"pokeswitch-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, line + Environment.NewLine);
                RotateLogFiles(directory);
            }
            catch
            {
                // Logging must never break a toggle action.
            }
        }

        private static void RotateLogFiles(string directory)
        {
            foreach (FileInfo file in new DirectoryInfo(directory)
                .GetFiles("pokeswitch-*.log")
                .OrderByDescending(f => f.CreationTimeUtc)
                .Skip(7))
            {
                try
                {
                    file.Delete();
                }
                catch
                {
                }
            }
        }

        private void ShowInfo(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
        {
            if (Dispatcher.HasShutdownStarted)
            {
                return;
            }

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => ShowInfo(title, message, severity));
                return;
            }

            StatusInfoBar.Title = title;
            StatusInfoBar.Message = message;
            StatusInfoBar.Severity = severity;
            StatusInfoBar.IsOpen = true;
        }

        private async void WslToggle_Click(object sender, RoutedEventArgs e)
        {
            await ToggleWslAsync();
        }

        private async Task ToggleWslAsync()
        {
            await RunToggleOperationAsync(WslToggle, "WSL Keep-Alive", async cancellationToken =>
            {
                if (_isWslKeepAliveRunning)
                {
                    ShowInfo("Executing", "Stopping WSL Keep-Alive...", InfoBarSeverity.Warning);
                    await _dockerManager.StopWslAsync(AppendLog, cancellationToken);

                    bool success = await WaitUntilAsync(
                        () => !_dockerManager.IsWslKeepAliveRunning(),
                        attempts: 50,
                        delay: TimeSpan.FromMilliseconds(200),
                        cancellationToken);

                    return success
                        ? new ToggleActionResult(true, "Success", "WSL Keep-Alive is stopped.")
                        : new ToggleActionResult(false, "Warning", "Stop command sent, but WSL is taking too long to update.", "Failed");
                }

                ShowInfo("Executing", "Starting WSL in Headless Mode...", InfoBarSeverity.Informational);
                await _dockerManager.StartWslKeepAliveAsync(AppendLog, cancellationToken);

                bool started = await WaitUntilAsync(
                    () => _dockerManager.IsWslKeepAliveRunning(),
                    attempts: 50,
                    delay: TimeSpan.FromMilliseconds(200),
                    cancellationToken);

                return started
                    ? new ToggleActionResult(true, "Success", "WSL Keep-Alive is now running in the background.")
                    : new ToggleActionResult(false, "Warning", "Start command sent, but WSL is taking too long to respond.", "Failed");
            });
        }

        private async void DockerToggle_Click(object sender, RoutedEventArgs e)
        {
            await ToggleDockerAsync();
        }

        private async Task ToggleDockerAsync()
        {
            await RunToggleOperationAsync(DockerToggle, "Docker Engine", async cancellationToken =>
            {
                if (_isDockerRunning)
                {
                    if (Config.Toggles?.ConfirmDockerStop == true && !Confirm("Stop Docker Engine", "Stop Docker Desktop and all running containers?"))
                    {
                        return new ToggleActionResult(false, "Canceled", "Docker stop canceled.", "Ready");
                    }

                    ShowInfo("Executing", "Stopping Docker and containers...", InfoBarSeverity.Warning);
                    await _dockerManager.StopDockerOnlyAsync(AppendLog, cancellationToken);

                    bool success = await WaitUntilAsync(
                        () => !_dockerManager.IsDockerDesktopRunning(),
                        attempts: 40,
                        delay: TimeSpan.FromMilliseconds(250),
                        cancellationToken);

                    return success
                        ? new ToggleActionResult(true, "Success", "Docker is stopped.")
                        : new ToggleActionResult(false, "Warning", "Docker stop command sent, but processes are taking too long to exit.", "Failed");
                }

                await _dockerManager.BootDockerEngineAsync(AppendLog, cancellationToken);
                return new ToggleActionResult(true, "Success", "Server Mode Active. All containers are online!");
            });
        }

        private async void GpuToggle_Click(object sender, RoutedEventArgs e)
        {
            await ToggleGpuAsync();
        }

        private async Task ToggleGpuAsync()
        {
            await RunToggleOperationAsync(GpuToggle, "NVIDIA GPU", async cancellationToken =>
            {
                if (_gpuStatus is { Found: true, IsEnabled: true }
                    && Config.Toggles?.ConfirmGpuDisable == true
                    && !Confirm("Disable NVIDIA GPU", "Disable the selected NVIDIA GPU device? Close GPU-using apps first."))
                {
                    return new ToggleActionResult(false, "Canceled", "GPU disable canceled.", "Ready");
                }

                ShowInfo("Executing", "Toggling NVIDIA GPU...", InfoBarSeverity.Warning);
                AppendLog($"GPU before toggle: {_gpuStatus.Message}");

                GpuToggleResult result = await _gpuManager.ToggleAsync(cancellationToken);
                AppendLog($"GPU {result.Action}: {result.Message}");

                return new ToggleActionResult(
                    result.Success,
                    result.Success ? "Success" : "Warning",
                    result.Message,
                    result.Success ? "Ready" : "Failed");
            });
        }

        private async void NuclearShutdown_Click(object sender, RoutedEventArgs e)
        {
            await NuclearShutdownAsync();
        }

        private async Task NuclearShutdownAsync()
        {
            await RunToggleOperationAsync(NuclearToggle, "Nuclear Shutdown", async cancellationToken =>
            {
                if (Config.Toggles?.ConfirmNuclearShutdown == true
                    && !Confirm("Nuclear Shutdown", "Stop Docker, containers, and shut down all WSL instances?"))
                {
                    return new ToggleActionResult(false, "Canceled", "Nuclear shutdown canceled.", "Ready");
                }

                await _dockerManager.NuclearShutdownAsync(AppendLog, cancellationToken);

                bool success = await WaitUntilAsync(
                    () => !_dockerManager.IsDockerDesktopRunning()
                        && !_dockerManager.IsWslRunning()
                        && !_dockerManager.IsWslKeepAliveRunning(),
                    attempts: 60,
                    delay: TimeSpan.FromMilliseconds(250),
                    cancellationToken);

                return success
                    ? new ToggleActionResult(true, "Success", "Desktop Mode Active. RAM and Battery saved!")
                    : new ToggleActionResult(false, "Warning", "Nuclear shutdown sent, but some processes are taking a long time to exit.", "Failed");
            });
        }

        private async Task RunToggleOperationAsync(
            ToggleCardState card,
            string operationName,
            Func<CancellationToken, Task<ToggleActionResult>> operation)
        {
            if (card.IsRunning)
            {
                ShowInfo("Busy", $"{operationName} is already running.", InfoBarSeverity.Warning);
                return;
            }

            card.MarkRunning("Running");
            try
            {
                ToggleActionResult result = await operation(_shutdownCts.Token);
                if (result.Success)
                {
                    card.MarkReady(result.Message);
                    ShowInfo(result.Title, result.Message, InfoBarSeverity.Success);
                    Notify($"{operationName}: {result.Title}", result.Message);
                }
                else if (result.Title == "Canceled")
                {
                    card.MarkReady(result.Message);
                    ShowInfo(result.Title, result.Message, InfoBarSeverity.Informational);
                }
                else
                {
                    card.MarkFailed(result.Message);
                    ShowInfo(result.Title, result.Message, InfoBarSeverity.Warning);
                    RecordFailure($"{operationName}: {result.Message}");
                    Notify($"{operationName}: {result.Title}", result.Message);
                }

                await UpdateDashboardAsync(_shutdownCts.Token);
            }
            catch (OperationCanceledException)
            {
                card.MarkFailed("Operation canceled.");
                AppendLog($"{operationName} canceled.");
            }
            catch (Exception ex)
            {
                card.MarkFailed(ex.Message);
                ShowInfo("Error", ex.Message, InfoBarSeverity.Error);
                AppendLog($"Error: {ex.Message}");
                RecordFailure($"{operationName}: {ex.Message}");
                Notify($"{operationName}: Error", ex.Message);
            }
        }

        private bool Confirm(string title, string message)
        {
            return WpfMessageBox.Show(this, message, title, System.Windows.MessageBoxButton.YesNo, MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes;
        }

        private void Notify(string title, string message)
        {
            if (Config.Tray?.Enabled != true)
            {
                return;
            }

            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = message;
            _notifyIcon.ShowBalloonTip(3000);
        }

        private static async Task<bool> WaitUntilAsync(
            Func<bool> predicate,
            int attempts,
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            for (int i = 0; i < attempts; i++)
            {
                if (await Task.Run(predicate, cancellationToken))
                {
                    return true;
                }

                await Task.Delay(delay, cancellationToken);
            }

            return false;
        }

        private void RunBackground(Func<Task> action, string operationName)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await action();
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    AppendLog($"{operationName} failed: {ex.Message}");
                    RecordFailure($"{operationName}: {ex.Message}");
                    ShowInfo("Error", $"{operationName} failed. {ex.Message}", InfoBarSeverity.Error);
                }
            });
        }

        private void RecordFailure(string failure)
        {
            Dispatcher.Invoke(() =>
            {
                _recentFailures.Enqueue($"[{DateTime.Now:HH:mm:ss}] {failure}");
                while (_recentFailures.Count > MaxRecentFailures)
                {
                    _recentFailures.Dequeue();
                }

                DiagnosticsText = BuildDiagnosticsText(CreateDiagnosticsSnapshot());
            });
        }

        private async Task RefreshGpuDevicesAsync()
        {
            IReadOnlyList<HardwareDeviceDescriptor> devices = await _gpuManager.ListDisplayDevicesAsync(_shutdownCts.Token);
            await Dispatcher.InvokeAsync(() =>
            {
                DisplayDevices.Clear();
                foreach (HardwareDeviceDescriptor device in devices)
                {
                    DisplayDevices.Add(device);
                }

                OnPropertyChanged(nameof(DisplayDevices));
            });
        }

        private void ClearTerminal_Click(object sender, RoutedEventArgs e)
        {
            _logLines.Clear();
            TxtTerminal.Text = string.Empty;
        }

        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            SettingsFlyout.IsOpen = true;
            RunBackground(RefreshGpuDevicesAsync, "GPU device refresh");
        }

        private void RefreshGpuDevices_Click(object sender, RoutedEventArgs e)
        {
            RunBackground(RefreshGpuDevicesAsync, "GPU device refresh");
        }

        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            _configManager.Save();
            _gpuManager = CreateGpuManager();
            OnPropertyChanged(nameof(Config));
            _statusTimer.Interval = TimeSpan.FromSeconds(GetPollIntervalSeconds());
            ConfigureTray();
            SettingsFlyout.IsOpen = false;
            AppendLog("Settings saved and applied.");
            RunBackground(() => UpdateDashboardAsync(_shutdownCts.Token), "Dashboard refresh");
        }

        private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Clipboard.SetText(DiagnosticsText);
            ShowInfo("Copied", "Diagnostics copied to clipboard.", InfoBarSeverity.Success);
        }

        private void ExportLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string directory = ConfigManager.GetLogDirectory();
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, $"pokeswitch-export-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

                var builder = new StringBuilder();
                builder.AppendLine("PokeSwitch Diagnostics");
                builder.AppendLine(DiagnosticsText);
                builder.AppendLine();
                builder.AppendLine("Live Terminal");
                foreach (string line in _logLines)
                {
                    builder.AppendLine(line);
                }

                File.WriteAllText(path, builder.ToString());
                ShowInfo("Exported", $"Log exported to {path}", InfoBarSeverity.Success);
                AppendLog($"Log exported to {path}");
            }
            catch (Exception ex)
            {
                ShowInfo("Export Failed", ex.Message, InfoBarSeverity.Error);
                RecordFailure($"Log export: {ex.Message}");
            }
        }

        private int GetPollIntervalSeconds()
        {
            return Config.Dashboard?.PollIntervalSeconds ?? 3;
        }

        private DiagnosticSnapshot CreateDiagnosticsSnapshot()
        {
            return new DiagnosticSnapshot(
                IsAdministrator(),
                _configManager.ConfigFilePath,
                _isDockerRunning,
                _isWslRunning,
                _isWslKeepAliveRunning,
                _dockerManager.GetInstalledDistros(),
                _gpuStatus,
                _recentFailures.ToArray());
        }

        private static string BuildDiagnosticsText(DiagnosticSnapshot snapshot)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"Admin: {(snapshot.IsAdministrator ? "Yes" : "No")}");
            builder.AppendLine($"Config: {snapshot.ConfigPath}");
            builder.AppendLine($"Docker: {(snapshot.DockerDesktopRunning ? "Running" : "Stopped")}");
            builder.AppendLine($"WSL: {(snapshot.WslRunning ? "Active" : "Inactive")}");
            builder.AppendLine($"WSL Keep-Alive: {(snapshot.WslKeepAliveRunning ? "Running" : "Stopped")}");
            builder.AppendLine($"Distros: {(snapshot.InstalledDistros.Length == 0 ? "-" : string.Join(", ", snapshot.InstalledDistros))}");
            builder.AppendLine($"GPU: {(snapshot.GpuStatus.Found ? snapshot.GpuStatus.FriendlyName : snapshot.GpuStatus.Message)}");
            builder.AppendLine($"GPU Status: {snapshot.GpuStatus.Status ?? "-"}");

            if (snapshot.RecentFailures.Count > 0)
            {
                builder.AppendLine("Recent Failures:");
                foreach (string failure in snapshot.RecentFailures)
                {
                    builder.AppendLine($"- {failure}");
                }
            }

            return builder.ToString().TrimEnd();
        }

        private static bool IsAdministrator()
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (WindowState == WindowState.Minimized && Config.Tray?.Enabled == true && Config.Tray.MinimizeToTray)
            {
                Hide();
                _notifyIcon.Visible = true;
                Notify("PokeSwitch", "Still running in the tray.");
            }
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void CloseForExit()
        {
            Config.Tray!.MinimizeToTray = false;
            Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (Config.Tray?.Enabled == true && Config.Tray.MinimizeToTray && !_shutdownCts.IsCancellationRequested)
            {
                e.Cancel = true;
                Hide();
                _notifyIcon.Visible = true;
                Notify("PokeSwitch", "Still running in the tray.");
                return;
            }

            _shutdownCts.Cancel();
            _statusTimer.Stop();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            base.OnClosing(e);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
