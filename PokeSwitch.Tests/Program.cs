using System.Diagnostics;
using System.Text.Json;
using PokeSwitch.Models;
using PokeSwitch.Services;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Config normalization fills null sections and clamps values", TestConfigNormalization),
    ("Config manager creates a missing config file safely", TestMissingConfigCreatesFile),
    ("Process runner captures output and exit code", TestProcessRunnerCapturesOutput),
    ("Process runner times out and kills long commands", TestProcessRunnerTimeout),
    ("GPU manager parses enabled status payload", TestGpuManagerParsesStatusPayload),
    ("GPU manager parses toggle payload", TestGpuManagerParsesTogglePayload),
    ("GPU manager parses display device list", TestGpuManagerParsesDeviceList),
    ("GPU manager reports ambiguous configured device", TestGpuManagerReportsAmbiguousDevice),
    ("Toggle card tracks busy and failed state", TestToggleCardState)
};

var failures = new List<string>();

foreach ((string name, Func<Task> run) in tests)
{
    try
    {
        await run();
        Console.WriteLine($"PASS: {name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{name}: {ex.Message}");
        Console.WriteLine($"FAIL: {name}");
        Console.WriteLine(ex);
    }
}

if (failures.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("Failures:");
    foreach (string failure in failures)
    {
        Console.WriteLine($"- {failure}");
    }

    return 1;
}

return 0;

static Task TestConfigNormalization()
{
    var config = ConfigManager.Normalize(new AppConfig
    {
        WslDistroName = "  ",
        DockerDesktopPath = "  ",
        Logging = new LoggingConfig { MaxLines = -10 },
        Dashboard = new DashboardConfig { PollIntervalSeconds = 0 }
    });

    AssertEqual("Ubuntu", config.WslDistroName, nameof(config.WslDistroName));
    AssertEqual(@"C:\Program Files\Docker\Docker\Docker Desktop.exe", config.DockerDesktopPath, nameof(config.DockerDesktopPath));
    AssertNotNull(config.Logging, nameof(config.Logging));
    AssertNotNull(config.Dashboard, nameof(config.Dashboard));
    AssertEqual(50, config.Logging!.MaxLines, nameof(config.Logging.MaxLines));
    AssertEqual(1, config.Dashboard!.PollIntervalSeconds, nameof(config.Dashboard.PollIntervalSeconds));

    config = ConfigManager.Normalize(new AppConfig
    {
        Logging = null,
        Dashboard = null
    });

    AssertNotNull(config.Logging, nameof(config.Logging));
    AssertNotNull(config.Dashboard, nameof(config.Dashboard));
    AssertNotNull(config.Hardware, nameof(config.Hardware));
    AssertNotNull(config.Toggles, nameof(config.Toggles));
    AssertNotNull(config.Tray, nameof(config.Tray));
    return Task.CompletedTask;
}

static Task TestMissingConfigCreatesFile()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "PokeSwitchTests", Guid.NewGuid().ToString("N"));
    string configPath = Path.Combine(tempDirectory, "pokeswitch-config.json");

    var manager = new ConfigManager(configPath);
    manager.Load();

    AssertTrue(File.Exists(configPath), "Expected missing config file to be created.");

    string json = File.ReadAllText(configPath);
    var config = JsonSerializer.Deserialize<AppConfig>(json);
    AssertNotNull(config, "Saved config JSON should deserialize.");

    Directory.Delete(tempDirectory, recursive: true);
    return Task.CompletedTask;
}

static async Task TestProcessRunnerCapturesOutput()
{
    var runner = new ProcessRunner();
    var psi = new ProcessStartInfo { FileName = "cmd.exe" };
    psi.ArgumentList.Add("/c");
    psi.ArgumentList.Add("echo hello");

    ProcessResult result = await runner.RunAsync(psi, TimeSpan.FromSeconds(5));

    AssertEqual(0, result.ExitCode, nameof(result.ExitCode));
    AssertFalse(result.TimedOut, "Process should not time out.");
    AssertTrue(result.StandardOutput.Contains("hello", StringComparison.OrdinalIgnoreCase), "Expected stdout to contain echo output.");
}

static async Task TestProcessRunnerTimeout()
{
    var runner = new ProcessRunner();
    var psi = new ProcessStartInfo { FileName = "cmd.exe" };
    psi.ArgumentList.Add("/c");
    psi.ArgumentList.Add("ping -n 6 127.0.0.1 > nul");

    ProcessResult result = await runner.RunAsync(psi, TimeSpan.FromMilliseconds(100));

    AssertTrue(result.TimedOut, "Process should report timeout.");
    AssertEqual(-1, result.ExitCode, nameof(result.ExitCode));
}

static async Task TestGpuManagerParsesStatusPayload()
{
    var runner = new FakeProcessRunner
    {
        NextResult = new ProcessResult(
            0,
            """{"found":true,"multiple":false,"friendlyName":"NVIDIA GeForce RTX 3050 Laptop GPU","instanceId":"PCI\\VEN_TEST","status":"OK","isEnabled":true,"message":"GPU status read successfully."}""",
            "",
            TimedOut: false)
    };

    var manager = new GpuManager(runner);
    GpuStatus status = await manager.GetStatusAsync();

    AssertTrue(status.Found, "GPU should be found.");
    AssertFalse(status.Multiple, "GPU should not report multiple devices.");
    AssertTrue(status.IsEnabled, "GPU should be enabled.");
    AssertEqual("OK", status.Status, nameof(status.Status));
}

static async Task TestGpuManagerParsesTogglePayload()
{
    var runner = new FakeProcessRunner
    {
        NextResult = new ProcessResult(
            0,
            """{"success":true,"action":"Disable","message":"NVIDIA GPU device is now disabled.","status":{"found":true,"multiple":false,"friendlyName":"NVIDIA GeForce RTX 3050 Laptop GPU","instanceId":"PCI\\VEN_TEST","status":"Error","isEnabled":false,"message":"GPU status read successfully."}}""",
            "",
            TimedOut: false)
    };

    var manager = new GpuManager(runner);
    GpuToggleResult result = await manager.ToggleAsync();

    AssertTrue(result.Success, "GPU toggle should succeed.");
    AssertEqual("Disable", result.Action, nameof(result.Action));
    AssertFalse(result.Status.IsEnabled, "GPU should be disabled after toggle.");
}

static async Task TestGpuManagerParsesDeviceList()
{
    var runner = new FakeProcessRunner
    {
        NextResult = new ProcessResult(
            0,
            """[{"friendlyName":"Intel UHD Graphics","instanceId":"PCI\\VEN_INTEL","status":"OK"},{"friendlyName":"NVIDIA GeForce RTX 3050 Laptop GPU","instanceId":"PCI\\VEN_NVIDIA","status":"Error"}]""",
            "",
            TimedOut: false)
    };

    var manager = new GpuManager(runner);
    IReadOnlyList<HardwareDeviceDescriptor> devices = await manager.ListDisplayDevicesAsync();

    AssertEqual(2, devices.Count, nameof(devices.Count));
    AssertEqual("PCI\\VEN_NVIDIA", devices[1].InstanceId, "Second device InstanceId");
}

static async Task TestGpuManagerReportsAmbiguousDevice()
{
    var runner = new FakeProcessRunner
    {
        NextResult = new ProcessResult(
            0,
            """{"found":true,"multiple":true,"friendlyName":null,"instanceId":null,"status":null,"isEnabled":false,"message":"Multiple matching display devices found. Select a GPU in Settings."}""",
            "",
            TimedOut: false)
    };

    var manager = new GpuManager(runner);
    GpuStatus status = await manager.GetStatusAsync();

    AssertTrue(status.Found, "Ambiguous lookup should still report that matching devices exist.");
    AssertTrue(status.Multiple, "Ambiguous lookup should set Multiple.");
    AssertFalse(status.IsEnabled, "Ambiguous lookup should not be actionable.");
}

static Task TestToggleCardState()
{
    var state = new ToggleCardState("Play24", "Start", "Ready to start", "#107C10");

    state.MarkRunning();
    AssertFalse(state.IsEnabled, "Running toggle should be disabled.");
    AssertTrue(state.IsRunning, "Running toggle should report IsRunning.");
    AssertEqual("Running", state.Status, nameof(state.Status));

    state.MarkFailed("Something failed.");
    AssertTrue(state.IsEnabled, "Failed toggle should be re-enabled.");
    AssertFalse(state.IsRunning, "Failed toggle should stop running.");
    AssertEqual("Failed", state.Status, nameof(state.Status));
    AssertEqual("Something failed.", state.LastResult, nameof(state.LastResult));

    return Task.CompletedTask;
}

static void AssertEqual<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{name}: expected '{expected}', got '{actual}'.");
    }
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertFalse(bool condition, string message)
{
    AssertTrue(!condition, message);
}

static void AssertNotNull<T>(T? value, string name)
{
    if (value is null)
    {
        throw new InvalidOperationException($"{name} should not be null.");
    }
}

sealed class FakeProcessRunner : IProcessRunner
{
    public ProcessResult NextResult { get; init; } = new(0, "", "", TimedOut: false);

    public Task<ProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(NextResult);
    }
}
