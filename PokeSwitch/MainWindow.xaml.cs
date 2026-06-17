using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using PokeSwitch.Models;
using PokeSwitch.Services;
using Wpf.Ui.Controls;

namespace PokeSwitch
{
    public partial class MainWindow : FluentWindow, INotifyPropertyChanged
    {
        private readonly ConfigManager _configManager;
        private readonly DockerManager _dockerManager;
        private readonly GpuManager _gpuManager;
        private readonly DispatcherTimer _statusTimer;
        private readonly SemaphoreSlim _dashboardRefreshGate = new(1, 1);
        private readonly SemaphoreSlim _operationGate = new(1, 1);
        private readonly CancellationTokenSource _shutdownCts = new();
        private readonly Queue<string> _logLines = new();

        public AppConfig Config => _configManager.CurrentConfig;

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

        private string _wslCardIcon = "Play24";
        public string WslCardIcon { get => _wslCardIcon; set { _wslCardIcon = value; OnPropertyChanged(); } }

        private string _wslCardLabel = "Start WSL Keep-Alive";
        public string WslCardLabel { get => _wslCardLabel; set { _wslCardLabel = value; OnPropertyChanged(); } }

        private string _wslCardSub = "Headless Server Mode (sleep infinity)";
        public string WslCardSub { get => _wslCardSub; set { _wslCardSub = value; OnPropertyChanged(); } }

        private string _wslCardAccent = "#0078D4";
        public string WslCardAccent { get => _wslCardAccent; set { _wslCardAccent = value; OnPropertyChanged(); } }

        private string _dockerCardIcon = "ArrowSync24";
        public string DockerCardIcon { get => _dockerCardIcon; set { _dockerCardIcon = value; OnPropertyChanged(); } }

        private string _dockerCardLabel = "Boot Docker Engine";
        public string DockerCardLabel { get => _dockerCardLabel; set { _dockerCardLabel = value; OnPropertyChanged(); } }

        private string _dockerCardSub = "Full spin-up of Docker & all containers";
        public string DockerCardSub { get => _dockerCardSub; set { _dockerCardSub = value; OnPropertyChanged(); } }

        private string _dockerCardAccent = "#107C10";
        public string DockerCardAccent { get => _dockerCardAccent; set { _dockerCardAccent = value; OnPropertyChanged(); } }

        private string _gpuCardIcon = "DeveloperBoard24";
        public string GpuCardIcon { get => _gpuCardIcon; set { _gpuCardIcon = value; OnPropertyChanged(); } }

        private string _gpuCardLabel = "Checking GPU...";
        public string GpuCardLabel { get => _gpuCardLabel; set { _gpuCardLabel = value; OnPropertyChanged(); } }

        private string _gpuCardSub = "NVIDIA RTX 3050 status";
        public string GpuCardSub { get => _gpuCardSub; set { _gpuCardSub = value; OnPropertyChanged(); } }

        private string _gpuCardAccent = "Gray";
        public string GpuCardAccent { get => _gpuCardAccent; set { _gpuCardAccent = value; OnPropertyChanged(); } }

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
            _gpuManager = new GpuManager();

            _statusTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(GetPollIntervalSeconds())
            };
            _statusTimer.Tick += StatusTimer_Tick;
            _statusTimer.Start();

            AppendLog("Application started.");
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

            if (isWslKeepAliveRunning)
            {
                WslCardLabel = "Stop WSL Keep-Alive";
                WslCardIcon = "Stop24";
                WslCardAccent = "#D83B01";
                WslCardSub = "Running in headless mode";
            }
            else
            {
                WslCardLabel = "Start WSL Keep-Alive";
                WslCardIcon = "Play24";
                WslCardAccent = "#0078D4";
                WslCardSub = "Headless Server Mode (sleep infinity)";
            }

            if (isDockerRunning)
            {
                DockerCardLabel = "Stop Docker Engine";
                DockerCardIcon = "Stop24";
                DockerCardAccent = "#D83B01";
                DockerCardSub = $"Running ({containers.running} containers active)";
            }
            else
            {
                DockerCardLabel = "Boot Docker Engine";
                DockerCardIcon = "ArrowSync24";
                DockerCardAccent = "#107C10";
                DockerCardSub = "Full spin-up of Docker & all containers";
            }

            ApplyGpuCardState(gpuStatus);
        }

        private void ApplyGpuCardState(GpuStatus gpuStatus)
        {
            GpuCardIcon = "DeveloperBoard24";

            if (gpuStatus.Multiple)
            {
                GpuCardLabel = "GPU Toggle Unavailable";
                GpuCardAccent = "#D13438";
                GpuCardSub = gpuStatus.Message;
                return;
            }

            if (!gpuStatus.Found)
            {
                GpuCardLabel = "GPU Not Found";
                GpuCardAccent = "Gray";
                GpuCardSub = gpuStatus.Message;
                return;
            }

            if (gpuStatus.IsEnabled)
            {
                GpuCardLabel = "Disable NVIDIA GPU";
                GpuCardAccent = "#D83B01";
                GpuCardSub = $"{gpuStatus.FriendlyName} is enabled";
            }
            else
            {
                GpuCardLabel = "Enable NVIDIA GPU";
                GpuCardAccent = "#107C10";
                GpuCardSub = $"{gpuStatus.FriendlyName} status: {gpuStatus.Status ?? "Unknown"}";
            }
        }

        private void AppendLog(string message)
        {
            if (Config.Logging?.Enabled != true || Dispatcher.HasShutdownStarted)
            {
                return;
            }

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AppendLog(message));
                return;
            }

            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            _logLines.Enqueue($"[{timestamp}] {message}");

            int maxLines = Math.Clamp(Config.Logging.MaxLines, 50, 5000);
            while (_logLines.Count > maxLines)
            {
                _logLines.Dequeue();
            }

            TxtTerminal.Text = string.Join(Environment.NewLine, _logLines);
            TerminalScrollViewer.ScrollToEnd();
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
            await RunUserOperationAsync(async cancellationToken =>
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

                    if (success) ShowInfo("Success", "WSL Keep-Alive is stopped.", InfoBarSeverity.Success);
                    else ShowInfo("Warning", "Stop command sent, but WSL is taking too long to update.", InfoBarSeverity.Warning);
                }
                else
                {
                    ShowInfo("Executing", "Starting WSL in Headless Mode...", InfoBarSeverity.Informational);
                    await _dockerManager.StartWslKeepAliveAsync(AppendLog, cancellationToken);

                    bool success = await WaitUntilAsync(
                        () => _dockerManager.IsWslKeepAliveRunning(),
                        attempts: 50,
                        delay: TimeSpan.FromMilliseconds(200),
                        cancellationToken);

                    if (success) ShowInfo("Success", "WSL Keep-Alive is now running in the background.", InfoBarSeverity.Success);
                    else ShowInfo("Warning", "Start command sent, but WSL is taking too long to respond.", InfoBarSeverity.Warning);
                }
            });
        }

        private async void DockerToggle_Click(object sender, RoutedEventArgs e)
        {
            await RunUserOperationAsync(async cancellationToken =>
            {
                if (_isDockerRunning)
                {
                    ShowInfo("Executing", "Stopping Docker and containers...", InfoBarSeverity.Warning);
                    await _dockerManager.StopDockerOnlyAsync(AppendLog, cancellationToken);

                    bool success = await WaitUntilAsync(
                        () => !_dockerManager.IsDockerDesktopRunning(),
                        attempts: 40,
                        delay: TimeSpan.FromMilliseconds(250),
                        cancellationToken);

                    if (success) ShowInfo("Success", "Docker is stopped.", InfoBarSeverity.Success);
                    else ShowInfo("Warning", "Docker stop command sent, but processes are taking too long to exit.", InfoBarSeverity.Warning);
                }
                else
                {
                    await _dockerManager.BootDockerEngineAsync(AppendLog, cancellationToken);
                    ShowInfo("Success", "Server Mode Active. All containers are online!", InfoBarSeverity.Success);
                }
            });
        }

        private async void GpuToggle_Click(object sender, RoutedEventArgs e)
        {
            await RunUserOperationAsync(async cancellationToken =>
            {
                ShowInfo("Executing", "Toggling NVIDIA GPU...", InfoBarSeverity.Warning);
                AppendLog($"GPU before toggle: {_gpuStatus.Message}");

                GpuToggleResult result = await _gpuManager.ToggleAsync(cancellationToken);
                AppendLog($"GPU {result.Action}: {result.Message}");

                if (result.Success)
                {
                    ShowInfo("Success", result.Message, InfoBarSeverity.Success);
                }
                else
                {
                    ShowInfo("Warning", result.Message, InfoBarSeverity.Warning);
                }
            });
        }

        private async void NuclearShutdown_Click(object sender, RoutedEventArgs e)
        {
            await RunUserOperationAsync(async cancellationToken =>
            {
                await _dockerManager.NuclearShutdownAsync(AppendLog, cancellationToken);

                bool success = await WaitUntilAsync(
                    () => !_dockerManager.IsDockerDesktopRunning()
                        && !_dockerManager.IsWslRunning()
                        && !_dockerManager.IsWslKeepAliveRunning(),
                    attempts: 60,
                    delay: TimeSpan.FromMilliseconds(250),
                    cancellationToken);

                if (success) ShowInfo("Success", "Desktop Mode Active. RAM and Battery saved!", InfoBarSeverity.Success);
                else ShowInfo("Warning", "Nuclear shutdown sent, but some processes are taking a long time to exit.", InfoBarSeverity.Warning);
            });
        }

        private async Task RunUserOperationAsync(Func<CancellationToken, Task> operation)
        {
            if (!await _operationGate.WaitAsync(0))
            {
                ShowInfo("Busy", "Another operation is already running.", InfoBarSeverity.Warning);
                return;
            }

            _statusTimer.Stop();
            try
            {
                await operation(_shutdownCts.Token);
                await UpdateDashboardAsync(_shutdownCts.Token);
            }
            catch (OperationCanceledException)
            {
                AppendLog("Operation canceled.");
            }
            catch (Exception ex)
            {
                ShowInfo("Error", ex.Message, InfoBarSeverity.Error);
                AppendLog($"Error: {ex.Message}");
            }
            finally
            {
                if (!_shutdownCts.IsCancellationRequested)
                {
                    _statusTimer.Start();
                }

                _operationGate.Release();
            }
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
                    ShowInfo("Error", $"{operationName} failed. {ex.Message}", InfoBarSeverity.Error);
                }
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
        }

        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            _configManager.Save();
            OnPropertyChanged(nameof(Config));
            _statusTimer.Interval = TimeSpan.FromSeconds(GetPollIntervalSeconds());
            SettingsFlyout.IsOpen = false;
            AppendLog("Settings saved and applied.");
        }

        private int GetPollIntervalSeconds()
        {
            return Config.Dashboard?.PollIntervalSeconds ?? 3;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            _shutdownCts.Cancel();
            _statusTimer.Stop();
            base.OnClosing(e);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
