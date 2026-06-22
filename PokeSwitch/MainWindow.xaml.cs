using System.ComponentModel;
using System.Windows;
using PokeSwitch.Services;
using PokeSwitch.ViewModels;
using Wpf.Ui.Controls;

namespace PokeSwitch;

public partial class MainWindow : FluentWindow
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        var configManager = new ConfigManager();
        configManager.Load();

        var processRunner = new ProcessRunner();
        var dockerManager = new DockerManager(configManager.CurrentConfig, processRunner);
        Func<Models.HardwareConfig?, IGpuManager> gpuManagerFactory = hardwareConfig => new GpuManager(hardwareConfig, processRunner);

        _viewModel = new MainWindowViewModel(
            configManager,
            dockerManager,
            gpuManagerFactory,
            new WpfInteractionService(this),
            new TrayService());

        _viewModel.RestoreWindowRequested += RestoreFromTray;
        _viewModel.ExitApplicationRequested += CloseForExit;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        DataContext = _viewModel;

        _viewModel.Start();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.TerminalText))
        {
            Dispatcher.InvokeAsync(() => TerminalScrollViewer.ScrollToEnd());
        }
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized && _viewModel.ShouldMinimizeToTray())
        {
            Hide();
            _viewModel.NotifyMinimizedToTray();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.TryCancelCloseToTray())
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.Dispose();
        base.OnClosing(e);
    }

    private void RestoreFromTray()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(RestoreFromTray);
            return;
        }

        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void CloseForExit()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(CloseForExit);
            return;
        }

        Close();
    }
}
