using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using PokeSwitch.Models;

namespace PokeSwitch.Services
{
    public class DockerManager
    {
        private readonly AppConfig _config;

        public DockerManager(AppConfig config)
        {
            _config = config;
        }

        public async Task StartWslKeepAliveAsync(Action<string> onLog)
        {
            await Task.Run(() =>
            {
                string distro = GetEffectiveDistroName();
                onLog($"Starting WSL Keep-Alive for distro: {distro}...");
                var psi = new ProcessStartInfo
                {
                    FileName = "wsl.exe",
                    Arguments = $"-d {distro} -e sleep infinity",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                var process = Process.Start(psi);
                if (process != null)
                {
                    onLog($"WSL started with PID: {process.Id}");
                }
            });
        }

        public async Task StopWslAsync(Action<string> onLog)
        {
            await Task.Run(() =>
            {
                string distro = GetEffectiveDistroName();
                onLog($"Stopping WSL distro: {distro}...");
                var psi = new ProcessStartInfo
                {
                    FileName = "wsl",
                    Arguments = $"-d {distro} -e pkill -f \"sleep infinity\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(psi);
                process?.WaitForExit();
                onLog($"WSL {distro} terminated.");
            });
        }

        public async Task BootDockerEngineAsync(Action<string> onLog)
        {
            await Task.Run(async () =>
            {
                onLog("Starting Docker Desktop...");
                
                if (!IsDockerDesktopRunning())
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = _config.DockerDesktopPath,
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Minimized
                    };
                    Process.Start(psi);
                }

                onLog("Waiting for Docker Daemon to respond...");
                int maxRetries = 30; // 60 seconds timeout
                int retries = 0;
                while (retries < maxRetries)
                {
                    var daemonPsi = new ProcessStartInfo
                    {
                        FileName = "docker",
                        Arguments = "info",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    using var process = Process.Start(daemonPsi);
                    process?.WaitForExit(2000);
                    if (process?.ExitCode == 0)
                    {
                        onLog("Docker daemon is responding.");
                        break;
                    }
                    
                    retries++;
                    if (retries >= maxRetries)
                    {
                        onLog("Error: Timed out waiting for Docker Daemon. The application might be stuck or updating.");
                        throw new TimeoutException("Docker Daemon response timeout.");
                    }
                    await Task.Delay(2000);
                }

                onLog("Waking up all containers...");
                var psPsi = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = "ps -a -q",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };

                using var psProcess = Process.Start(psPsi);
                if (psProcess != null)
                {
                    string allContainers = await psProcess.StandardOutput.ReadToEndAsync();
                    psProcess.WaitForExit();

                    var containerIds = allContainers.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (containerIds.Length > 0)
                    {
                        onLog($"Found {containerIds.Length} stopped containers. Starting them...");
                        var startPsi = new ProcessStartInfo
                        {
                            FileName = "docker",
                            Arguments = $"start {string.Join(" ", containerIds)}",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var startProcess = Process.Start(startPsi);
                        startProcess?.WaitForExit();
                    }
                    else
                    {
                        onLog("No containers to wake up.");
                    }
                }

                onLog("✅ Boot complete. Docker engine and containers are online.");
            });
        }

        public async Task StopDockerOnlyAsync(Action<string> onLog)
        {
            await Task.Run(async () =>
            {
                onLog("Stopping active containers gracefully...");
                var psPsi = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = "ps -q",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };

                try
                {
                    using var psProcess = Process.Start(psPsi);
                    if (psProcess != null)
                    {
                        string activeContainers = await psProcess.StandardOutput.ReadToEndAsync();
                        psProcess.WaitForExit();

                        var containerIds = activeContainers.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        if (containerIds.Length > 0)
                        {
                            onLog($"Stopping {containerIds.Length} containers...");
                            var stopPsi = new ProcessStartInfo
                            {
                                FileName = "docker",
                                Arguments = $"stop {string.Join(" ", containerIds)}",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            };
                            using var stopProcess = Process.Start(stopPsi);
                            stopProcess?.WaitForExit();
                        }
                    }
                }
                catch { onLog("Docker CLI unavailable, skipping container stop."); }

                onLog("Shutting down Docker Desktop processes...");
                KillProcessByName("Docker Desktop", onLog);
                KillProcessByName("com.docker.backend", onLog);
                onLog("Docker Engine stopped.");
            });
        }

        public async Task NuclearShutdownAsync(Action<string> onLog)
        {
            await Task.Run(async () =>
            {
                await StopDockerOnlyAsync(onLog);

                onLog("Purging WSL2 VMMem subsystem from RAM (wsl --shutdown)...");
                var wslPsi = new ProcessStartInfo
                {
                    FileName = "wsl",
                    Arguments = "--shutdown",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var wslProcess = Process.Start(wslPsi);
                wslProcess?.WaitForExit();

                onLog("✅ Nuclear Shutdown complete. RAM and Battery saved!");
            });
        }

        private void KillProcessByName(string processName, Action<string> onLog)
        {
            var processes = Process.GetProcessesByName(processName);
            foreach (var process in processes)
            {
                try
                {
                    onLog($"Killing process {processName} (PID: {process.Id})...");
                    process.Kill();
                    process.WaitForExit();
                }
                catch (Exception ex)
                {
                    onLog($"Failed to kill {processName}: {ex.Message}");
                }
            }
        }

        public bool IsDockerDesktopRunning()
        {
            return Process.GetProcessesByName("Docker Desktop").Any();
        }

        public bool IsWslRunning()
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wsl",
                Arguments = "-l --running",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = System.Text.Encoding.Unicode
            };

            try
            {
                using var process = Process.Start(psi);
                if (process == null) return false;
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(2000);
                string distro = GetEffectiveDistroName();
                return output.Contains(distro) || output.Contains("docker-desktop");
            }
            catch
            {
                return false;
            }
        }

        public bool IsWslKeepAliveRunning()
        {
            string distro = GetEffectiveDistroName();
            var psi = new ProcessStartInfo
            {
                FileName = "wsl",
                Arguments = $"-d {distro} -e pgrep -f \"sleep infinity\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };

            try
            {
                using var process = Process.Start(psi);
                if (process == null) return false;
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(2000);
                return !string.IsNullOrWhiteSpace(output);
            }
            catch
            {
                return false;
            }
        }

        public async Task<(int running, int total)> GetRunningContainerCountAsync()
        {
            if (!IsDockerDesktopRunning()) return (0, 0);

            try
            {
                var runTask = Task.Run(async () =>
                {
                    var psiRunning = new ProcessStartInfo { FileName = "docker", Arguments = "ps -q", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                    using var pRunning = Process.Start(psiRunning);
                    if (pRunning == null) return 0;
                    string outRunning = await pRunning.StandardOutput.ReadToEndAsync();
                    pRunning.WaitForExit(5000);
                    return outRunning.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
                });

                var totalTask = Task.Run(async () =>
                {
                    var psiTotal = new ProcessStartInfo { FileName = "docker", Arguments = "ps -a -q", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                    using var pTotal = Process.Start(psiTotal);
                    if (pTotal == null) return 0;
                    string outTotal = await pTotal.StandardOutput.ReadToEndAsync();
                    pTotal.WaitForExit(5000);
                    return outTotal.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
                });

                var allTask = Task.WhenAll(runTask, totalTask);
                if (await Task.WhenAny(allTask, Task.Delay(10000)) == allTask)
                {
                    return (runTask.Result, totalTask.Result);
                }
                return (0, 0);
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
                var vmmem = Process.GetProcessesByName("vmmem").FirstOrDefault() ?? Process.GetProcessesByName("vmmemWSL").FirstOrDefault();
                if (vmmem != null)
                {
                    // WorkingSet64 is in bytes, convert to MB
                    return (int)(vmmem.WorkingSet64 / (1024 * 1024));
                }
                return 0;
            }
            catch
            {
                return 0;
            }
        }

        public string GetEffectiveDistroName()
        {
            var configured = _config.WslDistroName;
            var installed = GetInstalledDistros();

            // 1. If configured matches exactly, use it
            if (installed.Contains(configured, StringComparer.OrdinalIgnoreCase))
            {
                return configured;
            }

            // 2. If configured is a substring of any installed distro, use the first matching installed distro
            var match = installed.FirstOrDefault(d => d.Contains(configured, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                return match;
            }

            // 3. Fallback to the first installed distro that is not docker-desktop/docker-desktop-data
            var fallback = installed.FirstOrDefault(d => !d.Equals("docker-desktop", StringComparison.OrdinalIgnoreCase) && !d.Equals("docker-desktop-data", StringComparison.OrdinalIgnoreCase));
            if (fallback != null)
            {
                return fallback;
            }

            // 4. Ultimate fallback to configured value
            return configured;
        }

        public string[] GetInstalledDistros()
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wsl",
                Arguments = "-l -q",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = System.Text.Encoding.Unicode
            };

            try
            {
                using var process = Process.Start(psi);
                if (process == null) return Array.Empty<string>();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                             .Select(s => s.Trim())
                             .Where(s => !string.IsNullOrEmpty(s))
                             .ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }
}
