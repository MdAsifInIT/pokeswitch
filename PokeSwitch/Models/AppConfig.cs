using System.Text.Json.Serialization;

namespace PokeSwitch.Models;

public class LoggingConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("maxLines")]
    public int MaxLines { get; set; } = 500;
}

public class DashboardConfig
{
    [JsonPropertyName("pollIntervalSeconds")]
    public int PollIntervalSeconds { get; set; } = 3;
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

    [JsonPropertyName("autoStartWslOnLaunch")]
    public bool AutoStartWslOnLaunch { get; set; }

    [JsonPropertyName("autoStartDockerOnLaunch")]
    public bool AutoStartDockerOnLaunch { get; set; }
}
