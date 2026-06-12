# PokeSwitch

PokeSwitch is a utility designed to seamlessly toggle Docker Desktop and its WSL2 backend on and off. This is highly optimized for environments where WSL is exclusively used for Docker, allowing you to quickly free up RAM and save battery when Docker is not in use.

## Features

- **Desktop Mode**: Gracefully stops all active Docker containers, shuts down Docker Desktop, and safely purges the WSL2 `vmmem` subsystem from RAM to conserve resources.
- **Server Mode**: Spins up Docker Desktop in the background, waits for the daemon to respond, and automatically wakes up all existing containers.
- Simple, one-click or script-based toggling for rapid environment switching.

## Getting Started

### Prerequisites
- Docker Desktop installed on Windows.
- WSL2 enabled and acting as the Docker backend.

### Usage
- Use the provided `toggle-docker.ps1` PowerShell script to switch between Desktop (Docker Off) and Server (Docker On) modes.
- Alternatively, run the PokeSwitch Windows Application for a GUI-based experience.

## Contributing

Contributions, issues, and feature requests are welcome! Feel free to check the issues page.
