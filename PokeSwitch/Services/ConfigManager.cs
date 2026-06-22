using System.IO;
using System.Text.Json;
using PokeSwitch.Models;

namespace PokeSwitch.Services;

public interface IConfigManager
{
    AppConfig CurrentConfig { get; }
    string ConfigFilePath { get; }
    void Load();
    void Save();
}

public class ConfigManager : IConfigManager
{
    private const int MinimumPollIntervalSeconds = 1;
    private const int MaximumPollIntervalSeconds = 60;
    private const int MinimumLogLines = 50;
    private const int MaximumLogLines = 5000;

    private readonly string _configFilePath;

    public AppConfig CurrentConfig { get; private set; }
    public string ConfigFilePath => _configFilePath;

    public ConfigManager()
        : this(GetDefaultConfigFilePath())
    {
    }

    public ConfigManager(string configFilePath)
    {
        _configFilePath = configFilePath;
        CurrentConfig = new AppConfig();
    }

    public void Load()
    {
        try
        {
            string? sourcePath = ResolveLoadPath();
            if (sourcePath == null)
            {
                CurrentConfig = Normalize(new AppConfig());
                Save();
                return;
            }

            string json = File.ReadAllText(sourcePath);
            CurrentConfig = Normalize(JsonSerializer.Deserialize<AppConfig>(json));

            if (!string.Equals(sourcePath, _configFilePath, StringComparison.OrdinalIgnoreCase))
            {
                Save();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading config: {ex.Message}");
            CurrentConfig = Normalize(new AppConfig());
        }
    }

    public void Save()
    {
        try
        {
            CurrentConfig = Normalize(CurrentConfig);
            Directory.CreateDirectory(Path.GetDirectoryName(_configFilePath) ?? ".");

            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(CurrentConfig, options);
            File.WriteAllText(_configFilePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving config: {ex.Message}");
        }
    }

    public static AppConfig Normalize(AppConfig? config)
    {
        var defaults = new AppConfig();
        config ??= defaults;

        config.WslDistroName = string.IsNullOrWhiteSpace(config.WslDistroName)
            ? defaults.WslDistroName
            : config.WslDistroName.Trim();

        config.DockerDesktopPath = string.IsNullOrWhiteSpace(config.DockerDesktopPath)
            ? defaults.DockerDesktopPath
            : config.DockerDesktopPath.Trim();

        config.Logging ??= new LoggingConfig();
        config.Logging.MaxLines = Math.Clamp(config.Logging.MaxLines, MinimumLogLines, MaximumLogLines);

        config.Dashboard ??= new DashboardConfig();
        config.Dashboard.PollIntervalSeconds = Math.Clamp(
            config.Dashboard.PollIntervalSeconds,
            MinimumPollIntervalSeconds,
            MaximumPollIntervalSeconds);

        config.Hardware ??= new HardwareConfig();
        config.Hardware.GpuDeviceNamePattern = string.IsNullOrWhiteSpace(config.Hardware.GpuDeviceNamePattern)
            ? defaults.Hardware!.GpuDeviceNamePattern
            : config.Hardware.GpuDeviceNamePattern.Trim();
        config.Hardware.GpuInstanceId = string.IsNullOrWhiteSpace(config.Hardware.GpuInstanceId)
            ? null
            : config.Hardware.GpuInstanceId.Trim();

        config.Toggles ??= new TogglesConfig();
        config.Tray ??= new TrayConfig();

        return config;
    }

    private string? ResolveLoadPath()
    {
        if (File.Exists(_configFilePath))
        {
            return _configFilePath;
        }

        string legacyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pokeswitch-config.json");
        return File.Exists(legacyPath) ? legacyPath : null;
    }

    private static string GetDefaultConfigFilePath()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PokeSwitch");

        return Path.Combine(directory, "pokeswitch-config.json");
    }

    public static string GetLogDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PokeSwitch",
            "logs");
    }
}
