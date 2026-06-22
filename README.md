# PokeSwitch

PokeSwitch is a lightweight, clean WPF utility designed to seamlessly toggle Docker Desktop and its WSL2 backend on and off. This tool is highly optimized for developers whose WSL environment is primarily or exclusively used for Docker, allowing them to quickly free up gigabytes of RAM (reclaiming the WSL `vmmem` memory) and save laptop battery when Docker is not active.

---

## Features

- **Dashboard**: Real-time status indicators showing:
  - **Docker Status**: Running or Stopped.
  - **WSL Status**: Active or Inactive.
  - **Container Count**: Active / Total containers.
  - **VMMem RAM Usage**: Live RAM consumed by the WSL `vmmem` subsystem.
- **Start WSL Keep-Alive**: Starts the WSL distro in a headless server state (`sleep infinity`) without booting Docker Desktop.
- **Boot Docker Engine**: Full spin-up of Docker Desktop, waiting for the daemon to respond, and automatically waking up all existing containers.
- **Stop Docker Engine**: Gracefully stops all active containers and shuts down Docker Desktop processes.
- **Nuclear Shutdown**: Gracefully stops containers, shuts down Docker, and purges the WSL2 subsystem (`wsl --shutdown`) to immediately reclaim RAM.
- **NVIDIA GPU Toggle**: Enables or disables the NVIDIA RTX 3050 display device from the GUI, refusing to act if no matching device or multiple matching devices are found.
- **Individual Toggle States**: Each control card shows its own running, ready, failed, or not-found result instead of relying only on global status.
- **Tray Quick Actions**: Optional system tray menu for WSL, Docker, GPU, and Nuclear Shutdown actions.
- **Diagnostics & Logs**: Live diagnostics panel plus log export/copy actions and optional rolling log files.

---

## Folder Architecture

```text
pokeswitch/
├── PokeSwitch.sln             # C# Visual Studio Solution File
├── pokeswitch-config.json     # Configuration file (WSL distro name, paths, etc.)
├── README.md                  # Project Documentation
└── PokeSwitch/                # Main Application Project
    ├── app.manifest           # Application manifest
    ├── App.xaml / App.xaml.cs # WPF Application entry point
    ├── AssemblyInfo.cs        # Assembly metadata
    ├── MainWindow.xaml        # MainWindow layout markup
    ├── MainWindow.xaml.cs     # MainWindow UI and interaction logic
    ├── Models/
    │   └── AppConfig.cs       # Application configuration models
    ├── Resources/
    │   └── PokeSwitch.ico     # Application Icon
    └── Services/
        ├── ConfigManager.cs   # Config file loader/saver
        └── DockerManager.cs   # WSL and Docker process management library
```

---

## Configuration (`pokeswitch-config.json`)

The application is configured using a `pokeswitch-config.json` file located in the same directory as the executable. If the file does not exist, a default configuration will be created automatically upon the first launch. Here is the default schema:

```json
{
  "wslDistroName": "Ubuntu",
  "dockerDesktopPath": "C:\\Program Files\\Docker\\Docker\\Docker Desktop.exe",
  "logging": {
    "enabled": true,
    "fileEnabled": false,
    "maxLines": 500
  },
  "dashboard": {
    "pollIntervalSeconds": 3
  },
  "hardware": {
    "gpuDeviceNamePattern": "*NVIDIA*RTX 3050*",
    "gpuInstanceId": null
  },
  "toggles": {
    "confirmGpuDisable": true,
    "confirmDockerStop": true,
    "confirmNuclearShutdown": true
  },
  "tray": {
    "enabled": true,
    "minimizeToTray": true
  },
  "autoStartWslOnLaunch": false,
  "autoStartDockerOnLaunch": false
}
```

### Configuration Keys:
- `wslDistroName`: The name of the WSL2 distro to run/manage.
- `dockerDesktopPath`: The local path to your `Docker Desktop.exe` installation.
- `logging.enabled`: Set to `true` to view status outputs in the terminal control inside the app.
- `logging.fileEnabled`: Set to `true` to write rolling log files under `%LocalAppData%\PokeSwitch\logs`.
- `dashboard.pollIntervalSeconds`: The interval (in seconds) at which the dashboard refreshes the status.
- `hardware.gpuDeviceNamePattern`: Fallback display-device friendly-name match pattern for the GPU toggle.
- `hardware.gpuInstanceId`: Saved display-device instance ID selected from Settings.
- `toggles.*`: Enables confirmation prompts for high-impact actions.
- `tray.enabled`: Enables the system tray menu and completion notifications.
- `tray.minimizeToTray`: Minimizes/closes the window to the tray instead of exiting.
- `autoStartWslOnLaunch`: Automatically start WSL keep-alive when PokeSwitch opens.
- `autoStartDockerOnLaunch`: Automatically boot Docker Desktop and containers when PokeSwitch opens.

---

## Getting Started & Build Instructions

### Prerequisites
- Windows OS (with WPF support)
- .NET 10 SDK installed
- Docker Desktop installed
- WSL2 enabled

### How to Build & Run
From the root directory:
```bash
# Restore dependencies and build the solution
dotnet build

# Run the PokeSwitch application
dotnet run --project PokeSwitch/PokeSwitch.csproj
```

### How to Publish as a Single Executable
To compile and package the application into a single `.exe` file, run the following command from the root directory:

```bash
# Framework-dependent Single Executable (requires .NET 10 runtime installed on target system)
dotnet publish PokeSwitch/PokeSwitch.csproj -c Release -r win-x64

# Self-contained Single Executable (includes .NET 10 runtime, making it run anywhere without installing .NET)
dotnet publish PokeSwitch/PokeSwitch.csproj -c Release -r win-x64 --self-contained true
```

The output executable `PokeSwitch.exe` will be generated in:
`PokeSwitch/bin/Release/net10.0-windows10.0.17763.0/win-x64/publish/`
