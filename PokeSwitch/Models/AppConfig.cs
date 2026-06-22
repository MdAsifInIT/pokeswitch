using System.Text.Json.Serialization;

namespace PokeSwitch.Models;

public class LoggingConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("fileEnabled")]
    public bool FileEnabled { get; set; }

    [JsonPropertyName("maxLines")]
    public int MaxLines { get; set; } = 500;
}

public class DashboardConfig
{
    [JsonPropertyName("pollIntervalSeconds")]
    public int PollIntervalSeconds { get; set; } = 3;
}

public class HardwareConfig
{
    [JsonPropertyName("gpuDeviceNamePattern")]
    public string GpuDeviceNamePattern { get; set; } = "*NVIDIA*RTX 3050*";

    [JsonPropertyName("gpuInstanceId")]
    public string? GpuInstanceId { get; set; }
}

public class TogglesConfig
{
    [JsonPropertyName("confirmGpuDisable")]
    public bool ConfirmGpuDisable { get; set; } = true;

    [JsonPropertyName("confirmDockerStop")]
    public bool ConfirmDockerStop { get; set; } = true;

    [JsonPropertyName("confirmNuclearShutdown")]
    public bool ConfirmNuclearShutdown { get; set; } = true;
}

public class TrayConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("minimizeToTray")]
    public bool MinimizeToTray { get; set; } = true;
}

public class AppConfig
{
    [JsonPropertyName("wslDistroName")]
    public string WslDistroName { get; set; } = "Ubuntu";

    [JsonPropertyName("dockerDesktopPath")]
    public string DockerDesktopPath { get; set; } = @"C:\Program Files\Docker\Docker\Docker Desktop.exe";

    [JsonPropertyName("logging")]
    public LoggingConfig? Logging { get; set; } = new();

    [JsonPropertyName("dashboard")]
    public DashboardConfig? Dashboard { get; set; } = new();

    [JsonPropertyName("hardware")]
    public HardwareConfig? Hardware { get; set; } = new();

    [JsonPropertyName("toggles")]
    public TogglesConfig? Toggles { get; set; } = new();

    [JsonPropertyName("tray")]
    public TrayConfig? Tray { get; set; } = new();

    [JsonPropertyName("autoStartWslOnLaunch")]
    public bool AutoStartWslOnLaunch { get; set; }

    [JsonPropertyName("autoStartDockerOnLaunch")]
    public bool AutoStartDockerOnLaunch { get; set; }
}
