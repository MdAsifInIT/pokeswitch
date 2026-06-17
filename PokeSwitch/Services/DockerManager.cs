using System.Diagnostics;
using System.IO;
using System.Text;
using PokeSwitch.Models;

namespace PokeSwitch.Services;

public interface IDockerManager
{
    Task StartWslKeepAliveAsync(Action<string> onLog, CancellationToken cancellationToken = default);
    Task StopWslAsync(Action<string> onLog, CancellationToken cancellationToken = default);
    Task BootDockerEngineAsync(Action<string> onLog, CancellationToken cancellationToken = default);
    Task StopDockerOnlyAsync(Action<string> onLog, CancellationToken cancellationToken = default);
    Task NuclearShutdownAsync(Action<string> onLog, CancellationToken cancellationToken = default);
    bool IsDockerDesktopRunning();
    bool IsWslRunning();
    bool IsWslKeepAliveRunning();
    Task<(int running, int total)> GetRunningContainerCountAsync(CancellationToken cancellationToken = default);
    int GetVmMemUsageMB();
}

public class DockerManager : IDockerManager
{
    private const int ContainerBatchSize = 50;
    private static readonly TimeSpan ShortCommandTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ContainerCommandTimeout = TimeSpan.FromMinutes(2);

    private readonly AppConfig _config;
    private readonly IProcessRunner _processRunner;

    public DockerManager(AppConfig config, IProcessRunner? processRunner = null)
    {
        _config = ConfigManager.Normalize(config);
        _processRunner = processRunner ?? new ProcessRunner();
    }

    public Task StartWslKeepAliveAsync(Action<string> onLog, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string distro = GetEffectiveDistroName();
        onLog($"Starting WSL Keep-Alive for distro: {distro}...");

        var psi = CreateStartInfo("wsl.exe");
        psi.WindowStyle = ProcessWindowStyle.Hidden;
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(distro);
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add("sleep");
        psi.ArgumentList.Add("infinity");

        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start WSL keep-alive.");

        onLog($"WSL started with PID: {process.Id}");
        return Task.CompletedTask;
    }

    public async Task StopWslAsync(Action<string> onLog, CancellationToken cancellationToken = default)
    {
        string distro = GetEffectiveDistroName();
        onLog($"Stopping WSL distro: {distro}...");

        var psi = CreateStartInfo("wsl");
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(distro);
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add("pkill");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("sleep infinity");

        ProcessResult result = await _processRunner.RunAsync(psi, CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            onLog($"Timed out stopping WSL keep-alive for {distro}.");
            return;
        }

        if (result.ExitCode != 0 && !string.IsNullOrWhiteSpace(result.StandardError))
        {
            onLog($"WSL stop returned {result.ExitCode}: {result.StandardError.Trim()}");
        }

        onLog($"WSL {distro} terminated.");
    }

    public async Task BootDockerEngineAsync(Action<string> onLog, CancellationToken cancellationToken = default)
    {
        onLog("Starting Docker Desktop...");

        if (!IsDockerDesktopRunning())
        {
            StartDockerDesktop();
        }

        onLog("Waiting for Docker Daemon to respond...");
        const int maxRetries = 30;
        for (int retry = 0; retry < maxRetries; retry++)
        {
            if (await IsDockerDaemonReadyAsync(cancellationToken).ConfigureAwait(false))
            {
                onLog("Docker daemon is responding.");
                break;
            }

            if (retry == maxRetries - 1)
            {
                onLog("Error: Timed out waiting for Docker Daemon. The application might be stuck or updating.");
                throw new TimeoutException("Docker Daemon response timeout.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        onLog("Waking up all containers...");
        string[] containerIds = await GetContainerIdsAsync(allContainers: true, cancellationToken).ConfigureAwait(false);
        if (containerIds.Length == 0)
        {
            onLog("No containers to wake up.");
        }
        else
        {
            onLog($"Found {containerIds.Length} containers. Starting them...");
            await RunDockerContainerCommandInBatchesAsync("start", containerIds, onLog, cancellationToken).ConfigureAwait(false);
        }

        onLog("Boot complete. Docker engine and containers are online.");
    }

    public async Task StopDockerOnlyAsync(Action<string> onLog, CancellationToken cancellationToken = default)
    {
        onLog("Stopping active containers gracefully...");

        try
        {
            string[] containerIds = await GetContainerIdsAsync(allContainers: false, cancellationToken).ConfigureAwait(false);
            if (containerIds.Length > 0)
            {
                onLog($"Stopping {containerIds.Length} containers...");
                await RunDockerContainerCommandInBatchesAsync("stop", containerIds, onLog, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            onLog($"Docker CLI unavailable, skipping container stop: {ex.Message}");
        }

        onLog("Shutting down Docker Desktop processes...");
        await StopProcessByNameAsync("Docker Desktop", onLog, cancellationToken).ConfigureAwait(false);
        await StopProcessByNameAsync("com.docker.backend", onLog, cancellationToken).ConfigureAwait(false);
        onLog("Docker Engine stopped.");
    }

    public async Task NuclearShutdownAsync(Action<string> onLog, CancellationToken cancellationToken = default)
    {
        await StopDockerOnlyAsync(onLog, cancellationToken).ConfigureAwait(false);

        onLog("Purging WSL2 VMMem subsystem from RAM (wsl --shutdown)...");
        var psi = CreateStartInfo("wsl");
        psi.ArgumentList.Add("--shutdown");

        ProcessResult result = await _processRunner.RunAsync(psi, CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            onLog("Timed out waiting for wsl --shutdown.");
            return;
        }

        if (result.ExitCode != 0 && !string.IsNullOrWhiteSpace(result.StandardError))
        {
            onLog($"wsl --shutdown returned {result.ExitCode}: {result.StandardError.Trim()}");
        }

        onLog("Nuclear Shutdown complete. RAM and Battery saved!");
    }

    public bool IsDockerDesktopRunning()
    {
        try
        {
            return Process.GetProcessesByName("Docker Desktop").Any();
        }
        catch
        {
            return false;
        }
    }

    public bool IsWslRunning()
    {
        try
        {
            var psi = CreateStartInfo("wsl");
            psi.StandardOutputEncoding = Encoding.Unicode;
            psi.ArgumentList.Add("-l");
            psi.ArgumentList.Add("--running");

            ProcessResult result = RunProcessSync(psi, ShortCommandTimeout);
            string distro = GetEffectiveDistroName();
            return result.ExitCode == 0
                && (result.StandardOutput.Contains(distro, StringComparison.OrdinalIgnoreCase)
                    || result.StandardOutput.Contains("docker-desktop", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    public bool IsWslKeepAliveRunning()
    {
        try
        {
            string distro = GetEffectiveDistroName();
            var psi = CreateStartInfo("wsl");
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(distro);
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("pgrep");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("sleep infinity");

            ProcessResult result = RunProcessSync(psi, ShortCommandTimeout);
            return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput);
        }
        catch
        {
            return false;
        }
    }

    public async Task<(int running, int total)> GetRunningContainerCountAsync(CancellationToken cancellationToken = default)
    {
        if (!IsDockerDesktopRunning())
        {
            return (0, 0);
        }

        try
        {
            Task<string[]> runningTask = GetContainerIdsAsync(allContainers: false, cancellationToken);
            Task<string[]> totalTask = GetContainerIdsAsync(allContainers: true, cancellationToken);

            await Task.WhenAll(runningTask, totalTask).ConfigureAwait(false);
            return (runningTask.Result.Length, totalTask.Result.Length);
        }
        catch
        {
            return (0, 0);
        }
    }

    public int GetVmMemUsageMB()
    {
        try
        {
            using Process? vmmem = Process.GetProcessesByName("vmmem").FirstOrDefault()
                ?? Process.GetProcessesByName("vmmemWSL").FirstOrDefault();

            return vmmem == null ? 0 : (int)(vmmem.WorkingSet64 / (1024 * 1024));
        }
        catch
        {
            return 0;
        }
    }

    public string GetEffectiveDistroName()
    {
        string configured = _config.WslDistroName;
        string[] installed = GetInstalledDistros();

        if (installed.Contains(configured, StringComparer.OrdinalIgnoreCase))
        {
            return configured;
        }

        string? match = installed.FirstOrDefault(d => d.Contains(configured, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            return match;
        }

        string? fallback = installed.FirstOrDefault(d =>
            !d.Equals("docker-desktop", StringComparison.OrdinalIgnoreCase)
            && !d.Equals("docker-desktop-data", StringComparison.OrdinalIgnoreCase));

        return fallback ?? configured;
    }

    public string[] GetInstalledDistros()
    {
        try
        {
            var psi = CreateStartInfo("wsl");
            psi.StandardOutputEncoding = Encoding.Unicode;
            psi.ArgumentList.Add("-l");
            psi.ArgumentList.Add("-q");

            ProcessResult result = RunProcessSync(psi, ShortCommandTimeout);
            if (result.ExitCode != 0 || result.TimedOut)
            {
                return Array.Empty<string>();
            }

            return SplitLines(result.StandardOutput);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private void StartDockerDesktop()
    {
        if (!File.Exists(_config.DockerDesktopPath))
        {
            throw new FileNotFoundException("Docker Desktop executable was not found.", _config.DockerDesktopPath);
        }

        var psi = new ProcessStartInfo
        {
            FileName = _config.DockerDesktopPath,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Minimized
        };

        Process.Start(psi);
    }

    private async Task<bool> IsDockerDaemonReadyAsync(CancellationToken cancellationToken)
    {
        var psi = CreateStartInfo("docker");
        psi.ArgumentList.Add("info");

        ProcessResult result = await _processRunner.RunAsync(psi, TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        return !result.TimedOut && result.ExitCode == 0;
    }

    private async Task<string[]> GetContainerIdsAsync(bool allContainers, CancellationToken cancellationToken)
    {
        var psi = CreateStartInfo("docker");
        psi.ArgumentList.Add("ps");

        if (allContainers)
        {
            psi.ArgumentList.Add("-a");
        }

        psi.ArgumentList.Add("-q");

        ProcessResult result = await _processRunner.RunAsync(psi, ShortCommandTimeout, cancellationToken).ConfigureAwait(false);
        if (result.TimedOut || result.ExitCode != 0)
        {
            return Array.Empty<string>();
        }

        return SplitLines(result.StandardOutput);
    }

    private async Task RunDockerContainerCommandInBatchesAsync(
        string command,
        IReadOnlyCollection<string> containerIds,
        Action<string> onLog,
        CancellationToken cancellationToken)
    {
        foreach (string[] batch in containerIds.Chunk(ContainerBatchSize))
        {
            var psi = CreateStartInfo("docker");
            psi.ArgumentList.Add(command);

            foreach (string containerId in batch)
            {
                psi.ArgumentList.Add(containerId);
            }

            ProcessResult result = await _processRunner.RunAsync(psi, ContainerCommandTimeout, cancellationToken).ConfigureAwait(false);
            if (result.TimedOut)
            {
                onLog($"docker {command} timed out for a batch of {batch.Length} containers.");
            }
            else if (result.ExitCode != 0)
            {
                string error = string.IsNullOrWhiteSpace(result.StandardError)
                    ? result.StandardOutput
                    : result.StandardError;
                onLog($"docker {command} returned {result.ExitCode}: {error.Trim()}");
            }
        }
    }

    private static async Task StopProcessByNameAsync(string processName, Action<string> onLog, CancellationToken cancellationToken)
    {
        foreach (Process process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    if (process.HasExited)
                    {
                        continue;
                    }

                    onLog($"Stopping process {processName} (PID: {process.Id})...");
                    bool closeRequested = TryCloseMainWindow(process);

                    if (closeRequested && await WaitForExitAsync(process, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }

                    onLog($"Process {processName} did not exit gracefully; killing process tree.");
                    process.Kill(entireProcessTree: true);
                    if (!await WaitForExitAsync(process, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false))
                    {
                        onLog($"Timed out waiting for {processName} to exit after kill.");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    onLog($"Failed to stop {processName}: {ex.Message}");
                }
            }
        }
    }

    private static bool TryCloseMainWindow(Process process)
    {
        try
        {
            return process.CloseMainWindow();
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task waitTask = process.WaitForExitAsync(cancellationToken);
        Task timeoutTask = Task.Delay(timeout, cancellationToken);

        Task completedTask = await Task.WhenAny(waitTask, timeoutTask).ConfigureAwait(false);
        if (completedTask != waitTask)
        {
            return false;
        }

        await waitTask.ConfigureAwait(false);
        return true;
    }

    private ProcessResult RunProcessSync(ProcessStartInfo psi, TimeSpan timeout)
    {
        return _processRunner.RunAsync(psi, timeout).GetAwaiter().GetResult();
    }

    private static ProcessStartInfo CreateStartInfo(string fileName)
    {
        return new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    private static string[] SplitLines(string value)
    {
        return value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
