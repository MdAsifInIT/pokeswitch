using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PokeSwitch.Services;

public sealed record GpuStatus(
    bool Found,
    bool Multiple,
    string? FriendlyName,
    string? InstanceId,
    string? Status,
    bool IsEnabled,
    string Message);

public sealed record GpuToggleResult(
    bool Success,
    GpuStatus Status,
    string Action,
    string Message);

public interface IGpuManager
{
    Task<GpuStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<GpuToggleResult> ToggleAsync(CancellationToken cancellationToken = default);
}

public sealed class GpuManager : IGpuManager
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private readonly IProcessRunner _processRunner;

    public GpuManager(IProcessRunner? processRunner = null)
    {
        _processRunner = processRunner ?? new ProcessRunner();
    }

    public async Task<GpuStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        string script = """
            $Devices = @(Get-PnpDevice -Class Display -ErrorAction SilentlyContinue |
                Where-Object { $_.FriendlyName -like "*NVIDIA*RTX 3050*" })

            if (-not $Devices -or $Devices.Count -eq 0) {
                [pscustomobject]@{
                    found = $false
                    multiple = $false
                    friendlyName = $null
                    instanceId = $null
                    status = $null
                    isEnabled = $false
                    message = "NVIDIA RTX 3050 GPU could not be found in Device Manager."
                } | ConvertTo-Json -Compress
                exit 0
            }

            if ($Devices.Count -gt 1) {
                [pscustomobject]@{
                    found = $true
                    multiple = $true
                    friendlyName = $null
                    instanceId = $null
                    status = $null
                    isEnabled = $false
                    message = "Multiple matching NVIDIA RTX 3050 devices found. Refusing to guess."
                } | ConvertTo-Json -Compress
                exit 0
            }

            $Device = $Devices[0]
            [pscustomobject]@{
                found = $true
                multiple = $false
                friendlyName = $Device.FriendlyName
                instanceId = $Device.InstanceId
                status = $Device.Status
                isEnabled = ($Device.Status -eq "OK")
                message = "GPU status read successfully."
            } | ConvertTo-Json -Compress
            """;

        return await RunStatusScriptAsync(script, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GpuToggleResult> ToggleAsync(CancellationToken cancellationToken = default)
    {
        string script = """
            $Devices = @(Get-PnpDevice -Class Display -ErrorAction SilentlyContinue |
                Where-Object { $_.FriendlyName -like "*NVIDIA*RTX 3050*" })

            if (-not $Devices -or $Devices.Count -eq 0) {
                [pscustomobject]@{
                    success = $false
                    action = "None"
                    message = "NVIDIA RTX 3050 GPU could not be found in Device Manager."
                    status = @{
                        found = $false
                        multiple = $false
                        friendlyName = $null
                        instanceId = $null
                        status = $null
                        isEnabled = $false
                        message = "NVIDIA RTX 3050 GPU could not be found in Device Manager."
                    }
                } | ConvertTo-Json -Depth 4 -Compress
                exit 0
            }

            if ($Devices.Count -gt 1) {
                [pscustomobject]@{
                    success = $false
                    action = "None"
                    message = "Multiple matching NVIDIA RTX 3050 devices found. Refusing to guess."
                    status = @{
                        found = $true
                        multiple = $true
                        friendlyName = $null
                        instanceId = $null
                        status = $null
                        isEnabled = $false
                        message = "Multiple matching NVIDIA RTX 3050 devices found. Refusing to guess."
                    }
                } | ConvertTo-Json -Depth 4 -Compress
                exit 0
            }

            $Device = $Devices[0]
            $Action = if ($Device.Status -eq "OK") { "Disable" } else { "Enable" }

            try {
                if ($Action -eq "Disable") {
                    Disable-PnpDevice -InstanceId $Device.InstanceId -Confirm:$false -ErrorAction Stop
                } else {
                    Enable-PnpDevice -InstanceId $Device.InstanceId -Confirm:$false -ErrorAction Stop
                }

                Start-Sleep -Milliseconds 500
                $NewDevice = Get-PnpDevice -InstanceId $Device.InstanceId -ErrorAction Stop
                $IsEnabled = ($NewDevice.Status -eq "OK")
                $Success = if ($Action -eq "Disable") { -not $IsEnabled } else { $IsEnabled }
                $Message = if ($Success) {
                    if ($Action -eq "Disable") { "NVIDIA GPU device is now disabled." } else { "NVIDIA GPU is enabled and ready." }
                } else {
                    if ($Action -eq "Disable") { "Failed to disable the GPU. Close GPU-using apps and try again." } else { "Failed to enable the GPU. New status: $($NewDevice.Status)" }
                }

                [pscustomobject]@{
                    success = $Success
                    action = $Action
                    message = $Message
                    status = @{
                        found = $true
                        multiple = $false
                        friendlyName = $NewDevice.FriendlyName
                        instanceId = $NewDevice.InstanceId
                        status = $NewDevice.Status
                        isEnabled = $IsEnabled
                        message = "GPU status read successfully."
                    }
                } | ConvertTo-Json -Depth 4 -Compress
            } catch {
                [pscustomobject]@{
                    success = $false
                    action = $Action
                    message = $_.Exception.Message
                    status = @{
                        found = $true
                        multiple = $false
                        friendlyName = $Device.FriendlyName
                        instanceId = $Device.InstanceId
                        status = $Device.Status
                        isEnabled = ($Device.Status -eq "OK")
                        message = "GPU toggle failed."
                    }
                } | ConvertTo-Json -Depth 4 -Compress
            }
            """;

        ProcessResult result = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            GpuStatus status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            return new GpuToggleResult(false, status, "None", "Timed out while toggling the GPU.");
        }

        if (result.ExitCode != 0)
        {
            GpuStatus status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            string message = string.IsNullOrWhiteSpace(result.StandardError)
                ? "PowerShell failed while toggling the GPU."
                : result.StandardError.Trim();
            return new GpuToggleResult(false, status, "None", message);
        }

        try
        {
            GpuTogglePayload? payload = JsonSerializer.Deserialize<GpuTogglePayload>(result.StandardOutput, JsonOptions);
            if (payload?.Status == null)
            {
                throw new JsonException("GPU toggle output did not include a status payload.");
            }

            return new GpuToggleResult(
                payload.Success,
                payload.Status.ToStatus(),
                payload.Action ?? "None",
                payload.Message ?? "GPU toggle completed.");
        }
        catch (JsonException ex)
        {
            GpuStatus status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            return new GpuToggleResult(false, status, "None", $"Unable to parse GPU toggle output: {ex.Message}");
        }
    }

    private async Task<GpuStatus> RunStatusScriptAsync(string script, CancellationToken cancellationToken)
    {
        ProcessResult result = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            return Unavailable("Timed out while reading GPU status.");
        }

        if (result.ExitCode != 0)
        {
            string message = string.IsNullOrWhiteSpace(result.StandardError)
                ? "PowerShell failed while reading GPU status."
                : result.StandardError.Trim();
            return Unavailable(message);
        }

        try
        {
            GpuStatusPayload? payload = JsonSerializer.Deserialize<GpuStatusPayload>(result.StandardOutput, JsonOptions);
            return payload?.ToStatus() ?? Unavailable("GPU status output was empty.");
        }
        catch (JsonException ex)
        {
            return Unavailable($"Unable to parse GPU status output: {ex.Message}");
        }
    }

    private Task<ProcessResult> RunPowerShellAsync(string script, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        return _processRunner.RunAsync(psi, CommandTimeout, cancellationToken);
    }

    private static GpuStatus Unavailable(string message)
    {
        return new GpuStatus(false, false, null, null, null, false, message);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class GpuStatusPayload
    {
        [JsonPropertyName("found")]
        public bool Found { get; set; }

        [JsonPropertyName("multiple")]
        public bool Multiple { get; set; }

        [JsonPropertyName("friendlyName")]
        public string? FriendlyName { get; set; }

        [JsonPropertyName("instanceId")]
        public string? InstanceId { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("isEnabled")]
        public bool IsEnabled { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        public GpuStatus ToStatus()
        {
            return new GpuStatus(
                Found,
                Multiple,
                FriendlyName,
                InstanceId,
                Status,
                IsEnabled,
                Message ?? string.Empty);
        }
    }

    private sealed class GpuTogglePayload
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("status")]
        public GpuStatusPayload? Status { get; set; }

        [JsonPropertyName("action")]
        public string? Action { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }
}
