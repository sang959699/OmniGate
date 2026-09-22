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
    Task<XiaomiPurifierStatusDto> RefreshAsync(CancellationToken cancellationToken);
    Task<XiaomiPurifierStatusDto> TestLocalAsync(CancellationToken cancellationToken);
    Task<XiaomiPurifierStatusDto> SetPowerAsync(bool power, CancellationToken cancellationToken);
    Task<XiaomiPurifierStatusDto> SetControlAsync(XiaomiControlRequest request, CancellationToken cancellationToken);
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
    private readonly string _qrDirectory;
    private readonly string _configuredQrPath;
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
        _qrDirectory = LocalStatePath.Resolve(config, "Storage:XiaomiQrDirectory", "xiaomi-qr");
        _configuredQrPath = Path.Combine(_qrDirectory, "qr.png");
        _settings = LoadSettings() ?? new XiaomiPurifierSettings();
        _snapshot.Message = HasTokenLocked()
            ? "Local token is stored. Run the LAN test before enabling automation."
            : "Start one-time Xiaomi Home sign-in to obtain the local token.";
    }

    public string QrCodePath => FindQrCodePath() ?? _configuredQrPath;

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
            _snapshot.Message = "Preparing Xiaomi Home QR sign-in…";
            _logger.LogInformation("[Xiaomi] QR login started; configured QR directory is {QrDirectory}.", _qrDirectory);
            TryDeleteQrCodes();
            TryDeleteAuthFiles();
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
            _logger.LogInformation("[Xiaomi] Starting MiHome.Net cloud QR login.");
            await driver.Cloud.LoginAsync();
            _logger.LogInformation("[Xiaomi] MiHome.Net cloud login completed; reading the Malaysia-region device list.");
            SetMessage("Xiaomi account signed in. Looking up devices in the Malaysia region…");
            var devices = await GetMalaysiaDevicesAsync();
            _logger.LogInformation("[Xiaomi] Malaysia-region device list returned {DeviceCount} device(s).", devices.Count);
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

            _logger.LogInformation("[Xiaomi] Matched purifier model {Model}; storing its local token.", purifier.Model);
            SaveEncryptedToken(purifier.Token);
            lock (_lock)
            {
                _settings.DeviceName = string.IsNullOrWhiteSpace(purifier.Name) ? "Xiaomi Smart Air Purifier Elite" : purifier.Name;
                _settings.DeviceId = purifier.Did;
                _settings.Model = purifier.Model;
                _settings.MacAddress = NormalizeMac(purifier.Mac);
                if (!string.IsNullOrWhiteSpace(purifier.LocalIp)) _settings.IpAddress = purifier.LocalIp;
                _settings.LocalValidated = false;
                DisableAutomationLocked("Xiaomi Home sign-in completed; run a successful LAN test before enabling automation.", replaceExistingReason: true);
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
                await scope.ServiceProvider.GetRequiredService<IMiHomeDriver>().Cloud.LogOutAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex) { _logger.LogDebug(ex, "[Xiaomi] Could not clear the temporary cloud session through the library."); }
            TryDeleteAuthFiles();
            TryDeleteQrCodes();
            lock (_lock) _loginRunning = false;
        }
    }

    private async Task<List<XiaoMiDeviceInfo>> GetMalaysiaDevicesAsync()
    {
        string? authPath = FindAuthPath();
        if (authPath == null)
        {
            _logger.LogError("[Xiaomi] MiHome.Net login returned without auth.json. Expected: {AuthPath}.", _authPath);
            throw new InvalidOperationException("Xiaomi sign-in completed, but MiHome.Net did not create its temporary auth.json file.");
        }

        _logger.LogInformation("[Xiaomi] Temporary MiHome.Net credentials found; requesting the Malaysia-region device list.");
        var auth = JsonSerializer.Deserialize<XiaomiLoginInfo>(File.ReadAllText(authPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
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
        _logger.LogInformation("[Xiaomi] Sending the Malaysia-region device-list request to {Endpoint}.", endpoint.Host);
        using var response = await client.PostAsync(endpoint, content);
        string encryptedResponse = await response.Content.ReadAsStringAsync();
        _logger.LogInformation("[Xiaomi] Malaysia-region device-list response: HTTP {StatusCode}.", (int)response.StatusCode);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Xiaomi Singapore device request failed with HTTP {(int)response.StatusCode}.");

        string json;
        try { json = DecryptRegionalData(signedNonce, encryptedResponse); }
        catch (Exception ex) { throw new InvalidOperationException("Xiaomi Singapore returned an unreadable device response.", ex); }

        var result = JsonSerializer.Deserialize<GetDeviceListOutputResult>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (result?.Code != 0)
            throw new InvalidOperationException("Xiaomi Singapore device request failed: " + (result?.Message ?? "unknown response"));
        _logger.LogInformation("[Xiaomi] Malaysia-region response decoded successfully; received {DeviceCount} device(s).", result.Result?.List?.Count ?? 0);
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

    public async Task<XiaomiPurifierStatusDto> RefreshAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            // The cloud QR login and local LAN refresh can overlap while the
            // browser is polling. Do not let a local read block QR discovery.
            if (_loginRunning || !HasTokenLocked()) return BuildStatusLocked();
        }
        await _localLock.WaitAsync(cancellationToken);
        try
        {
            var (ip, token) = GetLocalCredentials();
            using var scope = _scopeFactory.CreateScope();
            var local = scope.ServiceProvider.GetRequiredService<IMiHomeDriver>().Local;
            var payloads = new List<GetPropertyPayload>
            {
                new() { Siid = 2, Piid = 1 }, new() { Siid = 2, Piid = 2 }, new() { Siid = 2, Piid = 4 },
                new() { Siid = 2, Piid = 5 }, new() { Siid = 2, Piid = 6 }, new() { Siid = 2, Piid = 7 },
                new() { Siid = 3, Piid = 1 }, new() { Siid = 3, Piid = 4 }, new() { Siid = 3, Piid = 7 },
                new() { Siid = 3, Piid = 8 }, new() { Siid = 3, Piid = 9 }, new() { Siid = 4, Piid = 1 },
                new() { Siid = 4, Piid = 3 }, new() { Siid = 6, Piid = 1 }, new() { Siid = 8, Piid = 1 },
                new() { Siid = 13, Piid = 2 }, new() { Siid = 14, Piid = 1 }, new() { Siid = 15, Piid = 1 },
                new() { Siid = 9, Piid = 1 }, new() { Siid = 9, Piid = 8 }, new() { Siid = 9, Piid = 10 },
                new() { Siid = 9, Piid = 11 }, new() { Siid = 9, Piid = 12 }, new() { Siid = 12, Piid = 1 },
                new() { Siid = 12, Piid = 2 }, new() { Siid = 12, Piid = 3 }, new() { Siid = 12, Piid = 4 },
                new() { Siid = 12, Piid = 5 }, new() { Siid = 11, Piid = 4 }
            };
            var values = new Dictionary<(int Siid, int Piid), GetPropertiesResultItem>();

            // Keep the original single-property power read as the LAN
            // validation probe. It worked before the readings were added, and
            // some purifier firmware returns an empty result for an oversized
            // get_properties request even though get_prop works normally.
            var powerResult = await ReadPropertyAsync(local, ip, token, payloads[0], cancellationToken, TimeSpan.FromSeconds(10));
            AddPropertyResults(values, powerResult, payloads[0]);
            var power = ReadBoolean(values, 2, 1);
            if (power == null)
            {
                _logger.LogWarning(
                    "[Xiaomi] Local power read returned no usable value. Result count: {ResultCount}; error code: {ErrorCode}.",
                    powerResult?.Result?.Count ?? 0,
                    ErrorCode(values, 2, 1));
                throw new InvalidOperationException($"The purifier did not return its power state. Error code: {ErrorCode(values, 2, 1)}.");
            }

            // Read the remaining properties in small groups. This avoids the
            // empty response seen when all model properties are sent at once.
            foreach (var chunk in payloads.Skip(1).Chunk(8))
            {
                try
                {
                    var result = await ReadPropertiesAsync(local, ip, token, chunk.ToList(), cancellationToken, TimeSpan.FromSeconds(5));
                    AddPropertyResults(values, result);
                    _logger.LogDebug("[Xiaomi] Local property batch returned {ResultCount} item(s) for {RequestedCount} requested property(ies).", result?.Result?.Count ?? 0, chunk.Length);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "[Xiaomi] Local property batch failed for {RequestedCount} requested property(ies); falling back to individual reads.", chunk.Length);
                }
            }

            // If a batch is unsupported or only partially supported, recover
            // the missing readings one at a time. Optional properties may still
            // remain unavailable, but they must not invalidate power control.
            foreach (var payload in payloads.Skip(1))
            {
                if (values.TryGetValue((payload.Siid, payload.Piid), out var existing) && existing.Code == 0) continue;
                try
                {
                    var result = await ReadPropertyAsync(local, ip, token, payload, cancellationToken, TimeSpan.FromSeconds(3));
                    AddPropertyResults(values, result, payload);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "[Xiaomi] Optional local property {Siid}/{Piid} could not be read.", payload.Siid, payload.Piid);
                }
            }

            lock (_lock)
            {
                _settings.LocalValidated = true;
                SaveSettingsLocked();
                _snapshot.Power = power.Value;
                _snapshot.Mode = ReadInt(values, 2, 4);
                _snapshot.FanLevel = ReadInt(values, 2, 5);
                _snapshot.Plasma = ReadBoolean(values, 2, 6);
                _snapshot.Uv = ReadBoolean(values, 2, 7);
                _snapshot.Fault = ReadInt(values, 2, 2);
                _snapshot.Humidity = ReadInt(values, 3, 1);
                _snapshot.Pm25 = ReadDouble(values, 3, 4);
                _snapshot.Temperature = ReadDouble(values, 3, 7);
                _snapshot.Pm10 = ReadDouble(values, 3, 8);
                _snapshot.AirQuality = ReadInt(values, 3, 9);
                _snapshot.FilterLife = ReadInt(values, 4, 1);
                _snapshot.FilterUsedHours = ReadInt(values, 4, 3);
                _snapshot.Alarm = ReadBoolean(values, 6, 1);
                _snapshot.ChildLock = ReadBoolean(values, 8, 1);
                _snapshot.ScreenBrightness = ReadInt(values, 13, 2);
                _snapshot.FavoriteLevel = ReadInt(values, 14, 1);
                _snapshot.TemperatureDisplayUnit = ReadInt(values, 15, 1);
                _snapshot.MotorRpm = ReadInt(values, 9, 1);
                _snapshot.RebootCause = ReadInt(values, 9, 8);
                _snapshot.IicErrorCount = ReadInt(values, 9, 10);
                _snapshot.CountryCode = ReadInt(values, 9, 11);
                _snapshot.FavoriteSquare = ReadString(values, 9, 12);
                _snapshot.FilterTag = ReadString(values, 12, 1);
                _snapshot.FilterFactoryId = ReadString(values, 12, 2);
                _snapshot.FilterProductId = ReadString(values, 12, 3);
                _snapshot.FilterManufacturedAt = ReadString(values, 12, 4);
                _snapshot.FilterSerialNumber = ReadString(values, 12, 5);
                _snapshot.AqiUpdateHeartbeat = ReadInt(values, 11, 4);
                _snapshot.LastLocalContact = DateTimeOffset.Now;
                _snapshot.Message = $"Local status refreshed. Purifier is currently {(power.Value ? "on" : "off")}.";
                return BuildStatusLocked();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[Xiaomi] Local purifier status refresh failed.");
            lock (_lock)
            {
                _settings.LocalValidated = false;
                DisableAutomationLocked("LAN status refresh failed: " + CleanMessage(ex.Message));
                SaveSettingsLocked();
                _snapshot.Message = "LAN test failed: " + CleanMessage(ex.Message);
            }
            throw new InvalidOperationException(GetStatus().Message, ex);
        }
        finally { _localLock.Release(); }
    }

    private static async Task<GetPropertiesResult?> ReadPropertyAsync(
        IMiotLocal local,
        string ip,
        string token,
        GetPropertyPayload payload,
        CancellationToken cancellationToken,
        TimeSpan timeout) =>
        await Task.Run(() => local.GetPropertyAsync(ip, token, payload), cancellationToken)
            .WaitAsync(timeout, cancellationToken);

    private static async Task<GetPropertiesResult?> ReadPropertiesAsync(
        IMiotLocal local,
        string ip,
        string token,
        List<GetPropertyPayload> payloads,
        CancellationToken cancellationToken,
        TimeSpan timeout) =>
        await Task.Run(() => local.GetPropertiesAsync(ip, token, payloads), cancellationToken)
            .WaitAsync(timeout, cancellationToken);

    private static void AddPropertyResults(
        Dictionary<(int Siid, int Piid), GetPropertiesResultItem> values,
        GetPropertiesResult? result,
        GetPropertyPayload? requestedPayload = null)
    {
        var items = result?.Result ?? new List<GetPropertiesResultItem>();
        foreach (var item in items.Where(item => item != null))
            values[(item.Siid, item.Piid)] = item;

        // MiHome.Net normally echoes siid/piid in the response. Preserve the
        // requested key for a single-property response if a device omits them.
        var first = items.FirstOrDefault();
        if (requestedPayload != null && first != null && !values.ContainsKey((requestedPayload.Siid, requestedPayload.Piid)))
            values[(requestedPayload.Siid, requestedPayload.Piid)] = first;
    }

    public Task<XiaomiPurifierStatusDto> TestLocalAsync(CancellationToken cancellationToken) => RefreshAsync(cancellationToken);

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
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not InvalidOperationException)
        {
            throw new InvalidOperationException("Local purifier command failed: " + CleanMessage(ex.Message), ex);
        }
        finally { _localLock.Release(); }

        return await RefreshAfterCommandAsync(
            $"Purifier turned {(power ? "on" : "off")} over the local network.",
            cancellationToken);
    }

    public async Task<XiaomiPurifierStatusDto> SetControlAsync(XiaomiControlRequest request, CancellationToken cancellationToken)
    {
        (int siid, int piid, object value, Action<XiaomiPurifierSnapshot> update) = ResolveControl(request);
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
            var result = await Task.Run(() => local.SetPropertyAsync(ip, token, new SetPropertyPayload { Siid = siid, Piid = piid, Value = value }), cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            var item = result?.Result?.FirstOrDefault();
            if (item == null || item.Code != 0)
                throw new InvalidOperationException($"The purifier returned error code {item?.Code.ToString() ?? "none"}.");

            lock (_lock)
            {
                update(_snapshot);
                _snapshot.LastLocalContact = DateTimeOffset.Now;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not InvalidOperationException)
        {
            throw new InvalidOperationException("Local purifier command failed: " + CleanMessage(ex.Message), ex);
        }
        finally { _localLock.Release(); }

        return await RefreshAfterCommandAsync(
            $"Purifier {request.Control} updated over the local network.",
            cancellationToken);
    }

    private async Task<XiaomiPurifierStatusDto> RefreshAfterCommandAsync(string commandMessage, CancellationToken cancellationToken)
    {
        try
        {
            await RefreshAsync(cancellationToken);
            lock (_lock)
            {
                _snapshot.Message = commandMessage + " Status refreshed.";
                return BuildStatusLocked();
            }
        }
        catch (InvalidOperationException ex)
        {
            // The command already succeeded. Keep the command result visible
            // if the follow-up read is temporarily unavailable.
            lock (_lock)
            {
                _snapshot.Message = commandMessage + " Status refresh failed: " + CleanMessage(ex.Message);
                return BuildStatusLocked();
            }
        }
    }

    private static (int Siid, int Piid, object Value, Action<XiaomiPurifierSnapshot> Update) ResolveControl(XiaomiControlRequest request)
    {
        string control = (request.Control ?? string.Empty).Trim().ToLowerInvariant();
        return control switch
        {
            "mode" when request.Value is >= 0 and <= 3 => (2, 4, request.Value.Value, snapshot => snapshot.Mode = request.Value.Value),
            "fanlevel" when request.Value is >= 1 and <= 3 => (2, 5, request.Value.Value, snapshot => snapshot.FanLevel = request.Value.Value),
            "plasma" when request.Enabled.HasValue => (2, 6, request.Enabled.Value, snapshot => snapshot.Plasma = request.Enabled.Value),
            "uv" when request.Enabled.HasValue => (2, 7, request.Enabled.Value, snapshot => snapshot.Uv = request.Enabled.Value),
            "alarm" when request.Enabled.HasValue => (6, 1, request.Enabled.Value, snapshot => snapshot.Alarm = request.Enabled.Value),
            "childlock" when request.Enabled.HasValue => (8, 1, request.Enabled.Value, snapshot => snapshot.ChildLock = request.Enabled.Value),
            "brightness" when request.Value is >= 0 and <= 2 => (13, 2, request.Value.Value, snapshot => snapshot.ScreenBrightness = request.Value.Value),
            "favoritelevel" when request.Value is >= 0 and <= 14 => (14, 1, request.Value.Value, snapshot => snapshot.FavoriteLevel = request.Value.Value),
            "temperatureunit" when request.Value is 1 or 2 => (15, 1, request.Value.Value, snapshot => snapshot.TemperatureDisplayUnit = request.Value.Value),
            _ => throw new InvalidOperationException("Unsupported purifier control or value.")
        };
    }

    public XiaomiPurifierStatusDto SetAutomation(bool enabled)
    {
        lock (_lock)
        {
            if (enabled && !_settings.LocalValidated)
                throw new InvalidOperationException("Run a successful LAN test before enabling hourly presence automation.");

            if (enabled)
            {
                if (!_settings.AutomationEnabled)
                {
                    _settings.AutomationEnabled = true;
                    _settings.AutomationDisabledAt = null;
                    _settings.AutomationDisabledReason = null;
                    _logger.LogInformation("[Xiaomi] Hourly presence automation changed from disabled to enabled.");
                }
            }
            else if (_settings.AutomationEnabled)
            {
                DisableAutomationLocked("Disabled manually from the OmniGate UI.");
            }

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
                _logger.LogInformation(
                    "[Xiaomi] Scheduled presence result was {Presence}; automation is disabled. Reason: {Reason}",
                    isHome ? "home" : "away",
                    _settings.AutomationDisabledReason ?? "No disable reason was recorded.");
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
        QrReady = _loginRunning && FindQrCodePath() is not null,
        LocalValidated = _settings.LocalValidated,
        AutomationEnabled = _settings.AutomationEnabled,
        Power = _snapshot.Power,
        Mode = _snapshot.Mode,
        FanLevel = _snapshot.FanLevel,
        Plasma = _snapshot.Plasma,
        Uv = _snapshot.Uv,
        Fault = _snapshot.Fault,
        Humidity = _snapshot.Humidity,
        Pm25 = _snapshot.Pm25,
        Temperature = _snapshot.Temperature,
        Pm10 = _snapshot.Pm10,
        AirQuality = _snapshot.AirQuality,
        FilterLife = _snapshot.FilterLife,
        FilterUsedHours = _snapshot.FilterUsedHours,
        Alarm = _snapshot.Alarm,
        ChildLock = _snapshot.ChildLock,
        ScreenBrightness = _snapshot.ScreenBrightness,
        FavoriteLevel = _snapshot.FavoriteLevel,
        TemperatureDisplayUnit = _snapshot.TemperatureDisplayUnit,
        MotorRpm = _snapshot.MotorRpm,
        RebootCause = _snapshot.RebootCause,
        IicErrorCount = _snapshot.IicErrorCount,
        CountryCode = _snapshot.CountryCode,
        FavoriteSquare = _snapshot.FavoriteSquare,
        AqiUpdateHeartbeat = _snapshot.AqiUpdateHeartbeat,
        FilterTag = _snapshot.FilterTag,
        FilterFactoryId = _snapshot.FilterFactoryId,
        FilterProductId = _snapshot.FilterProductId,
        FilterManufacturedAt = _snapshot.FilterManufacturedAt,
        FilterSerialNumber = _snapshot.FilterSerialNumber,
        LastLocalContact = _snapshot.LastLocalContact,
        AutomationDisabledAt = _settings.AutomationDisabledAt,
        AutomationDisabledReason = _settings.AutomationDisabledReason,
        LastAutomationAt = _settings.LastAutomationAt,
        LastAutomationResult = _settings.LastAutomationResult,
        Message = _snapshot.Message
    };

    private bool HasTokenLocked() => File.Exists(_tokenPath);

    private void DisableAutomationLocked(string reason, bool replaceExistingReason = false)
    {
        bool wasEnabled = _settings.AutomationEnabled;
        if (!wasEnabled && !replaceExistingReason && !string.IsNullOrWhiteSpace(_settings.AutomationDisabledReason))
            return;

        _settings.AutomationEnabled = false;
        _settings.AutomationDisabledAt = DateTimeOffset.Now;
        _settings.AutomationDisabledReason = reason;

        if (wasEnabled)
        {
            _logger.LogWarning(
                "[Xiaomi] Hourly presence automation changed from enabled to disabled. Reason: {Reason}",
                reason);
        }
    }

    private void SetMessage(string message)
    {
        lock (_lock) _snapshot.Message = message;
    }

    private string? FindAuthPath()
    {
        foreach (string path in AuthFileCandidates())
        {
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private void TryDeleteAuthFiles()
    {
        foreach (string path in AuthFileCandidates()) TryDelete(path);
    }

    private IEnumerable<string> AuthFileCandidates()
    {
        yield return _authPath;

        string? stateDirectory = Path.GetDirectoryName(_tokenPath);
        if (!string.IsNullOrWhiteSpace(stateDirectory))
            yield return Path.Combine(stateDirectory, "auth.json");

        yield return Path.Combine(Directory.GetCurrentDirectory(), "auth.json");
    }

    private string? FindQrCodePath()
    {
        foreach (string path in new[]
        {
            _configuredQrPath,
            Path.Combine(_qrDirectory, "qr.png"),
            Path.Combine(AppContext.BaseDirectory, "output", "qr.png"),
            Path.Combine(Directory.GetCurrentDirectory(), "output", "qr.png")
        }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private void TryDeleteQrCodes()
    {
        foreach (string path in new[]
        {
            _configuredQrPath,
            Path.Combine(AppContext.BaseDirectory, "output", "qr.png"),
            Path.Combine(Directory.GetCurrentDirectory(), "output", "qr.png")
        }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            TryDelete(path);
        }
    }

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

    private static bool? ReadBoolean(Dictionary<(int Siid, int Piid), GetPropertiesResultItem> values, int siid, int piid)
    {
        object? value = ReadValue(values, siid, piid);
        if (value == null) return null;
        try { return ReadBoolean(value); }
        catch { return null; }
    }

    private static int? ReadInt(Dictionary<(int Siid, int Piid), GetPropertiesResultItem> values, int siid, int piid)
    {
        object? value = ReadValue(values, siid, piid);
        if (value == null) return null;
        return int.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), out int result) ? result : null;
    }

    private static double? ReadDouble(Dictionary<(int Siid, int Piid), GetPropertiesResultItem> values, int siid, int piid)
    {
        object? value = ReadValue(values, siid, piid);
        if (value == null) return null;
        return double.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double result) ? result : null;
    }

    private static string? ReadString(Dictionary<(int Siid, int Piid), GetPropertiesResultItem> values, int siid, int piid) =>
        Convert.ToString(ReadValue(values, siid, piid), System.Globalization.CultureInfo.InvariantCulture);

    private static object? ReadValue(Dictionary<(int Siid, int Piid), GetPropertiesResultItem> values, int siid, int piid) =>
        values.TryGetValue((siid, piid), out var item) && item.Code == 0 ? item.Value : null;

    private static string ErrorCode(Dictionary<(int Siid, int Piid), GetPropertiesResultItem> values, int siid, int piid) =>
        values.TryGetValue((siid, piid), out var item) ? item.Code.ToString(System.Globalization.CultureInfo.InvariantCulture) : "missing";

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
        public DateTimeOffset? AutomationDisabledAt { get; set; }
        public string? AutomationDisabledReason { get; set; }
        public DateTimeOffset? LastAutomationAt { get; set; }
        public string? LastAutomationResult { get; set; }
    }

    private sealed class XiaomiPurifierSnapshot
    {
        public bool? Power { get; set; }
        public int? Mode { get; set; }
        public int? FanLevel { get; set; }
        public bool? Plasma { get; set; }
        public bool? Uv { get; set; }
        public int? Fault { get; set; }
        public int? Humidity { get; set; }
        public double? Pm25 { get; set; }
        public double? Temperature { get; set; }
        public double? Pm10 { get; set; }
        public int? AirQuality { get; set; }
        public int? FilterLife { get; set; }
        public int? FilterUsedHours { get; set; }
        public bool? Alarm { get; set; }
        public bool? ChildLock { get; set; }
        public int? ScreenBrightness { get; set; }
        public int? FavoriteLevel { get; set; }
        public int? TemperatureDisplayUnit { get; set; }
        public int? MotorRpm { get; set; }
        public int? RebootCause { get; set; }
        public int? IicErrorCount { get; set; }
        public int? CountryCode { get; set; }
        public string? FavoriteSquare { get; set; }
        public int? AqiUpdateHeartbeat { get; set; }
        public string? FilterTag { get; set; }
        public string? FilterFactoryId { get; set; }
        public string? FilterProductId { get; set; }
        public string? FilterManufacturedAt { get; set; }
        public string? FilterSerialNumber { get; set; }
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
public sealed record XiaomiControlRequest(string Control, int? Value = null, bool? Enabled = null);

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
    public DateTimeOffset? AutomationDisabledAt { get; init; }
    public string? AutomationDisabledReason { get; init; }
    public bool? Power { get; init; }
    public int? Mode { get; init; }
    public int? FanLevel { get; init; }
    public bool? Plasma { get; init; }
    public bool? Uv { get; init; }
    public int? Fault { get; init; }
    public int? Humidity { get; init; }
    public double? Pm25 { get; init; }
    public double? Temperature { get; init; }
    public double? Pm10 { get; init; }
    public int? AirQuality { get; init; }
    public int? FilterLife { get; init; }
    public int? FilterUsedHours { get; init; }
    public bool? Alarm { get; init; }
    public bool? ChildLock { get; init; }
    public int? ScreenBrightness { get; init; }
    public int? FavoriteLevel { get; init; }
    public int? TemperatureDisplayUnit { get; init; }
    public int? MotorRpm { get; init; }
    public int? RebootCause { get; init; }
    public int? IicErrorCount { get; init; }
    public int? CountryCode { get; init; }
    public string? FavoriteSquare { get; init; }
    public int? AqiUpdateHeartbeat { get; init; }
    public string? FilterTag { get; init; }
    public string? FilterFactoryId { get; init; }
    public string? FilterProductId { get; init; }
    public string? FilterManufacturedAt { get; init; }
    public string? FilterSerialNumber { get; init; }
    public DateTimeOffset? LastLocalContact { get; init; }
    public DateTimeOffset? LastAutomationAt { get; init; }
    public string? LastAutomationResult { get; init; }
    public string Message { get; init; } = string.Empty;
}
