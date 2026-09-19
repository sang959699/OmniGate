using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

public interface IAsusPresenceService
{
    AsusPresenceStatusDto GetStatus();
    AsusPresenceStatusDto SaveSettings(AsusPresenceSettingsRequest request);
    Task<AsusPresenceStatusDto> GenerateKeyAsync(bool regenerate, CancellationToken cancellationToken);
    Task<AsusPresenceStatusDto> CheckNowAsync(CancellationToken cancellationToken);
    void StartScheduler();
    void Stop();
}

public sealed class AsusPresenceService : IAsusPresenceService
{
    private const string ClientListCommand = "cat /tmp/clientlist.json";
    private static readonly Regex UserNamePattern = new("^[A-Za-z0-9_.-]{1,64}$", RegexOptions.Compiled);
    private static readonly Regex HostNamePattern = new("^[A-Za-z0-9.-]{1,253}$", RegexOptions.Compiled);

    private readonly ILogger<AsusPresenceService> _logger;
    private readonly IXiaomiPurifierService _xiaomiPurifier;
    private readonly string _settingsPath;
    private readonly string _privateKeyPath;
    private readonly string _publicKeyPath;
    private readonly string _knownHostsPath;
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly CancellationTokenSource _schedulerCancellation = new();

    private AsusPresenceSettings _settings;
    private AsusPresenceSnapshot _snapshot = new();
    private Task? _schedulerTask;

    public AsusPresenceService(IConfiguration config, ILogger<AsusPresenceService> logger, IXiaomiPurifierService xiaomiPurifier)
    {
        _logger = logger;
        _xiaomiPurifier = xiaomiPurifier;
        _settingsPath = LocalStatePath.Resolve(config, "Storage:AsusPresenceFile", "asus-presence.json");
        _privateKeyPath = LocalStatePath.Resolve(config, "Storage:AsusPresencePrivateKey", "asus-presence-ed25519");
        _publicKeyPath = _privateKeyPath + ".pub";
        _knownHostsPath = LocalStatePath.Resolve(config, "Storage:AsusPresenceKnownHosts", "asus-presence-known-hosts");

        _settings = LoadSettings() ?? new AsusPresenceSettings
        {
            RouterHost = config["AsusPresence:RouterHost"] ?? "router.local",
            Port = config.GetValue("AsusPresence:Port", 22),
            UserName = config["AsusPresence:UserName"] ?? string.Empty,
            DeviceMac = NormalizeMac(config["AsusPresence:DeviceMac"] ?? string.Empty)
        };
    }

    public void StartScheduler()
    {
        lock (_stateLock)
        {
            _schedulerTask ??= Task.Run(() => SchedulerLoopAsync(_schedulerCancellation.Token));
        }
    }

    public void Stop()
    {
        _schedulerCancellation.Cancel();
        try { _schedulerTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }

    public AsusPresenceStatusDto GetStatus()
    {
        lock (_stateLock)
        {
            return BuildStatusLocked();
        }
    }

    public AsusPresenceStatusDto SaveSettings(AsusPresenceSettingsRequest request)
    {
        var settings = ValidateSettings(request);
        lock (_stateLock)
        {
            _settings = settings;
            _snapshot = new AsusPresenceSnapshot
            {
                State = "Unknown",
                Message = "Settings saved. Run Check now after installing the public key in Merlin."
            };
            SaveSettingsLocked();
            return BuildStatusLocked();
        }
    }

    public async Task<AsusPresenceStatusDto> GenerateKeyAsync(bool regenerate, CancellationToken cancellationToken)
    {
        if (File.Exists(_privateKeyPath) && !regenerate)
            throw new InvalidOperationException("An ASUS presence key already exists. Use regenerate only if you intend to replace the key in Merlin.");

        string tempKeyPath = _privateKeyPath + ".new-" + Guid.NewGuid().ToString("N");
        try
        {
            var result = await RunProcessAsync(GetOpenSshExecutable("ssh-keygen.exe"), new[]
            {
                "-q", "-t", "ed25519", "-N", string.Empty,
                "-C", "omnigate-asus-presence", "-f", tempKeyPath
            }, TimeSpan.FromSeconds(15), cancellationToken);

            if (result.ExitCode != 0 || !File.Exists(tempKeyPath) || !File.Exists(tempKeyPath + ".pub"))
                throw new InvalidOperationException(FormatProcessError("Could not generate the SSH key", result));

            File.Move(tempKeyPath, _privateKeyPath, true);
            File.Move(tempKeyPath + ".pub", _publicKeyPath, true);

            lock (_stateLock)
            {
                _snapshot = new AsusPresenceSnapshot
                {
                    State = "Unknown",
                    Message = "Key generated. Copy the public key into the Merlin Authorized Keys field, then run Check now."
                };
                return BuildStatusLocked();
            }
        }
        finally
        {
            TryDelete(tempKeyPath);
            TryDelete(tempKeyPath + ".pub");
        }
    }

    public Task<AsusPresenceStatusDto> CheckNowAsync(CancellationToken cancellationToken) =>
        CheckNowInternalAsync(applyAutomation: false, cancellationToken);

    private async Task<AsusPresenceStatusDto> CheckNowInternalAsync(bool applyAutomation, CancellationToken cancellationToken)
    {
        if (!await _checkLock.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("A presence check is already running.");

        try
        {
            AsusPresenceSettings settings;
            lock (_stateLock) settings = _settings.Clone();
            ValidateReady(settings);

            var result = await RunProcessAsync(GetOpenSshExecutable("ssh.exe"), new[]
            {
                "-i", _privateKeyPath,
                "-p", settings.Port.ToString(),
                "-o", "BatchMode=yes",
                "-o", "ConnectTimeout=8",
                "-o", "ConnectionAttempts=1",
                "-o", "StrictHostKeyChecking=accept-new",
                "-o", $"UserKnownHostsFile={_knownHostsPath}",
                $"{settings.UserName}@{settings.RouterHost}",
                ClientListCommand
            }, TimeSpan.FromSeconds(15), cancellationToken);

            AsusPresenceSnapshot snapshot;
            if (result.ExitCode != 0)
            {
                snapshot = new AsusPresenceSnapshot
                {
                    State = "Unknown",
                    CheckedAt = DateTimeOffset.Now,
                    Message = FormatProcessError("Router check failed", result)
                };
            }
            else
            {
                var match = FindClient(result.StandardOutput, settings.DeviceMac);
                snapshot = new AsusPresenceSnapshot
                {
                    State = match.IsOnline ? "Connected" : "Not connected",
                    CheckedAt = DateTimeOffset.Now,
                    ClientIp = match.IpAddress,
                    Interface = match.Interface,
                    Message = match.IsOnline
                        ? "The iPhone is present in the ASUS live client list."
                        : "The iPhone is absent from the ASUS live client list."
                };
            }

            AsusPresenceStatusDto status;
            lock (_stateLock)
            {
                _snapshot = snapshot;
                status = BuildStatusLocked();
            }
            if (applyAutomation && snapshot.State is "Connected" or "Not connected")
                await _xiaomiPurifier.ApplyPresenceAsync(snapshot.State == "Connected", cancellationToken);
            return status;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not InvalidOperationException)
        {
            _logger.LogWarning(ex, "[ASUS Presence] Presence check failed.");
            lock (_stateLock)
            {
                _snapshot = new AsusPresenceSnapshot
                {
                    State = "Unknown",
                    CheckedAt = DateTimeOffset.Now,
                    Message = ex.Message
                };
                return BuildStatusLocked();
            }
        }
        finally
        {
            _checkLock.Release();
        }
    }

    private async Task SchedulerLoopAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset next = SchedulerDueTime(DateTimeOffset.Now);
        _logger.LogInformation("[ASUS Presence] Scheduler started. Next automatic check: {NextCheck}.", next);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                DateTimeOffset now = DateTimeOffset.Now;
                if (now < next)
                {
                    TimeSpan remaining = next - now;
                    await Task.Delay(remaining < TimeSpan.FromSeconds(30) ? remaining : TimeSpan.FromSeconds(30), cancellationToken);
                    continue;
                }

                _logger.LogInformation("[ASUS Presence] Starting scheduled check for {ScheduledTime}.", next);
                await CheckNowInternalAsync(applyAutomation: true, cancellationToken);
                _logger.LogInformation("[ASUS Presence] Scheduled check completed.");
                next = NextHourlyCheck(DateTimeOffset.Now);
                _logger.LogInformation("[ASUS Presence] Next automatic check: {NextCheck}.", next);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogInformation("[ASUS Presence] Scheduled check skipped: {Message}", ex.Message);
                next = ex.Message.Contains("already running", StringComparison.OrdinalIgnoreCase)
                    ? DateTimeOffset.Now.AddSeconds(30)
                    : NextHourlyCheck(DateTimeOffset.Now);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ASUS Presence] Scheduled check failed.");
                next = NextHourlyCheck(DateTimeOffset.Now);
            }
        }
    }

    private AsusPresenceStatusDto BuildStatusLocked()
    {
        string? publicKey = null;
        try
        {
            if (File.Exists(_publicKeyPath)) publicKey = File.ReadAllText(_publicKeyPath).Trim();
        }
        catch { }

        return new AsusPresenceStatusDto
        {
            RouterHost = _settings.RouterHost,
            Port = _settings.Port,
            UserName = _settings.UserName,
            DeviceMac = _settings.DeviceMac,
            KeyGenerated = File.Exists(_privateKeyPath) && !string.IsNullOrWhiteSpace(publicKey),
            PublicKey = publicKey,
            State = _snapshot.State,
            LastCheckedAt = _snapshot.CheckedAt,
            NextCheckAt = NextHourlyCheck(DateTimeOffset.Now),
            ClientIp = _snapshot.ClientIp,
            Interface = _snapshot.Interface,
            Message = _snapshot.Message,
            ReadOnly = true
        };
    }

    private AsusPresenceSettings? LoadSettings()
    {
        try
        {
            return File.Exists(_settingsPath)
                ? JsonSerializer.Deserialize<AsusPresenceSettings>(File.ReadAllText(_settingsPath))
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ASUS Presence] Could not load {Path}.", _settingsPath);
            return null;
        }
    }

    private void SaveSettingsLocked()
    {
        string tempPath = _settingsPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(_settings, _jsonOptions));
        File.Move(tempPath, _settingsPath, true);
    }

    private static AsusPresenceSettings ValidateSettings(AsusPresenceSettingsRequest request)
    {
        string host = (request.RouterHost ?? string.Empty).Trim();
        string userName = (request.UserName ?? string.Empty).Trim();
        string mac = NormalizeMac(request.DeviceMac ?? string.Empty);

        if (!IPAddress.TryParse(host, out _) && !HostNamePattern.IsMatch(host))
            throw new InvalidOperationException("Enter a valid router IP address or host name.");
        if (request.Port is < 1 or > 65535)
            throw new InvalidOperationException("SSH port must be between 1 and 65535.");
        if (!UserNamePattern.IsMatch(userName))
            throw new InvalidOperationException("Enter the ASUS SSH user name.");
        if (mac.Length != 17)
            throw new InvalidOperationException("Enter the iPhone MAC address in AA:BB:CC:DD:EE:FF format.");

        return new AsusPresenceSettings { RouterHost = host, Port = request.Port, UserName = userName, DeviceMac = mac };
    }

    private void ValidateReady(AsusPresenceSettings settings)
    {
        _ = ValidateSettings(new AsusPresenceSettingsRequest(settings.RouterHost, settings.Port, settings.UserName, settings.DeviceMac));
        if (!File.Exists(_privateKeyPath))
            throw new InvalidOperationException("Generate the OmniGate SSH key first.");
    }

    private static ClientMatch FindClient(string json, string targetMac)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("The router returned an empty live client list.");

        using JsonDocument document = JsonDocument.Parse(json);
        string normalizedTarget = NormalizeMac(targetMac);
        ClientMatch? match = FindClientRecursive(document.RootElement, normalizedTarget, null);
        return match ?? new ClientMatch(false, null, null);
    }

    private static ClientMatch? FindClientRecursive(JsonElement element, string targetMac, string? propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            bool objectMatches = NormalizeMac(propertyName ?? string.Empty) == targetMac;
            string? ip = null;
            string? connection = null;
            bool? online = null;

            foreach (JsonProperty property in element.EnumerateObject())
            {
                string name = property.Name;
                string? value = ScalarText(property.Value);
                if ((name.Equals("mac", StringComparison.OrdinalIgnoreCase) || name.Equals("macaddr", StringComparison.OrdinalIgnoreCase)) && NormalizeMac(value ?? string.Empty) == targetMac)
                    objectMatches = true;
                else if (name.Equals("ip", StringComparison.OrdinalIgnoreCase) || name.Equals("ipaddr", StringComparison.OrdinalIgnoreCase))
                    ip = value;
                else if (name.Equals("isOnline", StringComparison.OrdinalIgnoreCase) || name.Equals("online", StringComparison.OrdinalIgnoreCase))
                    online = ParseBoolean(value);
                else if (name.Equals("isWL", StringComparison.OrdinalIgnoreCase) || name.Equals("interface", StringComparison.OrdinalIgnoreCase) || name.Equals("amesh_papMac", StringComparison.OrdinalIgnoreCase))
                    connection = value;
            }

            if (objectMatches) return new ClientMatch(online ?? true, ip, connection);

            foreach (JsonProperty property in element.EnumerateObject())
            {
                ClientMatch? nested = FindClientRecursive(property.Value, targetMac, property.Name);
                if (nested != null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                ClientMatch? nested = FindClientRecursive(item, targetMac, propertyName);
                if (nested != null) return nested;
            }
        }

        return null;
    }

    private static string? ScalarText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null
    };

    private static bool? ParseBoolean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (bool.TryParse(value, out bool parsed)) return parsed;
        if (int.TryParse(value, out int numeric)) return numeric != 0;
        return null;
    }

    private static string NormalizeMac(string value)
    {
        string hex = Regex.Replace(value ?? string.Empty, "[^0-9A-Fa-f]", string.Empty).ToUpperInvariant();
        return hex.Length == 12 ? string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2))) : (value ?? string.Empty).Trim().ToUpperInvariant();
    }

    private static DateTimeOffset NextHourlyCheck(DateTimeOffset now)
    {
        DateTimeOffset hour = new(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset);
        return hour.AddHours(1);
    }

    private static DateTimeOffset SchedulerDueTime(DateTimeOffset now)
    {
        DateTimeOffset currentHour = new(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset);
        // If OmniGate starts just after :00, execute that hour instead of silently waiting 60 minutes.
        return now - currentHour <= TimeSpan.FromMinutes(2) ? currentHour : currentHour.AddHours(1);
    }

    private static string GetOpenSshExecutable(string fileName)
    {
        string systemPath = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string path = Path.Combine(systemPath, "OpenSSH", fileName);
        return File.Exists(path) ? path : fileName;
    }

    private static async Task<ProcessResult> RunProcessAsync(string fileName, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException($"Could not start {Path.GetFileName(fileName)}.");

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(true); } catch { }
            throw new TimeoutException($"{Path.GetFileName(fileName)} timed out after {timeout.TotalSeconds:0} seconds.");
        }

        return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
    }

    private static string FormatProcessError(string prefix, ProcessResult result)
    {
        string detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        detail = Regex.Replace(detail.Trim(), "\\s+", " ");
        if (detail.Length > 300) detail = detail[..300] + "…";
        return string.IsNullOrWhiteSpace(detail) ? $"{prefix} (exit code {result.ExitCode})." : $"{prefix}: {detail}";
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
    private sealed record ClientMatch(bool IsOnline, string? IpAddress, string? Interface);

    private sealed class AsusPresenceSettings
    {
        public string RouterHost { get; set; } = "router.local";
        public int Port { get; set; } = 22;
        public string UserName { get; set; } = string.Empty;
        public string DeviceMac { get; set; } = string.Empty;
        public AsusPresenceSettings Clone() => (AsusPresenceSettings)MemberwiseClone();
    }

    private sealed class AsusPresenceSnapshot
    {
        public string State { get; set; } = "Unknown";
        public DateTimeOffset? CheckedAt { get; set; }
        public string? ClientIp { get; set; }
        public string? Interface { get; set; }
        public string Message { get; set; } = "Generate an SSH key and configure the router connection.";
    }
}

public sealed record AsusPresenceSettingsRequest(string? RouterHost, int Port, string? UserName, string? DeviceMac);
public sealed record AsusPresenceKeyRequest(bool Regenerate = false);

public sealed class AsusPresenceStatusDto
{
    public string RouterHost { get; set; } = string.Empty;
    public int Port { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string DeviceMac { get; set; } = string.Empty;
    public bool KeyGenerated { get; set; }
    public string? PublicKey { get; set; }
    public string State { get; set; } = "Unknown";
    public DateTimeOffset? LastCheckedAt { get; set; }
    public DateTimeOffset NextCheckAt { get; set; }
    public string? ClientIp { get; set; }
    public string? Interface { get; set; }
    public string Message { get; set; } = string.Empty;
    public bool ReadOnly { get; set; }
}
