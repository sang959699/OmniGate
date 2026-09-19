using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MiHome.Net.Dto;
using MiHome.Net.Miio;
using MiHome.Net.Service;
using MiHome.Net.Utils;

public interface IXiaomiPurifierService
{
    XiaomiPurifierStatusDto GetStatus();
    XiaomiPurifierStatusDto StartLogin();
    Task<XiaomiPurifierStatusDto> TestLocalAsync(CancellationToken cancellationToken);
    Task<XiaomiPurifierStatusDto> SetPowerAsync(bool power, CancellationToken cancellationToken);
    XiaomiPurifierStatusDto SetAutomation(bool enabled);
    Task ApplyPresenceAsync(bool isHome, CancellationToken cancellationToken);
    string QrCodePath { get; }
}

public sealed class XiaomiPurifierService : IXiaomiPurifierService
{
    private const string ExpectedModel = "zhimi.airp.meb1";
    private const string ExpectedMac = "84:46:93:C1:7F:4A";
    private const string MalaysiaCloudUrl = "https://sg.api.io.mi.com/app/home/device_list";
    private static readonly byte[] TokenEntropy = Encoding.UTF8.GetBytes("OmniGate.XiaomiPurifier.Token.v1");
    private static readonly Regex TokenPattern = new("^[0-9a-fA-F]{32}$", RegexOptions.Compiled);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<XiaomiPurifierService> _logger;
    private readonly string _settingsPath;
    private readonly string _tokenPath;
    private readonly string _authPath = Path.Combine(AppContext.BaseDirectory, "auth.json");
    private readonly object _lock = new();
    private readonly SemaphoreSlim _localLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private XiaomiPurifierSettings _settings;
    private XiaomiPurifierSnapshot _snapshot = new();
    private bool _loginRunning;

    public XiaomiPurifierService(IConfiguration config, IServiceScopeFactory scopeFactory, ILogger<XiaomiPurifierService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _settingsPath = LocalStatePath.Resolve(config, "Storage:XiaomiPurifierFile", "xiaomi-purifier.json");
        _tokenPath = LocalStatePath.Resolve(config, "Storage:XiaomiPurifierTokenFile", "xiaomi-purifier-token.dat");
        QrCodePath = Path.Combine(LocalStatePath.Resolve(config, "Storage:XiaomiQrDirectory", "xiaomi-qr"), "qr.png");
        _settings = LoadSettings() ?? new XiaomiPurifierSettings();
        _snapshot.Message = HasTokenLocked()
            ? "Local token is stored. Run the LAN test before enabling automation."
            : "Start one-time Xiaomi Home sign-in to obtain the local token.";
    }

    public string QrCodePath { get; }

    public XiaomiPurifierStatusDto GetStatus()
    {
        lock (_lock) return BuildStatusLocked();
    }

    public XiaomiPurifierStatusDto StartLogin()
    {
        lock (_lock)
        {
            if (_loginRunning) return BuildStatusLocked();
            _loginRunning = true;
            _snapshot.Message = "Preparing Xiaomi Home QR code…";
            TryDelete(QrCodePath);
            TryDelete(_authPath);
            _ = Task.Run(LoginWorkerAsync);
            return BuildStatusLocked();
        }
    }

    private async Task LoginWorkerAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var driver = scope.ServiceProvider.GetRequiredService<IMiHomeDriver>();
            await driver.Cloud.LoginAsync();
            var devices = await GetMalaysiaDevicesAsync();
            var purifier = devices.FirstOrDefault(d => string.Equals(NormalizeMac(d.Mac), ExpectedMac, StringComparison.OrdinalIgnoreCase))
                ?? devices.FirstOrDefault(d => string.Equals(d.Model, ExpectedModel, StringComparison.OrdinalIgnoreCase))
                ?? devices.FirstOrDefault(d => string.Equals(d.LocalIp, "192.168.1.106", StringComparison.OrdinalIgnoreCase));

            if (purifier == null)
            {
                string models = string.Join(", ", devices.Select(d => d.Model).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().Take(8));
                string detail = devices.Count == 0 ? "Xiaomi returned no devices from the Singapore region." : $"Xiaomi returned {devices.Count} device(s): {models}.";
                throw new InvalidOperationException("Smart Air Purifier Elite was not found. " + detail);
            }
            if (string.IsNullOrWhiteSpace(purifier.Token) || !TokenPattern.IsMatch(purifier.Token))
                throw new InvalidOperationException("The purifier was found, but Xiaomi did not provide a usable local token.");

            SaveEncryptedToken(purifier.Token);
            lock (_lock)
            {
                _settings.DeviceName = string.IsNullOrWhiteSpace(purifier.Name) ? "Xiaomi Smart Air Purifier Elite" : purifier.Name;
                _settings.DeviceId = purifier.Did;
                _settings.Model = purifier.Model;
                _settings.MacAddress = NormalizeMac(purifier.Mac);
                if (!string.IsNullOrWhiteSpace(purifier.LocalIp)) _settings.IpAddress = purifier.LocalIp;
                _settings.LocalValidated = false;
                _settings.AutomationEnabled = false;
                SaveSettingsLocked();
                _snapshot.Message = "Token stored securely. Xiaomi Home is signed out; run the LAN test next.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Xiaomi] One-time sign-in failed.");
            lock (_lock) _snapshot.Message = "Xiaomi sign-in failed: " + CleanMessage(ex.Message);
        }
        finally
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IMiHomeDriver>().Cloud.LogOutAsync();
            }
            catch (Exception ex) { _logger.LogDebug(ex, "[Xiaomi] Could not clear the temporary cloud session through the library."); }
            TryDelete(_authPath);
            TryDelete(QrCodePath);
            lock (_lock) _loginRunning = false;
        }
    }

    private async Task<List<XiaoMiDeviceInfo>> GetMalaysiaDevicesAsync()
    {
        if (!File.Exists(_authPath)) throw new InvalidOperationException("Xiaomi sign-in completed without creating temporary account credentials.");
        var auth = JsonSerializer.Deserialize<XiaomiLoginInfo>(File.ReadAllText(_authPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (auth == null || string.IsNullOrWhiteSpace(auth.UserId) || string.IsNullOrWhiteSpace(auth.ServiceToken) || string.IsNullOrWhiteSpace(auth.Ssecurity))
            throw new InvalidOperationException("Xiaomi sign-in credentials were incomplete.");

        var parameters = GetRegionalRc4Parameters(MalaysiaCloudUrl, auth.Ssecurity);
        string signedNonce = parameters["signedNonce"];

        var endpoint = new Uri(MalaysiaCloudUrl);
        var cookies = new CookieContainer();
        foreach (var pair in new Dictionary<string, string>
        {
            ["userId"] = auth.UserId,
            ["serviceToken"] = auth.ServiceToken,
            ["yetAnotherServiceToken"] = auth.ServiceToken,
            ["is_daylight"] = "0",
            ["channel"] = "MI_APP_STORE",
            ["dst_offset"] = "0",
            ["locale"] = "en_MY",
            ["timezone"] = "GMT+08:00",
            ["sdkVersion"] = "3.9",
            ["deviceId"] = auth.DeviceId ?? string.Empty
        }) cookies.Add(endpoint, new Cookie(pair.Key, pair.Value));

        using var handler = new HttpClientHandler { CookieContainer = cookies, UseCookies = true };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "APP/com.xiaomi.mihome APPV/6.0.103 iosPassportSDK/3.9.0 iOS/14.4 miHSTS");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-XIAOMI-PROTOCAL-FLAG-CLI", "PROTOCAL-HTTP2");
        client.DefaultRequestHeaders.TryAddWithoutValidation("MIOT-ENCRYPT-ALGORITHM", "ENCRYPT-RC4");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "identity");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");

        using var content = new FormUrlEncodedContent(parameters.Where(p => p.Key != "signedNonce"));
        using var response = await client.PostAsync(endpoint, content);
        string encryptedResponse = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Xiaomi Singapore device request failed with HTTP {(int)response.StatusCode}.");

        string json;
        try { json = DecryptRegionalData(signedNonce, encryptedResponse); }
        catch (Exception ex) { throw new InvalidOperationException("Xiaomi Singapore returned an unreadable device response.", ex); }

        var result = JsonSerializer.Deserialize<GetDeviceListOutputResult>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (result?.Code != 0)
            throw new InvalidOperationException("Xiaomi Singapore device request failed: " + (result?.Message ?? "unknown response"));
        return result.Result?.List ?? new List<XiaoMiDeviceInfo>();
    }

    private static Dictionary<string, string> GetRegionalRc4Parameters(string url, string ssecurity)
    {
        var data = new Dictionary<string, string>
        {
            ["data"] = JsonSerializer.Serialize(new
            {
                GetVirtualModel = true,
                GetHuamiDevices = 1,
                Get_split_device = false,
                Support_smart_home = true
            })
        };
        string nonce = CalculateRegionalNonce();
        string signedNonce = Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(ssecurity).Concat(Convert.FromBase64String(nonce)).ToArray()));
        data["rc4_hash__"] = RegionalSha1Sign(url, data, signedNonce);
        foreach (string key in data.Keys.ToArray())
            data[key] = Convert.ToBase64String(new Rc4(signedNonce).Init1024().Crypt(Encoding.UTF8.GetBytes(data[key])));
        data["signature"] = RegionalSha1Sign(url, data, signedNonce);
        data["_nonce"] = nonce;
        data["ssecurity"] = ssecurity;
        data["signedNonce"] = signedNonce;
        return data;
    }

    private static string RegionalSha1Sign(string url, Dictionary<string, string> data, string nonce)
    {
        string path = new Uri(url).AbsolutePath;
        if (path.StartsWith("/app/", StringComparison.Ordinal)) path = path[4..];
        var parts = new List<string> { "POST", path };
        parts.AddRange(data.Select(pair => $"{pair.Key}={pair.Value}"));
        parts.Add(nonce);
        return Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(string.Join("&", parts))));
    }

    private static string CalculateRegionalNonce()
    {
        byte[] nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce.AsSpan(0, 8));
        BinaryPrimitives.WriteInt32BigEndian(nonce.AsSpan(8, 4), checked((int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60)));
        return Convert.ToBase64String(nonce);
    }

    private static string DecryptRegionalData(string signedNonce, string encryptedData) =>
        Encoding.UTF8.GetString(new Rc4(signedNonce).Init1024().Crypt(Convert.FromBase64String(encryptedData)));

    public async Task<XiaomiPurifierStatusDto> TestLocalAsync(CancellationToken cancellationToken)
    {
        await _localLock.WaitAsync(cancellationToken);
        try
        {
            var (ip, token) = GetLocalCredentials();
            using var scope = _scopeFactory.CreateScope();
            var local = scope.ServiceProvider.GetRequiredService<IMiHomeDriver>().Local;
            var result = await Task.Run(() => local.GetPropertyAsync(ip, token, new GetPropertyPayload { Siid = 2, Piid = 1 }), cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            var item = result?.Result?.FirstOrDefault();
            if (item == null || item.Code != 0)
                throw new InvalidOperationException($"The purifier returned error code {item?.Code.ToString() ?? "none"}.");

            bool power = ReadBoolean(item.Value);
            lock (_lock)
            {
                _settings.LocalValidated = true;
                SaveSettingsLocked();
                _snapshot.Power = power;
                _snapshot.LastLocalContact = DateTimeOffset.Now;
                _snapshot.Message = $"Direct LAN control works. Purifier is currently {(power ? "on" : "off")}.";
                return BuildStatusLocked();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_lock)
            {
                _settings.LocalValidated = false;
                _settings.AutomationEnabled = false;
                SaveSettingsLocked();
                _snapshot.Message = "LAN test failed: " + CleanMessage(ex.Message);
            }
            throw new InvalidOperationException(GetStatus().Message, ex);
        }
        finally { _localLock.Release(); }
    }

    public async Task<XiaomiPurifierStatusDto> SetPowerAsync(bool power, CancellationToken cancellationToken)
    {
        await _localLock.WaitAsync(cancellationToken);
        try
        {
            lock (_lock)
            {
                if (!_settings.LocalValidated) throw new InvalidOperationException("Run a successful LAN test first.");
            }
            var (ip, token) = GetLocalCredentials();
            using var scope = _scopeFactory.CreateScope();
            var local = scope.ServiceProvider.GetRequiredService<IMiHomeDriver>().Local;
            var result = await Task.Run(() => local.SetPropertyAsync(ip, token, new SetPropertyPayload { Siid = 2, Piid = 1, Value = power }), cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            var item = result?.Result?.FirstOrDefault();
            if (item == null || item.Code != 0)
                throw new InvalidOperationException($"The purifier returned error code {item?.Code.ToString() ?? "none"}.");

            lock (_lock)
            {
                _snapshot.Power = power;
                _snapshot.LastLocalContact = DateTimeOffset.Now;
                _snapshot.Message = $"Purifier turned {(power ? "on" : "off")} over the local network.";
                return BuildStatusLocked();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not InvalidOperationException)
        {
            throw new InvalidOperationException("Local purifier command failed: " + CleanMessage(ex.Message), ex);
        }
        finally { _localLock.Release(); }
    }

    public XiaomiPurifierStatusDto SetAutomation(bool enabled)
    {
        lock (_lock)
        {
            if (enabled && !_settings.LocalValidated)
                throw new InvalidOperationException("Run a successful LAN test before enabling hourly presence automation.");
            _settings.AutomationEnabled = enabled;
            SaveSettingsLocked();
            _snapshot.Message = enabled
                ? "Hourly presence automation is enabled. Manual presence checks will not switch the purifier."
                : "Hourly presence automation is disabled.";
            return BuildStatusLocked();
        }
    }

    public async Task ApplyPresenceAsync(bool isHome, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_settings.AutomationEnabled)
            {
                _logger.LogInformation("[Xiaomi] Scheduled presence result was {Presence}; automation is disabled.", isHome ? "home" : "away");
                return;
            }
            if (!_settings.LocalValidated)
            {
                _logger.LogWarning("[Xiaomi] Scheduled presence result was {Presence}; local control is not validated.", isHome ? "home" : "away");
                return;
            }
        }
        try
        {
            _logger.LogInformation("[Xiaomi] Scheduled presence is {Presence}; setting purifier power to {Power} over LAN.", isHome ? "home" : "away", isHome ? "on" : "off");
            await SetPowerAsync(isHome, cancellationToken);
            lock (_lock)
            {
                _settings.LastAutomationAt = DateTimeOffset.Now;
                _settings.LastAutomationResult = $"iPhone {(isHome ? "connected" : "absent")} — purifier turned {(isHome ? "on" : "off")}.";
                _snapshot.Message = "Hourly automation: " + _settings.LastAutomationResult;
                SaveSettingsLocked();
            }
            _logger.LogInformation("[Xiaomi] Scheduled purifier action completed successfully.");
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _settings.LastAutomationAt = DateTimeOffset.Now;
                _settings.LastAutomationResult = "Failed: " + CleanMessage(ex.Message);
                _snapshot.Message = "Hourly automation failed: " + CleanMessage(ex.Message);
                SaveSettingsLocked();
            }
            _logger.LogWarning(ex, "[Xiaomi] Scheduled purifier action failed.");
        }
    }

    private (string Ip, string Token) GetLocalCredentials()
    {
        string ip;
        lock (_lock) ip = _settings.IpAddress;
        if (string.IsNullOrWhiteSpace(ip)) throw new InvalidOperationException("Purifier IP address is missing.");
        if (!File.Exists(_tokenPath)) throw new InvalidOperationException("No Xiaomi local token is stored.");
        try
        {
            byte[] encrypted = File.ReadAllBytes(_tokenPath);
            byte[] clear = ProtectedData.Unprotect(encrypted, TokenEntropy, DataProtectionScope.CurrentUser);
            string token = Encoding.UTF8.GetString(clear);
            CryptographicOperations.ZeroMemory(clear);
            if (!TokenPattern.IsMatch(token)) throw new CryptographicException("Invalid token format.");
            return (ip, token);
        }
        catch (Exception ex) { throw new InvalidOperationException("The stored Xiaomi token cannot be opened by this Windows user.", ex); }
    }

    private void SaveEncryptedToken(string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_tokenPath)!);
        byte[] clear = Encoding.UTF8.GetBytes(token);
        try
        {
            byte[] encrypted = ProtectedData.Protect(clear, TokenEntropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_tokenPath, encrypted);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    private XiaomiPurifierStatusDto BuildStatusLocked() => new()
    {
        DeviceName = _settings.DeviceName,
        Model = _settings.Model,
        IpAddress = _settings.IpAddress,
        TokenStored = HasTokenLocked(),
        LoginRunning = _loginRunning,
        QrReady = _loginRunning && File.Exists(QrCodePath),
        LocalValidated = _settings.LocalValidated,
        AutomationEnabled = _settings.AutomationEnabled,
        Power = _snapshot.Power,
        LastLocalContact = _snapshot.LastLocalContact,
        LastAutomationAt = _settings.LastAutomationAt,
        LastAutomationResult = _settings.LastAutomationResult,
        Message = _snapshot.Message
    };

    private bool HasTokenLocked() => File.Exists(_tokenPath);

    private XiaomiPurifierSettings? LoadSettings()
    {
        try { return File.Exists(_settingsPath) ? JsonSerializer.Deserialize<XiaomiPurifierSettings>(File.ReadAllText(_settingsPath)) : null; }
        catch (Exception ex) { _logger.LogWarning(ex, "[Xiaomi] Could not read settings."); return null; }
    }

    private void SaveSettingsLocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_settings, _jsonOptions));
    }

    private static bool ReadBoolean(object? value)
    {
        if (value is bool boolean) return boolean;
        string text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        if (bool.TryParse(text, out bool parsed)) return parsed;
        if (int.TryParse(text, out int number)) return number != 0;
        throw new InvalidOperationException("The purifier returned an unexpected power value.");
    }

    private static string NormalizeMac(string? value) => (value ?? string.Empty).Trim().Replace('-', ':').ToUpperInvariant();
    private static string CleanMessage(string value) => string.IsNullOrWhiteSpace(value) ? "Unknown error." : value.Replace('\r', ' ').Replace('\n', ' ').Trim();
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    private sealed class XiaomiPurifierSettings
    {
        public string DeviceName { get; set; } = "Xiaomi Smart Air Purifier Elite";
        public string Model { get; set; } = ExpectedModel;
        public string IpAddress { get; set; } = "192.168.1.106";
        public string? DeviceId { get; set; }
        public string MacAddress { get; set; } = ExpectedMac;
        public bool LocalValidated { get; set; }
        public bool AutomationEnabled { get; set; }
        public DateTimeOffset? LastAutomationAt { get; set; }
        public string? LastAutomationResult { get; set; }
    }

    private sealed class XiaomiPurifierSnapshot
    {
        public bool? Power { get; set; }
        public DateTimeOffset? LastLocalContact { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    private sealed class XiaomiLoginInfo
    {
        public string UserId { get; set; } = string.Empty;
        public string ServiceToken { get; set; } = string.Empty;
        public string? DeviceId { get; set; }
        public string Ssecurity { get; set; } = string.Empty;
    }
}

public sealed record XiaomiAutomationRequest(bool Enabled);

public sealed class XiaomiPurifierStatusDto
{
    public string DeviceName { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string IpAddress { get; init; } = string.Empty;
    public bool TokenStored { get; init; }
    public bool LoginRunning { get; init; }
    public bool QrReady { get; init; }
    public bool LocalValidated { get; init; }
    public bool AutomationEnabled { get; init; }
    public bool? Power { get; init; }
    public DateTimeOffset? LastLocalContact { get; init; }
    public DateTimeOffset? LastAutomationAt { get; init; }
    public string? LastAutomationResult { get; init; }
    public string Message { get; init; } = string.Empty;
}
