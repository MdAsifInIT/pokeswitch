using System.ComponentModel;
using System.Runtime.CompilerServices;
using PokeSwitch.Services;

namespace PokeSwitch.Models;

public interface IToggleAction
{
    string Id { get; }
    string Name { get; }
    Task<ToggleActionResult> ExecuteAsync(CancellationToken cancellationToken = default);
}

public sealed record ToggleActionResult(
    bool Success,
    string Title,
    string Message,
    string Status = "Ready");

public sealed record HardwareDeviceDescriptor(
    string FriendlyName,
    string InstanceId,
    string Status);

public sealed record DiagnosticSnapshot(
    bool IsAdministrator,
    string ConfigPath,
    bool DockerDesktopRunning,
    bool WslRunning,
    bool WslKeepAliveRunning,
    string[] InstalledDistros,
    GpuStatus GpuStatus,
    IReadOnlyList<string> RecentFailures);

public sealed class ToggleCardState : INotifyPropertyChanged
{
    private string _icon;
    private string _label;
    private string _subtitle;
    private string _accent;
    private string _status;
    private bool _isEnabled = true;
    private bool _isRunning;
    private string _lastResult = "Ready";

    public ToggleCardState(string icon, string label, string subtitle, string accent)
    {
        _icon = icon;
        _label = label;
        _subtitle = subtitle;
        _accent = accent;
        _status = "Ready";
    }

    public string Icon { get => _icon; set => SetField(ref _icon, value); }
    public string Label { get => _label; set => SetField(ref _label, value); }
    public string Subtitle { get => _subtitle; set => SetField(ref _subtitle, value); }
    public string Accent { get => _accent; set => SetField(ref _accent, value); }
    public string Status { get => _status; set => SetField(ref _status, value); }
    public bool IsEnabled { get => _isEnabled; set => SetField(ref _isEnabled, value); }
    public bool IsRunning { get => _isRunning; set => SetField(ref _isRunning, value); }
    public string LastResult { get => _lastResult; set => SetField(ref _lastResult, value); }

    public void MarkRunning(string message = "Running")
    {
        IsRunning = true;
        IsEnabled = false;
        Status = message;
        LastResult = message;
    }

    public void MarkReady(string message = "Ready")
    {
        IsRunning = false;
        IsEnabled = true;
        Status = "Ready";
        LastResult = message;
    }

    public void MarkFailed(string message)
    {
        IsRunning = false;
        IsEnabled = true;
        Status = "Failed";
        LastResult = message;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
