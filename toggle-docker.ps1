<#
.SYNOPSIS
    Toggles Docker Desktop and the WSL2 backend completely On/Off to save RAM and battery.
    Optimized for environments where WSL is exclusively used for Docker.
#>

$DockerPath = "C:\Program Files\Docker\Docker\Docker Desktop.exe"
$DockerProcess = Get-Process -Name "Docker Desktop" -ErrorAction SilentlyContinue

if ($DockerProcess) {
    Write-Host "🔄 Switching to DESKTOP MODE: Stopping Server Stacks cleanly..." -ForegroundColor Yellow
    
    # 1. Gracefully stop all running containers
    $activeContainers = docker ps -q
    if ($activeContainers) {
        Write-Host "Stopping active containers..."
        docker stop $activeContainers | Out-Null
    }
    
    # 2. Close Docker Desktop App
    Write-Host "Shutting down Docker Desktop..."
    Stop-Process -Name "Docker Desktop" -Force -ErrorAction SilentlyContinue
    Stop-Process -Name "com.docker.backend" -Force -ErrorAction SilentlyContinue
    
    # 3. Nuclear WSL shutdown (Safe since Docker is your only WSL distro)
    Write-Host "Purging WSL2 VMMem subsystem from RAM..."
    wsl --shutdown
    
    Write-Host "✅ Desktop Mode Active. RAM and Battery saved!" -ForegroundColor Green

} else {
    Write-Host "🔄 Switching to SERVER MODE: Spinning up environment..." -ForegroundColor Cyan
    
    # 1. Start Docker Desktop Hidden/Minimized
    Start-Process -FilePath $DockerPath -WindowStyle Minimized
    
    # 2. Wait for the Docker daemon to become responsive
    Write-Host "Waiting for Docker Daemon to respond..."
    while ($true) {
        docker info > $null 2>&1
        if ($LASTEXITCODE -eq 0) { break }
        Start-Sleep -Seconds 2
    }
    
    # 3. Start ALL existing containers
    Write-Host "Waking up all containers..."
    $allContainers = docker ps -a -q
    if ($allContainers) {
        docker start $allContainers | Out-Null
    }
    
    Write-Host "✅ Server Mode Active. All containers are online!" -ForegroundColor Green
}

Start-Sleep -Seconds 3