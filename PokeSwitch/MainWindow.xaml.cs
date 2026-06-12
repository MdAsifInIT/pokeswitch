using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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
        private readonly DispatcherTimer _statusTimer;

        public AppConfig Config => _configManager.CurrentConfig;

        // Dashboard Properties
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

        // Control Card Properties
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

        private bool _isDockerRunning;
        private bool _isWslRunning;
        private bool _isWslKeepAliveRunning;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            _configManager = new ConfigManager();
            _configManager.Load();

            _dockerManager = new DockerManager(Config);

            _statusTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(Config.Dashboard.PollIntervalSeconds)
            };
            _statusTimer.Tick += StatusTimer_Tick;
            _statusTimer.Start();

            // NotifyIcon for tray could go here if implemented, but we rely on simple minimize for now.

            AppendLog("Application started.");
            _ = UpdateDashboard();

            if (Config.AutoStartWslOnLaunch)
            {
                AppendLog("Auto-starting WSL (config).");
                _ = _dockerManager.StartWslKeepAliveAsync(AppendLog);
            }
            if (Config.AutoStartDockerOnLaunch)
            {
                AppendLog("Auto-starting Docker (config).");
                _ = _dockerManager.BootDockerEngineAsync(AppendLog);
            }
        }

        private async void StatusTimer_Tick(object? sender, EventArgs e)
        {
            await UpdateDashboard();
        }

        private async Task UpdateDashboard()
        {
            await Task.Run(async () =>
            {
                var dockerTask = Task.Run(() => _dockerManager.IsDockerDesktopRunning());
                var wslTask = Task.Run(() => _dockerManager.IsWslRunning());
                var wslKeepAliveTask = Task.Run(() => _dockerManager.IsWslKeepAliveRunning());
                var containersTask = _dockerManager.GetRunningContainerCountAsync();
                var ramTask = Task.Run(() => _dockerManager.GetVmMemUsageMB());

                await Task.WhenAll(dockerTask, wslTask, wslKeepAliveTask, containersTask, ramTask);

                _isDockerRunning = dockerTask.Result;
                _isWslRunning = wslTask.Result;
                _isWslKeepAliveRunning = wslKeepAliveTask.Result;
                var containers = containersTask.Result;
                int ram = ramTask.Result;

                Dispatcher.Invoke(() =>
                {
                    // Update Tiles
                    if (_isDockerRunning)
                    {
                        DockerStatusText = "Running";
                        DockerStatusColor = "#107C10";
                    }
                    else
                    {
                        DockerStatusText = "Stopped";
                        DockerStatusColor = "#D13438";
                    }

                    if (_isWslRunning)
                    {
                        WslStatusText = "Active";
                        WslStatusColor = "#0078D4";
                    }
                    else
                    {
                        WslStatusText = "Inactive";
                        WslStatusColor = "Gray";
                    }

                    ContainerText = _isDockerRunning ? $"{containers.running} / {containers.total}" : "—";
                    RamUsageText = ram > 0 ? $"{ram} MB" : "— (idle)";

                    // Update Toggle Cards
                    if (_isWslKeepAliveRunning)
                    {
                        WslCardLabel = "Stop WSL Keep-Alive";
                        WslCardIcon = "Stop24";
                        WslCardAccent = "#D83B01"; // Amber
                        WslCardSub = "Running in headless mode";
                    }
                    else
                    {
                        WslCardLabel = "Start WSL Keep-Alive";
                        WslCardIcon = "Play24";
                        WslCardAccent = "#0078D4"; // Blue
                        WslCardSub = "Headless Server Mode (sleep infinity)";
                    }

                    if (_isDockerRunning)
                    {
                        DockerCardLabel = "Stop Docker Engine";
                        DockerCardIcon = "Stop24";
                        DockerCardAccent = "#D83B01"; // Amber
                        DockerCardSub = $"Running ({containers.running} containers active)";
                    }
                    else
                    {
                        DockerCardLabel = "Boot Docker Engine";
                        DockerCardIcon = "ArrowSync24";
                        DockerCardAccent = "#107C10"; // Green
                        DockerCardSub = "Full spin-up of Docker & all containers";
                    }
                });
            });
        }

        private void AppendLog(string message)
        {
            if (!Config.Logging.Enabled) return;

            Dispatcher.Invoke(() =>
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                string formatted = $"[{timestamp}] {message}\n";
                
                TxtTerminal.Text += formatted;

                // Simple truncation logic
                var lines = TxtTerminal.Text.Split('\n');
                if (lines.Length > Config.Logging.MaxLines)
                {
                    TxtTerminal.Text = string.Join('\n', lines[(lines.Length - Config.Logging.MaxLines)..]);
                }

                TerminalScrollViewer.ScrollToEnd();
            });
        }

        private void ShowInfo(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
        {
            Dispatcher.Invoke(() =>
            {
                StatusInfoBar.Title = title;
                StatusInfoBar.Message = message;
                StatusInfoBar.Severity = severity;
                StatusInfoBar.IsOpen = true;
            });
        }

        private async void WslToggle_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_isWslKeepAliveRunning)
                {
                    ShowInfo("Executing", "Stopping WSL Keep-Alive...", InfoBarSeverity.Warning);
                    await _dockerManager.StopWslAsync(AppendLog);
                    
                    bool success = false;
                    for (int i = 0; i < 50; i++)
                    {
                        if (!_dockerManager.IsWslKeepAliveRunning()) { success = true; break; }
                        await Task.Delay(200);
                    }
                    if (success) ShowInfo("Success", "WSL Keep-Alive is stopped.", InfoBarSeverity.Success);
                    else ShowInfo("Warning", "Stop command sent, but WSL is taking too long to update.", InfoBarSeverity.Warning);
                }
                else
                {
                    ShowInfo("Executing", "Starting WSL in Headless Mode...", InfoBarSeverity.Informational);
                    await _dockerManager.StartWslKeepAliveAsync(AppendLog);
                    
                    bool success = false;
                    for (int i = 0; i < 50; i++)
                    {
                        if (_dockerManager.IsWslKeepAliveRunning()) { success = true; break; }
                        await Task.Delay(200);
                    }
                    if (success) ShowInfo("Success", "WSL Keep-Alive is now running in the background.", InfoBarSeverity.Success);
                    else ShowInfo("Warning", "Start command sent, but WSL is taking too long to respond.", InfoBarSeverity.Warning);
                }
                await UpdateDashboard();
            }
            catch (Exception ex)
            {
                ShowInfo("Error", ex.Message, InfoBarSeverity.Error);
                AppendLog($"Error: {ex.Message}");
            }
        }

        private async void DockerToggle_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_isDockerRunning)
                {
                    ShowInfo("Executing", "Stopping Docker and containers...", InfoBarSeverity.Warning);
                    await _dockerManager.StopDockerOnlyAsync(AppendLog);
                    
                    bool success = false;
                    for (int i = 0; i < 40; i++)
                    {
                        if (!_dockerManager.IsDockerDesktopRunning()) { success = true; break; }
                        await Task.Delay(250);
                    }
                    if (success) ShowInfo("Success", "Docker is stopped.", InfoBarSeverity.Success);
                    else ShowInfo("Warning", "Docker stop command sent, but processes are taking too long to exit.", InfoBarSeverity.Warning);
                }
                else
                {
                    await _dockerManager.BootDockerEngineAsync(AppendLog);
                    ShowInfo("Success", "Server Mode Active. All containers are online!", InfoBarSeverity.Success);
                }
                await UpdateDashboard();
            }
            catch (Exception ex)
            {
                ShowInfo("Error", ex.Message, InfoBarSeverity.Error);
                AppendLog($"Error: {ex.Message}");
            }
        }

        private async void NuclearShutdown_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await _dockerManager.NuclearShutdownAsync(AppendLog);
                
                bool success = false;
                for (int i = 0; i < 60; i++)
                {
                    if (!_dockerManager.IsDockerDesktopRunning() && !_dockerManager.IsWslRunning() && !_dockerManager.IsWslKeepAliveRunning())
                    {
                        success = true;
                        break;
                    }
                    await Task.Delay(250);
                }
                if (success) ShowInfo("Success", "Desktop Mode Active. RAM and Battery saved!", InfoBarSeverity.Success);
                else ShowInfo("Warning", "Nuclear shutdown sent, but some processes are taking a long time to exit.", InfoBarSeverity.Warning);
                
                await UpdateDashboard();
            }
            catch (Exception ex)
            {
                ShowInfo("Error", ex.Message, InfoBarSeverity.Error);
                AppendLog($"Error: {ex.Message}");
            }
        }

        private void ClearTerminal_Click(object sender, RoutedEventArgs e)
        {
            TxtTerminal.Text = string.Empty;
        }

        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            SettingsFlyout.IsOpen = true;
        }

        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            _configManager.Save();
            _statusTimer.Interval = TimeSpan.FromSeconds(Config.Dashboard.PollIntervalSeconds);
            SettingsFlyout.IsOpen = false;
            AppendLog("Settings saved and applied.");
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}