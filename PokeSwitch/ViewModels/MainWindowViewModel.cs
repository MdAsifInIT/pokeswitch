using System.Collections.ObjectModel;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Windows.Input;
using System.Windows.Threading;
using PokeSwitch.Models;
using PokeSwitch.Services;
using Wpf.Ui.Controls;

namespace PokeSwitch.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    private const int MaxRecentFailures = 10;

    private readonly ConfigManager _configManager;
    private readonly IDockerManager _dockerManager;
    private readonly Func<HardwareConfig?, IGpuManager> _gpuManagerFactory;
    private readonly IInteractionService _interactionService;
    private readonly ITrayService _trayService;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _statusTimer;
    private readonly SemaphoreSlim _dashboardRefreshGate = new(1, 1);
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Queue<string> _logLines = new();
    private readonly Queue<string> _recentFailures = new();
    private IGpuManager _gpuManager;
    private bool _allowExit;
    private bool _disposed;
    private bool _isDockerRunning;
    private bool _isWslRunning;
    private bool _isWslKeepAliveRunning;
    private GpuStatus _gpuStatus = new(false, false, null, null, null, false, "GPU status has not been checked yet.");

    private string _dockerStatusText = "Checking...";
    private string _dockerStatusColor = "Gray";
    private string _wslStatusText = "Checking...";
    private string _wslStatusColor = "Gray";
    private string _containerText = "0 / 0";
    private string _ramUsageText = "0 MB";
    private string _diagnosticsText = "Collecting diagnostics...";
    private string _terminalText = string.Empty;
    private bool _isSettingsOpen;
    private bool _isDiagnosticsExpanded;
    private bool _isInfoOpen;
    private string _infoTitle = "Status";
    private string _infoMessage = "Ready";
    private InfoBarSeverity _infoSeverity = InfoBarSeverity.Informational;

    public MainWindowViewModel(
        ConfigManager configManager,
        IDockerManager dockerManager,
        Func<HardwareConfig?, IGpuManager> gpuManagerFactory,
        IInteractionService interactionService,
        ITrayService trayService)
    {
        _configManager = configManager;
        _dockerManager = dockerManager;
        _gpuManagerFactory = gpuManagerFactory;
        _gpuManager = gpuManagerFactory(Config.Hardware);
        _interactionService = interactionService;
        _trayService = trayService;
        _dispatcher = Dispatcher.CurrentDispatcher;

        WslToggle = new ToggleCardState("Play24", "Start WSL Keep-Alive", "Headless Server Mode (sleep infinity)", "#0078D4");
        DockerToggle = new ToggleCardState("ArrowSync24", "Boot Docker Engine", "Full spin-up of Docker & all containers", "#107C10");
        GpuToggle = new ToggleCardState("DeveloperBoard24", "Checking GPU...", "Configured display device status", "Gray");
        NuclearToggle = new ToggleCardState("Power24", "Nuclear Shutdown", "Purge all Docker + WSL processes", "#D13438");

        ToggleWslCommand = new AsyncRelayCommand(ToggleWslAsync);
        ToggleDockerCommand = new AsyncRelayCommand(ToggleDockerAsync);
        ToggleGpuCommand = new AsyncRelayCommand(ToggleGpuAsync);
        NuclearShutdownCommand = new AsyncRelayCommand(NuclearShutdownAsync);
        RefreshGpuDevicesCommand = new AsyncRelayCommand(RefreshGpuDevicesAsync);
        OpenSettingsCommand = new AsyncRelayCommand(OpenSettingsAsync);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync);
        ClearTerminalCommand = new AsyncRelayCommand(ClearTerminalAsync);
        CopyDiagnosticsCommand = new AsyncRelayCommand(CopyDiagnosticsAsync);
        ExportLogCommand = new AsyncRelayCommand(ExportLogAsync);
        ToggleDiagnosticsCommand = new AsyncRelayCommand(ToggleDiagnosticsAsync);

        _statusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(GetPollIntervalSeconds())
        };
        _statusTimer.Tick += StatusTimer_Tick;
        ConfigureTray();
    }

    public event Action? RestoreWindowRequested;
    public event Action? ExitApplicationRequested;

    public AppConfig Config => _configManager.CurrentConfig;
    public ObservableCollection<HardwareDeviceDescriptor> DisplayDevices { get; } = new();

    public ToggleCardState WslToggle { get; }
    public ToggleCardState DockerToggle { get; }
    public ToggleCardState GpuToggle { get; }
    public ToggleCardState NuclearToggle { get; }

    public ICommand ToggleWslCommand { get; }
    public ICommand ToggleDockerCommand { get; }
    public ICommand ToggleGpuCommand { get; }
    public ICommand NuclearShutdownCommand { get; }
    public ICommand RefreshGpuDevicesCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand ClearTerminalCommand { get; }
    public ICommand CopyDiagnosticsCommand { get; }
    public ICommand ExportLogCommand { get; }
    public ICommand ToggleDiagnosticsCommand { get; }

    public string DockerStatusText { get => _dockerStatusText; set => SetField(ref _dockerStatusText, value); }
    public string DockerStatusColor { get => _dockerStatusColor; set => SetField(ref _dockerStatusColor, value); }
    public string WslStatusText { get => _wslStatusText; set => SetField(ref _wslStatusText, value); }
    public string WslStatusColor { get => _wslStatusColor; set => SetField(ref _wslStatusColor, value); }
    public string ContainerText { get => _containerText; set => SetField(ref _containerText, value); }
    public string RamUsageText { get => _ramUsageText; set => SetField(ref _ramUsageText, value); }
    public string DiagnosticsText { get => _diagnosticsText; set => SetField(ref _diagnosticsText, value); }
    public string TerminalText { get => _terminalText; set => SetField(ref _terminalText, value); }
    public bool IsSettingsOpen { get => _isSettingsOpen; set => SetField(ref _isSettingsOpen, value); }
    public bool IsDiagnosticsExpanded { get => _isDiagnosticsExpanded; set => SetField(ref _isDiagnosticsExpanded, value); }
    public bool IsInfoOpen { get => _isInfoOpen; set => SetField(ref _isInfoOpen, value); }
    public string InfoTitle { get => _infoTitle; set => SetField(ref _infoTitle, value); }
    public string InfoMessage { get => _infoMessage; set => SetField(ref _infoMessage, value); }
    public InfoBarSeverity InfoSeverity { get => _infoSeverity; set => SetField(ref _infoSeverity, value); }

    public void Start()
    {
        _statusTimer.Start();
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

    public bool ShouldMinimizeToTray()
    {
        return Config.Tray?.Enabled == true && Config.Tray.MinimizeToTray;
    }

    public bool TryCancelCloseToTray()
    {
        if (!ShouldMinimizeToTray() || _allowExit || _shutdownCts.IsCancellationRequested)
        {
            return false;
        }

        _trayService.Notify("PokeSwitch", "Still running in the tray.");
        return true;
    }

    public void NotifyMinimizedToTray()
    {
        _trayService.Notify("PokeSwitch", "Still running in the tray.");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _allowExit = true;
        _shutdownCts.Cancel();
        _statusTimer.Stop();
        _trayService.Dispose();
        _shutdownCts.Dispose();
        _dashboardRefreshGate.Dispose();
    }

    private void ConfigureTray()
    {
        _trayService.Configure(
            Config,
            new TrayActionHandlers(
                () => DispatchAsync(ToggleWslAsync),
                () => DispatchAsync(ToggleDockerAsync),
                () => DispatchAsync(ToggleGpuAsync),
                () => DispatchAsync(NuclearShutdownAsync),
                () => Dispatch(() => RestoreWindowRequested?.Invoke()),
                () =>
                {
                    _allowExit = true;
                    Dispatch(() => ExitApplicationRequested?.Invoke());
                }));
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

            await DispatchAsync(() => ApplyDashboardState(
                dockerTask.Result,
                wslTask.Result,
                wslKeepAliveTask.Result,
                containersTask.Result,
                ramTask.Result,
                gpuTask.Result));
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

    private async Task ToggleDockerAsync()
    {
        await RunToggleOperationAsync(DockerToggle, "Docker Engine", async cancellationToken =>
        {
            if (_isDockerRunning)
            {
                if (Config.Toggles?.ConfirmDockerStop == true && !_interactionService.Confirm("Stop Docker Engine", "Stop Docker Desktop and all running containers?"))
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

    private async Task ToggleGpuAsync()
    {
        await RunToggleOperationAsync(GpuToggle, "NVIDIA GPU", async cancellationToken =>
        {
            if (_gpuStatus is { Found: true, IsEnabled: true }
                && Config.Toggles?.ConfirmGpuDisable == true
                && !_interactionService.Confirm("Disable NVIDIA GPU", "Disable the selected NVIDIA GPU device? Close GPU-using apps first."))
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

    private async Task NuclearShutdownAsync()
    {
        await RunToggleOperationAsync(NuclearToggle, "Nuclear Shutdown", async cancellationToken =>
        {
            if (Config.Toggles?.ConfirmNuclearShutdown == true
                && !_interactionService.Confirm("Nuclear Shutdown", "Stop Docker, containers, and shut down all WSL instances?"))
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
                _trayService.Notify($"{operationName}: {result.Title}", result.Message);
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
                _trayService.Notify($"{operationName}: {result.Title}", result.Message);
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
            _trayService.Notify($"{operationName}: Error", ex.Message);
        }
    }

    private async Task OpenSettingsAsync()
    {
        IsSettingsOpen = true;
        await RefreshGpuDevicesAsync();
    }

    private async Task SaveSettingsAsync()
    {
        _configManager.Save();
        _gpuManager = _gpuManagerFactory(Config.Hardware);
        OnPropertyChanged(nameof(Config));
        _statusTimer.Interval = TimeSpan.FromSeconds(GetPollIntervalSeconds());
        ConfigureTray();
        IsSettingsOpen = false;
        AppendLog("Settings saved and applied.");
        await UpdateDashboardAsync(_shutdownCts.Token);
    }

    private Task ClearTerminalAsync()
    {
        _logLines.Clear();
        TerminalText = string.Empty;
        return Task.CompletedTask;
    }

    private Task CopyDiagnosticsAsync()
    {
        _interactionService.CopyToClipboard(DiagnosticsText);
        ShowInfo("Copied", "Diagnostics copied to clipboard.", InfoBarSeverity.Success);
        return Task.CompletedTask;
    }

    private Task ToggleDiagnosticsAsync()
    {
        IsDiagnosticsExpanded = !IsDiagnosticsExpanded;
        return Task.CompletedTask;
    }

    private async Task ExportLogAsync()
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

            await File.WriteAllTextAsync(path, builder.ToString());
            ShowInfo("Exported", $"Log exported to {path}", InfoBarSeverity.Success);
            AppendLog($"Log exported to {path}");
        }
        catch (Exception ex)
        {
            ShowInfo("Export Failed", ex.Message, InfoBarSeverity.Error);
            RecordFailure($"Log export: {ex.Message}");
        }
    }

    private async Task RefreshGpuDevicesAsync()
    {
        IReadOnlyList<HardwareDeviceDescriptor> devices = await _gpuManager.ListDisplayDevicesAsync(_shutdownCts.Token);
        await DispatchAsync(() =>
        {
            DisplayDevices.Clear();
            foreach (HardwareDeviceDescriptor device in devices)
            {
                DisplayDevices.Add(device);
            }

            OnPropertyChanged(nameof(DisplayDevices));
        });
    }

    private void AppendLog(string message)
    {
        if (!_dispatcher.CheckAccess())
        {
            Dispatch(() => AppendLog(message));
            return;
        }

        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        string formatted = $"[{timestamp}] {message}";

        if (Config.Logging?.FileEnabled == true)
        {
            WriteLogFile(formatted);
        }

        if (Config.Logging?.Enabled != true)
        {
            return;
        }

        _logLines.Enqueue(formatted);
        int maxLines = Math.Clamp(Config.Logging.MaxLines, 50, 5000);
        while (_logLines.Count > maxLines)
        {
            _logLines.Dequeue();
        }

        TerminalText = string.Join(Environment.NewLine, _logLines);
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
        if (!_dispatcher.CheckAccess())
        {
            Dispatch(() => ShowInfo(title, message, severity));
            return;
        }

        InfoTitle = title;
        InfoMessage = message;
        InfoSeverity = severity;
        IsInfoOpen = true;
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
        if (!_dispatcher.CheckAccess())
        {
            Dispatch(() => RecordFailure(failure));
            return;
        }

        _recentFailures.Enqueue($"[{DateTime.Now:HH:mm:ss}] {failure}");
        while (_recentFailures.Count > MaxRecentFailures)
        {
            _recentFailures.Dequeue();
        }

        DiagnosticsText = BuildDiagnosticsText(CreateDiagnosticsSnapshot());
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

    private void Dispatch(Action action)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.Invoke(action);
    }

    private Task DispatchAsync(Action action)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return Task.CompletedTask;
        }

        if (_dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return _dispatcher.InvokeAsync(action).Task;
    }

    private Task DispatchAsync(Func<Task> action)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return Task.CompletedTask;
        }

        return _dispatcher.CheckAccess()
            ? action()
            : _dispatcher.InvokeAsync(action).Task.Unwrap();
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
}
