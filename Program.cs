using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Globalization;
using HarmonyLib;

// MatterDotNet imports
using MatterEndPoint = MatterDotNet.Entities.EndPoint;
using MatterDotNet.Entities;
using MatterDotNet.OperationalDiscovery;
using MatterDotNet.PKI;
using MatterDotNet.Clusters.General;
using MatterDotNet.Protocol.Sessions;
using MatterDotNet.Protocol.Parsers;

// Windows Bluetooth imports
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

class Program
{
    static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // Apply Harmony patches to fix MatterDotNet 0.5.0 TLV bugs
        try
        {
            var harmony = new Harmony("com.omnigate.patch");
            harmony.PatchAll();
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [System] Harmony patches applied successfully for MatterDotNet.");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Error] Failed to apply runtime patches: {ex.Message}");
            Console.ResetColor();
        }

        var builder = WebApplication.CreateBuilder(args);

        // Load configuration files
        builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);

        // Configure Logging
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options =>
        {
            options.TimestampFormat = "[yyyy-MM-dd HH:mm:ss] ";
            options.SingleLine = false;
        });
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        // CORS Policy for Local Subnet/iOS Shortcut Access
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
            });
        });

        // Register Singletons
        builder.Services.AddSingleton<INameService, NameService>();
        builder.Services.AddSingleton<IHiddenService, HiddenService>();
        builder.Services.AddSingleton<ISwitchBotService, SwitchBotService>();
        builder.Services.AddSingleton<ITapoService, TapoService>();
        builder.Services.AddSingleton<IWakeOnLanService, WakeOnLanService>();

        // Set Binding Port
        string listenUrl = builder.Configuration["SwitchBot:ListenUrl"] ?? "http://0.0.0.0:5000";

        var app = builder.Build();

        app.UseCors();
        app.UseDefaultFiles();
        app.UseStaticFiles();

        // Initialize Services
        var switchBotService = app.Services.GetRequiredService<ISwitchBotService>();
        var tapoService = app.Services.GetRequiredService<ITapoService>();
        var nameService = app.Services.GetRequiredService<INameService>();

        // Start SwitchBot Queue Loop
        switchBotService.StartQueueProcessor();

        // ------------------ API ENDPOINTS ------------------

        // 1. SwitchBot: Turn ON
        app.MapPost("/api/switchbot/on", async (ISwitchBotService service) =>
        {
            service.QueueCommand(new byte[] { 0x57, 0x01, 0x01 });
            return Results.Ok(new { message = "SwitchBot Turn ON command queued." });
        });

        // 2. SwitchBot: Turn OFF
        app.MapPost("/api/switchbot/off", async (ISwitchBotService service) =>
        {
            service.QueueCommand(new byte[] { 0x57, 0x01, 0x02 });
            return Results.Ok(new { message = "SwitchBot Turn OFF command queued." });
        });

        // 3. SwitchBot: Status
        app.MapGet("/api/switchbot/status", (ISwitchBotService service) =>
        {
            return Results.Ok(service.GetStatus());
        });

        // 4. Tapo: List Nodes
        app.MapGet("/api/tapo/list", async (ITapoService service, INameService nameServ, CancellationToken cancellationToken) =>
        {
            try
            {
                var nodes = await service.ListNodesAsync(cancellationToken);
                var resultList = new List<object>();

                foreach (var node in nodes)
                {
                    string nodeName = nameServ.GetName(node.NodeId.ToString(), $"Tapo Strip ({node.NodeId})");
                    var epList = new List<object>();

                    foreach (var ep in node.Endpoints)
                    {
                        string epKey = $"{node.NodeId}_{ep.EndpointId}";
                        string epName = nameServ.GetName(epKey, $"Outlet {ep.EndpointId}");
                        epList.Add(new
                        {
                            endpointId = ep.EndpointId,
                            types = ep.Types,
                            state = ep.State,
                            customName = epName
                        });
                    }

                    resultList.Add(new
                    {
                        nodeId = node.NodeId.ToString(),
                        connectionRoot = node.ConnectionRoot,
                        customName = nodeName,
                        endpoints = epList
                    });
                }

                return Results.Ok(resultList);
            }
            catch (TapoRestartingException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception ex)
            {
                return Results.Problem($"Failed to list nodes: {ex.Message}");
            }
        });

        // 5. Tapo: Turn Outlet ON
        app.MapPost("/api/tapo/{nodeId}/{endpointId}/on", async (string nodeId, ushort endpointId, ITapoService service, CancellationToken cancellationToken) =>
        {
            if (!ulong.TryParse(nodeId, out ulong nId))
            {
                return Results.BadRequest(new { error = "Invalid Node ID." });
            }

            try
            {
                bool success = await service.ControlOutletAsync(nId, endpointId, turnOn: true, toggle: false, cancellationToken);
                if (success)
                    return Results.Ok(new { message = $"Outlet {endpointId} turned ON." });
                
                return Results.Problem($"Failed to execute command on Node {nodeId}, Endpoint {endpointId}.");
            }
            catch (InvalidOperationException ioEx)
            {
                return Results.BadRequest(new { error = ioEx.Message });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Error: {ex.Message}");
            }
        });

        // 6. Tapo: Turn Outlet OFF
        app.MapPost("/api/tapo/{nodeId}/{endpointId}/off", async (string nodeId, ushort endpointId, ITapoService service, CancellationToken cancellationToken) =>
        {
            if (!ulong.TryParse(nodeId, out ulong nId))
            {
                return Results.BadRequest(new { error = "Invalid Node ID." });
            }

            try
            {
                bool success = await service.ControlOutletAsync(nId, endpointId, turnOn: false, toggle: false, cancellationToken);
                if (success)
                    return Results.Ok(new { message = $"Outlet {endpointId} turned OFF." });

                return Results.Problem($"Failed to execute command on Node {nodeId}, Endpoint {endpointId}.");
            }
            catch (InvalidOperationException ioEx)
            {
                // Handles the Safety Lock rejection
                return Results.BadRequest(new { error = ioEx.Message });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Error: {ex.Message}");
            }
        });

        // 7. Tapo: Toggle Outlet
        app.MapPost("/api/tapo/{nodeId}/{endpointId}/toggle", async (string nodeId, ushort endpointId, ITapoService service, CancellationToken cancellationToken) =>
        {
            if (!ulong.TryParse(nodeId, out ulong nId))
            {
                return Results.BadRequest(new { error = "Invalid Node ID." });
            }

            try
            {
                bool success = await service.ControlOutletAsync(nId, endpointId, turnOn: false, toggle: true, cancellationToken);
                if (success)
                    return Results.Ok(new { message = $"Outlet {endpointId} toggled." });

                return Results.Problem($"Failed to execute command on Node {nodeId}, Endpoint {endpointId}.");
            }
            catch (InvalidOperationException ioEx)
            {
                // Handles the Safety Lock rejection
                return Results.BadRequest(new { error = ioEx.Message });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Error: {ex.Message}");
            }
        });

        // 8. Custom Naming: Get All
        app.MapGet("/api/names", (INameService service) =>
        {
            return Results.Ok(service.GetAllNames());
        });

        // 9. Custom Naming: Set Name
        app.MapPost("/api/names", (NameUpdateRequest request, INameService service) =>
        {
            if (string.IsNullOrWhiteSpace(request.Id) || request.Name == null)
            {
                return Results.BadRequest(new { error = "Invalid ID or Name." });
            }

            service.SetName(request.Id, request.Name);
            return Results.Ok(new { success = true });
        });

        // 9a. Hidden Outlets: Get All
        app.MapGet("/api/tapo/hidden", (IHiddenService service) =>
        {
            return Results.Ok(service.GetAllHidden());
        });

        // 9b. Hidden Outlets: Update State
        app.MapPost("/api/tapo/hidden", (HiddenUpdateRequest request, IHiddenService service) =>
        {
            if (string.IsNullOrWhiteSpace(request.Key))
            {
                return Results.BadRequest(new { error = "Invalid key." });
            }

            service.SetHidden(request.Key, request.Hidden);
            return Results.Ok(new { success = true });
        });

        // 10. Bluetooth Scanner
        app.MapGet("/api/bluetooth/scan", async (ISwitchBotService service) =>
        {
            var devices = await service.ScanDevicesAsync();
            return Results.Ok(devices);
        });

        // 11. Tapo: Commission Device
        app.MapPost("/api/tapo/commission", async (CommissionRequest request, ITapoService service) =>
        {
            if (string.IsNullOrWhiteSpace(request.SetupCode))
            {
                return Results.BadRequest(new { error = "Setup Code is required." });
            }

            try
            {
                var result = await service.CommissionNodeAsync(request.SetupCode, request.WifiSsid, request.WifiPassword);
                if (result.Success)
                    return Results.Ok(result);
                
                return Results.Problem(result.Message);
            }
            catch (Exception ex)
            {
                return Results.Problem($"Commissioning failed: {ex.Message}");
            }
        });

        // 12. Tapo: Remove Node
        app.MapPost("/api/tapo/remove/{nodeId}", async (string nodeId, ITapoService service) =>
        {
            if (!ulong.TryParse(nodeId, out ulong nId))
            {
                return Results.BadRequest(new { error = "Invalid Node ID." });
            }

            try
            {
                bool success = await service.RemoveNodeAsync(nId);
                if (success)
                    return Results.Ok(new { message = $"Node {nodeId} removed." });
                
                return Results.Problem($"Failed to remove Node {nodeId}.");
            }
            catch (Exception ex)
            {
                return Results.Problem($"Error: {ex.Message}");
            }
        });

        // 13. Wake on LAN API
        app.MapPost("/api/wol/wake", async (HttpContext context, IWakeOnLanService service) =>
        {
            try
            {
                WakeRequest? request = null;
                try
                {
                    if (context.Request.ContentLength is > 0)
                    {
                        request = await context.Request.ReadFromJsonAsync<WakeRequest>();
                    }
                }
                catch { /* Body is empty or not valid JSON — use server defaults */ }

                await service.WakeAsync(request?.MacAddress, request?.BroadcastIp, request?.Port);
                return Results.Ok(new { success = true, message = "Wake on LAN magic packet sent successfully." });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Server] Starting HTTP REST API Server on {listenUrl}...");
        await app.RunAsync(listenUrl);
    }

    private static void DumpMatterDotNet()
    {
        var asm = typeof(Controller).Assembly;
        Console.WriteLine("\n=== ALL STATIC FIELDS IN MatterDotNet ===");
        foreach (var type in asm.GetTypes())
        {
            var staticFields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            foreach (var f in staticFields)
            {
                if (!f.IsLiteral && !f.IsInitOnly) // Non-const, writable static fields
                {
                    Console.WriteLine($"   {type.FullName}.{f.Name} ({f.FieldType.Name})");
                }
            }
        }
    }
}

// ------------------ DTO models ------------------
public record NameUpdateRequest(string Id, string Name);
public record HiddenUpdateRequest(string Key, bool Hidden);
public record CommissionRequest(string SetupCode, string? WifiSsid = null, string? WifiPassword = null);
public record CommissionResult(bool Success, string Message, string? ConfiguredSSID = null);
public record WakeRequest(string? MacAddress = null, string? BroadcastIp = null, int? Port = null);

// ------------------ HIDDEN SERVICE ------------------
public interface IHiddenService
{
    bool IsHidden(string key);
    void SetHidden(string key, bool hidden);
    List<string> GetAllHidden();
}

public class HiddenService : IHiddenService
{
    private const string HiddenFile = "hidden.json";
    private readonly ConcurrentDictionary<string, bool> _hidden = new();
    private readonly object _fileLock = new();

    public HiddenService()
    {
        LoadHidden();
    }

    private void LoadHidden()
    {
        lock (_fileLock)
        {
            try
            {
                if (File.Exists(HiddenFile))
                {
                    string json = File.ReadAllText(HiddenFile);
                    var list = JsonSerializer.Deserialize<List<string>>(json);
                    if (list != null)
                    {
                        foreach (var key in list)
                        {
                            _hidden[key] = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Hidden] Failed to load hidden.json: {ex.Message}");
            }
        }
    }

    private void SaveHidden()
    {
        lock (_fileLock)
        {
            try
            {
                var list = _hidden.Where(kvp => kvp.Value).Select(kvp => kvp.Key).ToList();
                string json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(HiddenFile, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Hidden] Failed to save hidden.json: {ex.Message}");
            }
        }
    }

    public bool IsHidden(string key)
    {
        return _hidden.TryGetValue(key, out bool val) && val;
    }

    public void SetHidden(string key, bool hidden)
    {
        _hidden[key] = hidden;
        SaveHidden();
    }

    public List<string> GetAllHidden()
    {
        return _hidden.Where(kvp => kvp.Value).Select(kvp => kvp.Key).ToList();
    }
}

// ------------------ WAKE ON LAN SERVICE ------------------
public interface IWakeOnLanService
{
    Task WakeAsync(string? macAddress = null, string? broadcastIp = null, int? port = null);
}

public class WakeOnLanService : IWakeOnLanService
{
    private readonly IConfiguration _config;
    private readonly ILogger<WakeOnLanService> _logger;

    public WakeOnLanService(IConfiguration config, ILogger<WakeOnLanService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task WakeAsync(string? macAddress = null, string? broadcastIp = null, int? port = null)
    {
        string targetMac = macAddress ?? _config["WakeOnLan:TargetMacAddress"] ?? "00:00:00:00:00:00";
        string targetBroadcast = broadcastIp ?? _config["WakeOnLan:BroadcastIP"] ?? "255.255.255.255";
        int targetPort = port ?? _config.GetValue<int>("WakeOnLan:Port", 9);

        if (string.IsNullOrWhiteSpace(targetMac) || targetMac == "00:00:00:00:00:00")
        {
            throw new InvalidOperationException("Wake on LAN MAC address is not configured. Configure it in appsettings.json or supply it in request.");
        }

        _logger.LogInformation($"[WOL] Sending magic packet to MAC: {targetMac} via {targetBroadcast}:{targetPort}");

        string cleanMac = targetMac.Replace(":", "").Replace("-", "").Trim();
        if (cleanMac.Length != 12)
        {
            throw new ArgumentException("Invalid MAC address length. Must be 12 hexadecimal characters.");
        }

        byte[] macBytes = new byte[6];
        for (int i = 0; i < 6; i++)
        {
            macBytes[i] = byte.Parse(cleanMac.Substring(i * 2, 2), NumberStyles.HexNumber);
        }

        byte[] packet = new byte[102];
        for (int i = 0; i < 6; i++)
        {
            packet[i] = 0xFF;
        }
        for (int i = 0; i < 16; i++)
        {
            System.Buffer.BlockCopy(macBytes, 0, packet, 6 + i * 6, 6);
        }

        using var client = new UdpClient();
        client.EnableBroadcast = true;
        var endpoint = new IPEndPoint(IPAddress.Parse(targetBroadcast), targetPort);
        await client.SendAsync(packet, packet.Length, endpoint);
    }
}

// ------------------ NAMES SERVICE ------------------
public interface INameService
{
    string GetName(string key, string defaultValue);
    void SetName(string key, string name);
    Dictionary<string, string> GetAllNames();
}

public class NameService : INameService
{
    private const string NameFile = "names.json";
    private readonly ConcurrentDictionary<string, string> _names = new();
    private readonly object _fileLock = new();

    public NameService()
    {
        LoadNames();
    }

    private void LoadNames()
    {
        lock (_fileLock)
        {
            try
            {
                if (File.Exists(NameFile))
                {
                    string json = File.ReadAllText(NameFile);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (dict != null)
                    {
                        foreach (var kvp in dict)
                        {
                            _names[kvp.Key] = kvp.Value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Names] Failed to load names.json: {ex.Message}");
            }
        }
    }

    private void SaveNames()
    {
        lock (_fileLock)
        {
            try
            {
                string json = JsonSerializer.Serialize(_names, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(NameFile, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Names] Failed to save names.json: {ex.Message}");
            }
        }
    }

    public string GetName(string key, string defaultValue)
    {
        return _names.TryGetValue(key, out string? value) ? value : defaultValue;
    }

    public void SetName(string key, string name)
    {
        _names[key] = name;
        SaveNames();
    }

    public Dictionary<string, string> GetAllNames()
    {
        return new Dictionary<string, string>(_names);
    }
}

// ------------------ SWITCHBOT SERVICE ------------------
public interface ISwitchBotService
{
    void QueueCommand(byte[] command);
    void StartQueueProcessor();
    object GetStatus();
    Task<List<BluetoothDeviceDto>> ScanDevicesAsync();
}

public record BluetoothDeviceDto(string MacAddress, string Name, int Rssi);

public class SwitchBotService : ISwitchBotService
{
    private readonly IConfiguration _config;
    private readonly ILogger<SwitchBotService> _logger;
    
    private ulong _targetMacAddress;
    private string _macAddressStr = "00:00:00:00:00:00";
    
    private BluetoothLEDevice? _globalBleDevice = null;
    private GattCharacteristic? _globalWriteChar = null;
    private GattCharacteristic? _globalNotifyChar = null;
    private BluetoothLEAdvertisementWatcher? _globalWatcher = null;
    
    private readonly object _connectionLock = new();
    private Task<bool>? _ongoingConnectionTask = null;
    private bool _isExiting = false;
    private DateTime _lastCommandTime = DateTime.MinValue;
    
    private readonly Channel<byte[]> _commandChannel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    // Device Status Info
    private string _connectionState = "Disconnected";
    private int _batteryLevel = -1;
    private string _botState = "Unknown";
    private string _lastNotificationMsg = "";

    public SwitchBotService(IConfiguration config, ILogger<SwitchBotService> logger)
    {
        _config = config;
        _logger = logger;
        ConfigureDevice();
    }

    private void ConfigureDevice()
    {
        _macAddressStr = _config["SwitchBot:MacAddress"] ?? "00:00:00:00:00:00";
        _targetMacAddress = Convert.ToUInt64(_macAddressStr.Replace(":", ""), 16);
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [SwitchBot] Configured device with MAC: {_macAddressStr}");
    }

    public void QueueCommand(byte[] command)
    {
        _commandChannel.Writer.TryWrite(command);
        _logger.LogInformation("[SwitchBot] Command added to queue.");
    }

    public void StartQueueProcessor()
    {
        // Start connection pre-warm on boot
        _ = Task.Run(async () =>
        {
            _lastCommandTime = DateTime.UtcNow;
            await EnsureConnectedAsync();
            await Task.Delay(5000);
            lock (_connectionLock)
            {
                if (_commandChannel.Reader.Count == 0 && _globalBleDevice != null && _ongoingConnectionTask == null)
                {
                    _logger.LogInformation("[SwitchBot] Idle connection shutdown after boot.");
                    WipeConnection();
                }
            }
        });

        // Start queue consumer
        _ = Task.Run(async () =>
        {
            var reader = _commandChannel.Reader;
            while (await reader.WaitToReadAsync())
            {
                while (reader.TryRead(out var command))
                {
                    _logger.LogInformation("[SwitchBot] Processing queued command...");
                    bool success = await ExecuteCommandWithRetry(command);
                    _lastCommandTime = DateTime.UtcNow;
                }

                _ = Task.Run(async () =>
                {
                    await Task.Delay(10000);
                    lock (_connectionLock)
                    {
                        if (_commandChannel.Reader.Count == 0 && 
                            _globalBleDevice != null && 
                            _ongoingConnectionTask == null &&
                            (DateTime.UtcNow - _lastCommandTime).TotalSeconds >= 9.9)
                        {
                            _logger.LogInformation("[SwitchBot] 10s Idle Timeout. Disconnecting to preserve battery.");
                            WipeConnection();
                        }
                    }
                });
            }
        });
    }

    public object GetStatus()
    {
        lock (_connectionLock)
        {
            if (_globalBleDevice != null && _globalBleDevice.ConnectionStatus == BluetoothConnectionStatus.Connected)
            {
                _connectionState = "Connected";
            }
            else
            {
                _connectionState = _globalWatcher != null ? "Scanning/Warm Cache" : "Disconnected";
            }

            return new
            {
                macAddress = _macAddressStr,
                connectionState = _connectionState,
                batteryLevel = _batteryLevel,
                botState = _botState,
                lastNotification = _lastNotificationMsg
            };
        }
    }

    public async Task<List<BluetoothDeviceDto>> ScanDevicesAsync()
    {
        _logger.LogInformation("[SwitchBot] Starting Bluetooth Scan (15 seconds)...");
        var list = new ConcurrentDictionary<string, BluetoothDeviceDto>();

        var watcher = new BluetoothLEAdvertisementWatcher();
        watcher.ScanningMode = BluetoothLEScanningMode.Active;
        watcher.Received += (w, args) =>
        {
            string mac = string.Join(":", 
                Enumerable.Range(0, 6)
                    .Select(i => ((args.BluetoothAddress >> (40 - i * 8)) & 0xFF).ToString("X2"))
            );
            string name = string.IsNullOrEmpty(args.Advertisement.LocalName) ? "[Unknown]" : args.Advertisement.LocalName;
            list[mac] = new BluetoothDeviceDto(mac, name, args.RawSignalStrengthInDBm);
        };

        watcher.Start();
        await Task.Delay(15000);
        watcher.Stop();
        
        return list.Values.OrderByDescending(d => d.Rssi).ToList();
    }

    private async Task<bool> ExecuteCommandWithRetry(byte[] commandBytes)
    {
        for (int i = 0; i < 2; i++)
        {
            if (await EnsureConnectedAsync())
            {
                try
                {
                    var writeChar = _globalWriteChar;
                    if (writeChar == null)
                    {
                        throw new InvalidOperationException("Write characteristic is null.");
                    }

                    var writer = new DataWriter();
                    writer.WriteBytes(commandBytes);

                    GattWriteResult writeResult = await writeChar.WriteValueWithResultAsync(writer.DetachBuffer());

                    if (writeResult.Status == GattCommunicationStatus.Success)
                    {
                        _logger.LogInformation("[SwitchBot] Command written successfully!");
                        return true;
                    }
                    
                    _logger.LogWarning($"[SwitchBot] Command write failed: {writeResult.Status}. Forcing reconnect...");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"[SwitchBot] Error sending command: {ex.Message}. Forcing reconnect...");
                }
                
                WipeConnection();
            }
            await Task.Delay(300); 
        }
        return false;
    }

    private async Task<bool> EnsureConnectedAsync()
    {
        if (_globalBleDevice != null && _globalWriteChar != null && 
            _globalBleDevice.ConnectionStatus == BluetoothConnectionStatus.Connected)
        {
            return true; 
        }

        Task<bool> localTask;
        lock (_connectionLock)
        {
            if (_ongoingConnectionTask != null)
            {
                localTask = _ongoingConnectionTask;
            }
            else
            {
                _ongoingConnectionTask = ConnectInternalAsync();
                localTask = _ongoingConnectionTask;
            }
        }

        try
        {
            return await localTask;
        }
        finally
        {
            lock (_connectionLock)
            {
                if (_ongoingConnectionTask == localTask)
                {
                    _ongoingConnectionTask = null;
                }
            }
        }
    }

    private async Task<bool> ConnectInternalAsync()
    {
        Guid serviceUuid = new Guid("cba20d00-224d-11e6-9fb8-0002a5d5c51b");
        Guid writeCharacteristicUuid = new Guid("cba20002-224d-11e6-9fb8-0002a5d5c51b");
        Guid notifyCharacteristicUuid = new Guid("cba20003-224d-11e6-9fb8-0002a5d5c51b");

        CleanupConnectionOnly();
        StopBackgroundWatcher(); // Stop scanning background watcher to avoid RF/controller contention

        // Rapid Cached Mode
        _logger.LogInformation("[SwitchBot] Attempting rapid direct connection...");
        try
        {
            var deviceRef = await BluetoothLEDevice.FromBluetoothAddressAsync(_targetMacAddress);
            if (deviceRef != null)
            {
                var serviceResult = await deviceRef.GetGattServicesAsync(BluetoothCacheMode.Cached);
                if (serviceResult.Status == GattCommunicationStatus.Success && serviceResult.Services.Count > 0)
                {
                    GattDeviceService? targetService = serviceResult.Services.FirstOrDefault(s => s.Uuid == serviceUuid);
                    if (targetService != null)
                    {
                        var charResult = await targetService.GetCharacteristicsAsync(BluetoothCacheMode.Cached);
                        if (charResult.Status == GattCommunicationStatus.Success)
                        {
                            GattCharacteristic? writeChar = charResult.Characteristics.FirstOrDefault(c => c.Uuid == writeCharacteristicUuid);
                            GattCharacteristic? notifyChar = charResult.Characteristics.FirstOrDefault(c => c.Uuid == notifyCharacteristicUuid);

                            if (writeChar != null && notifyChar != null)
                            {
                                _globalWriteChar = writeChar;
                                _globalNotifyChar = notifyChar;
                                _globalNotifyChar.ValueChanged += OnNotificationReceived;
                                
                                var notifyStatus = await _globalNotifyChar.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
                                if (notifyStatus == GattCommunicationStatus.Success)
                                {
                                    _globalBleDevice = deviceRef;
                                    _globalBleDevice.ConnectionStatusChanged += OnConnectionStatusChanged;
                                    _logger.LogInformation("[SwitchBot] Rapid connection established!");
                                    StopBackgroundWatcher(); // Make sure background watcher is stopped since we are connected
                                    await Task.Delay(300); // Small delay to stabilize the connection
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation($"[SwitchBot] Cached path failed: {ex.Message}. Falling back to slow scan path...");
        }

        WipeConnection();

        // Slow Scan Mode
        _logger.LogInformation("[SwitchBot] Establishing fresh BLE session (Slow Scan)...");
        try
        {
            var tcs = new TaskCompletionSource<bool>();
            var watcher = new BluetoothLEAdvertisementWatcher();
            watcher.ScanningMode = BluetoothLEScanningMode.Active;
            watcher.Received += (w, args) =>
            {
                if (args.BluetoothAddress == _targetMacAddress)
                {
                    tcs.TrySetResult(true);
                }
            };

            watcher.Start();
            var delayTask = Task.Delay(25000);
            var completedTask = await Task.WhenAny(tcs.Task, delayTask);
            watcher.Stop();

            if (completedTask != tcs.Task)
            {
                _logger.LogWarning("[SwitchBot] BLE advertisement scan timed out. Attempting fallback cache lookup...");
            }

            var connectTask = BluetoothLEDevice.FromBluetoothAddressAsync(_targetMacAddress).AsTask();
            var timeoutTask = Task.Delay(15000);
            var completedConnectTask = await Task.WhenAny(connectTask, timeoutTask);

            if (completedConnectTask == timeoutTask)
            {
                _logger.LogError("[SwitchBot] Physical connection timeout.");
                return false;
            }

            var deviceRef = await connectTask;
            if (deviceRef == null) return false;

            await Task.Delay(100);

            GattDeviceServicesResult? serviceResult = await deviceRef.GetGattServicesAsync(BluetoothCacheMode.Cached);
            if (serviceResult == null || serviceResult.Status != GattCommunicationStatus.Success || serviceResult.Services.Count == 0)
            {
                _logger.LogInformation("[SwitchBot] Services not in Windows cache. Querying device over-the-air...");
                for (int retry = 0; retry < 5; retry++)
                {
                    serviceResult = await deviceRef.GetGattServicesAsync(BluetoothCacheMode.Uncached);
                    if (serviceResult.Status == GattCommunicationStatus.Success && serviceResult.Services.Count > 0)
                    {
                        break;
                    }
                    await Task.Delay(50);
                }
            }

            if (serviceResult == null || serviceResult.Status != GattCommunicationStatus.Success || serviceResult.Services.Count == 0)
            {
                return false;
            }

            GattDeviceService? targetService = serviceResult.Services.FirstOrDefault(s => s.Uuid == serviceUuid);
            if (targetService == null) return false;

            GattCharacteristicsResult charResult = await targetService.GetCharacteristicsAsync(BluetoothCacheMode.Cached);
            if (charResult == null || charResult.Status != GattCommunicationStatus.Success || charResult.Characteristics.Count == 0)
            {
                charResult = await targetService.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            }
            _globalWriteChar = charResult.Characteristics.FirstOrDefault(c => c.Uuid == writeCharacteristicUuid);
            _globalNotifyChar = charResult.Characteristics.FirstOrDefault(c => c.Uuid == notifyCharacteristicUuid);

            if (_globalWriteChar == null || _globalNotifyChar == null) return false;

            _globalNotifyChar.ValueChanged += OnNotificationReceived;
            GattCommunicationStatus notifyStatus = await _globalNotifyChar.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
            
            _globalBleDevice = deviceRef;
            _globalBleDevice.ConnectionStatusChanged += OnConnectionStatusChanged;

            StopBackgroundWatcher();
            _logger.LogInformation("[SwitchBot] Connection established successfully.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError($"[SwitchBot] Connection failed: {ex.Message}");
            WipeConnection();
            return false;
        }
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice device, object args)
    {
        if (device.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            _logger.LogWarning("[SwitchBot] Physical link disconnected. Re-initializing cache watcher.");
            WipeConnection();
        }
    }

    private void CleanupConnectionOnly()
    {
        if (_globalNotifyChar != null)
        {
            try { _globalNotifyChar.ValueChanged -= OnNotificationReceived; } catch { }
            _globalNotifyChar = null;
        }
        _globalWriteChar = null;
        if (_globalBleDevice != null)
        {
            try { _globalBleDevice.ConnectionStatusChanged -= OnConnectionStatusChanged; } catch { }
            try { _globalBleDevice.Dispose(); } catch { }
            _globalBleDevice = null;
        }
    }

    private void WipeConnection()
    {
        CleanupConnectionOnly();
        StartBackgroundWatcher();
        GC.Collect();
    }

    private void OnNotificationReceived(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            var reader = DataReader.FromBuffer(args.CharacteristicValue);
            byte[] data = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(data);
            
            string hex = BitConverter.ToString(data).Replace("-", " ");
            
            if (data.Length >= 3)
            {
                byte statusByte = data[0];
                byte batteryByte = data[1];
                byte stateByte = data[2];

                string statusText = statusByte switch
                {
                    0x01 => "Success",
                    0x03 => "Busy",
                    0x07 => "Encrypted (Password Required)",
                    0x09 => "Wrong Password",
                    _ => $"Unknown (0x{statusByte:X2})"
                };

                _batteryLevel = batteryByte;
                
                bool isSwitchMode = (stateByte & 0x80) != 0;
                bool isOn = (stateByte & 0x10) != 0;

                _botState = isSwitchMode ? (isOn ? "ON" : "OFF") : "Pressed";
                _lastNotificationMsg = $"Status: {statusText} | Battery: {_batteryLevel}% | Mode: {(isSwitchMode ? "Switch" : "Press")} | State: {_botState}";
                
                _logger.LogInformation($"[SwitchBot Notification] {_lastNotificationMsg} [Raw: {hex}]");
            }
            else
            {
                _lastNotificationMsg = $"Raw data: {hex}";
                _logger.LogInformation($"[SwitchBot Notification] Raw: {hex}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"[SwitchBot Notification Error] {ex.Message}");
        }
    }

    private void StartBackgroundWatcher()
    {
        if (_isExiting) return;
        lock (_connectionLock)
        {
            if (_globalWatcher != null) return;
            
            bool enableBgWatcher = _config.GetValue<bool>("SwitchBot:EnableBackgroundWatcher", false);
            if (!enableBgWatcher) return;
            
            _globalWatcher = new BluetoothLEAdvertisementWatcher();
            _globalWatcher.ScanningMode = BluetoothLEScanningMode.Active;
            _globalWatcher.Received += OnWatcherReceived;
            try
            {
                _globalWatcher.Start();
                _logger.LogInformation("[SwitchBot] Background watcher started to warm cache.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"[SwitchBot] Could not start background watcher: {ex.Message}");
                _globalWatcher = null;
            }
        }
    }

    private void StopBackgroundWatcher()
    {
        lock (_connectionLock)
        {
            if (_globalWatcher != null)
            {
                try 
                {
                    _globalWatcher.Stop();
                    _globalWatcher.Received -= OnWatcherReceived;
                } 
                catch { }
                _globalWatcher = null;
                _logger.LogInformation("[SwitchBot] Background watcher stopped.");
            }
        }
    }

    private void OnWatcherReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        if (args.BluetoothAddress == _targetMacAddress)
        {
            // Advertisement cached by Windows BLE Stack
        }
    }
}

// ------------------ TAPO (MATTER) SERVICE ------------------
public interface ITapoService
{
    Task<List<TapoNodeDto>> ListNodesAsync(CancellationToken cancellationToken = default);
    Task<bool> ControlOutletAsync(ulong nodeId, ushort endpointId, bool turnOn, bool toggle, CancellationToken cancellationToken = default);
    Task<CommissionResult> CommissionNodeAsync(string setupCode, string? wifiSsid, string? wifiPassword);
    Task<bool> RemoveNodeAsync(ulong nodeId);
}

public record TapoNodeDto(ulong NodeId, string ConnectionRoot, List<TapoEndpointDto> Endpoints);
public record TapoEndpointDto(ushort EndpointId, List<string> Types, string State);
public sealed class TapoRestartingException : Exception
{
    public TapoRestartingException(string message, Exception innerException) : base(message, innerException) { }
}

public class TapoService : ITapoService
{
    private readonly IConfiguration _config;
    private readonly ILogger<TapoService> _logger;

    private readonly string _fabricFile;
    private readonly string _keyFile;
    private readonly ushort _safetyLockEndpoint;

    private Controller? _controller;
    private readonly object _controllerLock = new();

    private bool _fabricEnumerated = false;
    private readonly SemaphoreSlim _fabricLock = new SemaphoreSlim(1, 1);
    private readonly ConcurrentDictionary<ulong, SecureSession> _sessionCache = new();
    private readonly ConcurrentDictionary<ulong, DateTime> _sessionTimestamps = new();
    private readonly SemaphoreSlim _operationLock = new SemaphoreSlim(1, 1);
    private int _restartScheduled;

    // Sessions are evicted proactively before the ~1hr Matter session expiry
    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(45);
    // MatterDotNet does not expose cancellation tokens for its network calls.
    // Bound normal operations so a lost device cannot hold the singleton lock forever.
    private static readonly TimeSpan MatterOperationTimeout = TimeSpan.FromSeconds(20);

    public TapoService(IConfiguration config, ILogger<TapoService> logger)
    {
        _config = config;
        _logger = logger;

        _fabricFile = _config["Tapo:FabricFile"] ?? "fabric.bin";
        _keyFile = _config["Tapo:KeyFile"] ?? "fabric.key";
        
        // Safety lock config defaults to Endpoint 4
        if (ushort.TryParse(_config["Tapo:SafetyLockEndpoint"], out ushort ep))
        {
            _safetyLockEndpoint = ep;
        }
        else
        {
            _safetyLockEndpoint = 4;
        }

        InitializeController();
    }

    private void InitializeController()
    {
        lock (_controllerLock)
        {
            try
            {
                if (File.Exists(_fabricFile) && File.Exists(_keyFile))
                {
                    _logger.LogInformation($"[Tapo] Loading Matter fabric from {_fabricFile}...");
                    _controller = Controller.Load(_fabricFile, _keyFile);
                }
                else
                {
                    _logger.LogWarning("[Tapo] Fabric files not found. Initializing a new fabric...");
                    _controller = new Controller("My Tapo Switch Fabric");
                    _controller.Save(_fabricFile, _keyFile);
                    _logger.LogInformation("[Tapo] New fabric created and saved.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Tapo] Controller initialization failed: {ex.Message}");
            }
        }
    }

    private void ResetController()
    {
        lock (_controllerLock)
        {
            _logger.LogWarning("[Tapo] Resetting Matter controller and clearing transport sockets...");
            _sessionCache.Clear();
            _sessionTimestamps.Clear();
            _fabricEnumerated = false;

            try
            {
                var smType = typeof(Controller).Assembly.GetType("MatterDotNet.Protocol.Sessions.SessionManager");
                if (smType != null)
                {
                    var connField = smType.GetField("connections", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (connField?.GetValue(null) is System.Collections.IDictionary connDict)
                    {
                        foreach (System.Collections.DictionaryEntry entry in connDict)
                        {
                            if (entry.Value is IDisposable disp)
                            {
                                try { disp.Dispose(); } catch { }
                            }
                        }
                        connDict.Clear();
                    }

                    var sessField = smType.GetField("sessions", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (sessField?.GetValue(null) is System.Collections.IDictionary sessDict)
                    {
                        foreach (System.Collections.DictionaryEntry entry in sessDict)
                        {
                            if (entry.Value is System.Collections.IDictionary innerDict)
                            {
                                foreach (System.Collections.DictionaryEntry innerEntry in innerDict)
                                {
                                    if (innerEntry.Value is IDisposable disp)
                                    {
                                        try { disp.Dispose(); } catch { }
                                    }
                                }
                                innerDict.Clear();
                            }
                        }
                        sessDict.Clear();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[Tapo] Warning while purging MatterDotNet SessionManager: {ex.Message}");
            }

            try
            {
                InitializeController();
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Tapo] Re-initializing controller failed: {ex.Message}");
            }
        }
    }

    private void ScheduleApplicationRestart(Exception failure)
    {
        if (Interlocked.Exchange(ref _restartScheduled, 1) != 0) return;

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogCritical(failure, "[Tapo] MatterDotNet transport is unrecoverable. Restart OmniGate manually; automatic restart is only supported from the published .exe.");
            return;
        }

        try
        {
            var workingDirectory = Path.GetDirectoryName(executablePath)!;
            var command = $"timeout /t 2 /nobreak >nul & start \"\" /D \"{workingDirectory}\" \"{executablePath}\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = $"/d /c {command}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            _logger.LogCritical(failure, "[Tapo] MatterDotNet transport is unrecoverable after retry. OmniGate will restart in two seconds.");
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500));
                Environment.Exit(0);
            });
        }
        catch (Exception restartEx)
        {
            _logger.LogCritical(restartEx, "[Tapo] Could not schedule automatic restart after an unrecoverable MatterDotNet transport failure.");
        }
    }

    private Controller GetController()
    {
        lock (_controllerLock)
        {
            if (_controller == null)
            {
                InitializeController();
            }
            if (_controller == null)
            {
                throw new InvalidOperationException("Matter controller could not be initialized.");
            }
            return _controller;
        }
    }

    private async Task EnsureFabricEnumeratedAsync(Controller controller, CancellationToken cancellationToken)
    {
        if (_fabricEnumerated) return;

        await _fabricLock.WaitAsync(cancellationToken);
        try
        {
            if (!_fabricEnumerated)
            {
                _logger.LogInformation("[Tapo] Performing initial fabric enumeration...");
                await AwaitMatterAsync(controller.EnumerateFabric(), "fabric enumeration");
                _fabricEnumerated = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"[Tapo] Initial EnumerateFabric failed: {ex.Message}");
            throw;
        }
        finally
        {
            _fabricLock.Release();
        }
    }

    private bool IsSessionExpired(ulong nodeId)
    {
        if (_sessionTimestamps.TryGetValue(nodeId, out var created))
        {
            return DateTime.UtcNow - created > SessionTtl;
        }
        return false;
    }

    private bool HasExpiredSession()
    {
        return _sessionTimestamps.Any(entry => DateTime.UtcNow - entry.Value > SessionTtl);
    }

    private void ResetControllerIfSessionExpired()
    {
        if (!HasExpiredSession()) return;

        _logger.LogInformation("[Tapo] A cached session reached its TTL. Clearing the secure-session cache before this operation...");
        ResetController();
    }

    private async Task<T> AwaitMatterAsync<T>(Task<T> operation, string description)
    {
        await AwaitMatterAsync((Task)operation, description);
        return await operation;
    }

    private async Task AwaitMatterAsync(Task operation, string description)
    {
        try
        {
            await operation.WaitAsync(MatterOperationTimeout);
        }
        catch (TimeoutException ex)
        {
            // The library cannot cancel an in-flight UDP operation. Observe a late
            // fault while the caller resets the controller and retries once.
            _ = operation.ContinueWith(
                task => _logger.LogDebug(task.Exception, "[Tapo] Timed-out {Description} completed with an error.", description),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            throw new TimeoutException($"Matter {description} timed out after {MatterOperationTimeout.TotalSeconds:0} seconds.", ex);
        }
    }

    /// <summary>
    /// Returns true if the exception indicates a transport-level failure that
    /// requires a full controller reset (stale socket, disposed UdpClient, etc).
    /// </summary>
    private static bool IsTransportError(Exception ex)
    {
        if (ex is SocketException || ex is ObjectDisposedException || ex is IOException || ex is TimeoutException)
            return true;

        string full = ex.ToString();
        if (full.Contains("SocketException", StringComparison.OrdinalIgnoreCase)) return true;
        if (full.Contains("An invalid argument was supplied", StringComparison.OrdinalIgnoreCase)) return true;
        if (full.Contains("disposed object", StringComparison.OrdinalIgnoreCase)) return true;
        if (full.Contains("UdpClient", StringComparison.OrdinalIgnoreCase)) return true;
        if (full.Contains("System.Net.Sockets", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    private async Task<SecureSession> GetOrCreateSessionAsync(Node node, CancellationToken cancellationToken)
    {
        // Proactively evict sessions older than the TTL before the Matter device
        // closes them server-side (which yields "An invalid argument was supplied").
        if (_sessionCache.TryGetValue(node.ID, out var cachedSession))
        {
            if (!IsSessionExpired(node.ID))
            {
                return cachedSession;
            }

            // TTL expired — the Controller's shared transport is likely stale.
            // A full reset rebuilds the controller with fresh sockets.
            _logger.LogInformation($"[Tapo] Session TTL expired for Node {node.ID}. Resetting controller for fresh transport...");
            ResetController();
            cancellationToken.ThrowIfCancellationRequested();

            // Re-acquire controller and re-enumerate after reset
            var freshController = GetController();
            await EnsureFabricEnumeratedAsync(freshController, cancellationToken);
            var freshNode = freshController.GetNode(node.ID);
            if (freshNode == null)
                throw new InvalidOperationException($"Node {node.ID} not found after controller reset.");

            _logger.LogInformation($"[Tapo] Establishing Secure CASE session with Node {freshNode.ID} (Post-Reset)...");
            var freshSession = await AwaitMatterAsync(freshNode.GetCASESession(), $"CASE session setup for Node {freshNode.ID}");
            _sessionCache[freshNode.ID] = freshSession;
            _sessionTimestamps[freshNode.ID] = DateTime.UtcNow;
            return freshSession;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogInformation($"[Tapo] Establishing Secure CASE session with Node {node.ID} (Cache Miss)...");
        var session = await AwaitMatterAsync(node.GetCASESession(), $"CASE session setup for Node {node.ID}");
        _sessionCache[node.ID] = session;
        _sessionTimestamps[node.ID] = DateTime.UtcNow;
        return session;
    }

    private void InvalidateSession(ulong nodeId)
    {
        // Only remove from cache — do NOT dispose the connection socket here.
        // The MRPConnection is shared by MatterDotNet controllers; this service
        // only removes the stale cache entry and leaves transport ownership to it.
        _sessionTimestamps.TryRemove(nodeId, out _);
        if (_sessionCache.TryRemove(nodeId, out _))
        {
            _logger.LogInformation($"[Tapo] Invalidated secure session cache for Node {nodeId}.");
        }
    }

    public async Task<List<TapoNodeDto>> ListNodesAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            return await ListNodesInternalAsync(cancellationToken);
        }
        catch (Exception ex) when (IsTransportError(ex))
        {
            _logger.LogWarning($"[Tapo] Socket error detected in ListNodesAsync ({ex.Message}). Resetting controller and retrying...");
            ResetController();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await ListNodesInternalAsync(cancellationToken);
            }
            catch (Exception retryEx) when (IsTransportError(retryEx))
            {
                ScheduleApplicationRestart(retryEx);
                throw new TapoRestartingException("Matter transport could not recover and OmniGate is restarting.", retryEx);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[Tapo] ListNodesAsync was cancelled by browser refresh.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError($"[Tapo] Critical error in ListNodesAsync: {ex.Message}");
            return new List<TapoNodeDto>();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<List<TapoNodeDto>> ListNodesInternalAsync(CancellationToken cancellationToken)
    {
        var nodesList = new List<TapoNodeDto>();
        cancellationToken.ThrowIfCancellationRequested();
        ResetControllerIfSessionExpired();
        var controller = GetController();
        await EnsureFabricEnumeratedAsync(controller, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var node in controller.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SecureSession? session = null;
            try
            {
                session = await GetOrCreateSessionAsync(node, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsTransportError(ex))
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[Tapo] CASE session failed (cached path): {ex.Message}. Retrying...");
                InvalidateSession(node.ID);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    session = await GetOrCreateSessionAsync(node, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception retryEx) when (IsTransportError(retryEx))
                {
                    throw;
                }
                catch (Exception retryEx)
                {
                    _logger.LogError($"[Tapo] Node {node.ID} CASE session retry failed: {retryEx.Message}");
                }
            }

            var endpoints = new List<MatterEndPoint>();
            if (node.Root != null)
            {
                GetEndpointsRecursive(node.Root, endpoints);
            }

            var epDtos = new List<TapoEndpointDto>();

            foreach (var ep in endpoints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ep == null) continue;
                ushort epIdx = GetEndpointIndex(ep);
                if (epIdx == 0) continue;

                string stateStr = "UNKNOWN";
                if (session != null)
                {
                    try
                    {
                        var onOff = new On_Off(epIdx);
                        var stateVal = await AwaitMatterAsync(onOff.GetOnOff(session), $"state read for Node {node.ID}, Endpoint {epIdx}");
                        stateStr = stateVal ? "ON" : "OFF";
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex) when (IsTransportError(ex))
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"[Tapo] State read failed for Node {node.ID}, Endpoint {epIdx}: {ex.Message}");
                        InvalidateSession(node.ID);
                    }
                }

                epDtos.Add(new TapoEndpointDto(
                    epIdx, 
                    ep.DeviceTypes?.Select(t => t.ToString()).ToList() ?? new List<string>(), 
                    stateStr
                ));
            }

            nodesList.Add(new TapoNodeDto(
                node.ID,
                node.Root?.ToString() ?? "Unknown",
                epDtos
            ));
        }

        return nodesList;
    }

    public async Task<bool> ControlOutletAsync(ulong nodeId, ushort endpointId, bool turnOn, bool toggle, CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            return await ControlOutletInternalAsync(nodeId, endpointId, turnOn, toggle, cancellationToken);
        }
        catch (Exception ex) when (IsTransportError(ex))
        {
            _logger.LogWarning($"[Tapo] Socket error detected in ControlOutletAsync ({ex.Message}). Resetting controller and retrying...");
            ResetController();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await ControlOutletInternalAsync(nodeId, endpointId, turnOn, toggle, cancellationToken);
            }
            catch (Exception retryEx) when (IsTransportError(retryEx))
            {
                ScheduleApplicationRestart(retryEx);
                throw new TapoRestartingException("Matter transport could not recover and OmniGate is restarting.", retryEx);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation($"[Tapo] ControlOutletAsync was cancelled by browser refresh.");
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<bool> ExecuteOnOffCommandAsync(On_Off onOff, SecureSession session, bool turnOn, bool toggle, ulong nodeId, ushort endpointId)
    {
        Task<bool> operation;
        if (toggle)
        {
            operation = onOff.Toggle(session);
        }
        else if (turnOn)
        {
            operation = onOff.On(session);
        }
        else
        {
            operation = onOff.Off(session);
        }

        return await AwaitMatterAsync(operation, $"command for Node {nodeId}, Endpoint {endpointId}");
    }

    private async Task<bool> ControlOutletInternalAsync(ulong nodeId, ushort endpointId, bool turnOn, bool toggle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResetControllerIfSessionExpired();
        var controller = GetController();
        _logger.LogInformation($"[Tapo] Searching Node {nodeId} in fabric...");
        await EnsureFabricEnumeratedAsync(controller, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var node = controller.GetNode(nodeId);
        if (node == null)
        {
            throw new ArgumentException($"Node {nodeId} not found in the fabric.");
        }

        SecureSession session;
        try
        {
            session = await GetOrCreateSessionAsync(node, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"[Tapo] Cached session retrieval failed for Node {nodeId}: {ex.Message}. Retrying with fresh session...");
            InvalidateSession(nodeId);
            cancellationToken.ThrowIfCancellationRequested();
            session = await GetOrCreateSessionAsync(node, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var ep = FindEndPoint(node.Root, endpointId);
        if (ep == null)
        {
            _logger.LogWarning($"[Tapo] Endpoint {endpointId} not found in discovery. Attempting direct control anyway...");
        }

        var onOff = new On_Off(endpointId);
        bool success = false;
        string actionStr = toggle ? "Toggle" : (turnOn ? "ON" : "OFF");
        
        _logger.LogInformation($"[Tapo] Sending command '{actionStr}' to Node {nodeId}, Endpoint {endpointId}...");

        try
        {
            success = await ExecuteOnOffCommandAsync(onOff, session, turnOn, toggle, nodeId, endpointId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception cmdEx) when (IsTransportError(cmdEx))
        {
            throw;
        }
        catch (Exception cmdEx)
        {
            _logger.LogWarning($"[Tapo] Command execution failed: {cmdEx.Message}. Invalidating session and retrying...");
            InvalidateSession(nodeId);
            cancellationToken.ThrowIfCancellationRequested();
            session = await GetOrCreateSessionAsync(node, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            success = await ExecuteOnOffCommandAsync(onOff, session, turnOn, toggle, nodeId, endpointId);
        }

        return success;
    }

    public async Task<CommissionResult> CommissionNodeAsync(string setupCode, string? wifiSsid, string? wifiPassword)
    {
        await _operationLock.WaitAsync();
        try
        {
            return await CommissionNodeInternalAsync(setupCode, wifiSsid, wifiPassword);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<CommissionResult> CommissionNodeInternalAsync(string setupCode, string? wifiSsid, string? wifiPassword)
    {
        var controller = GetController();
        _logger.LogInformation($"[Tapo] Parsing Setup Code '{setupCode}'...");

        CommissioningPayload payload;
        if (setupCode.StartsWith("MT:", StringComparison.OrdinalIgnoreCase))
        {
            payload = CommissioningPayload.FromQR(setupCode);
        }
        else
        {
            payload = CommissioningPayload.FromPIN(setupCode);
        }

        _logger.LogInformation($"[Tapo] Starting Commissioning for Vendor: {payload.VendorID}, Product: {payload.ProductID}...");
        
        CommissioningState state = await AwaitMatterAsync(controller.StartCommissioning(payload, "", VerificationLevel.AnyDevice), "commissioning start");
        _logger.LogInformation("[Tapo] Operational Discovery handshake complete.");

        if (state.WiFiNetworks != null && state.WiFiNetworks.Length > 0)
        {
            _logger.LogInformation("[Tapo] WiFi provisioning required on device.");
            
            // If Wi-Fi credentials were provided in the API call
            if (!string.IsNullOrEmpty(wifiSsid))
            {
                var targetWifi = state.WiFiNetworks.FirstOrDefault(net => Encoding.UTF8.GetString(net.SSID).Equals(wifiSsid, StringComparison.OrdinalIgnoreCase));
                if (targetWifi == null)
                {
                    // Fallback to index 0 if specific SSID not found
                    targetWifi = state.WiFiNetworks[0];
                }

                string ssidStr = Encoding.UTF8.GetString(targetWifi.SSID);
                _logger.LogInformation($"[Tapo] Provisioning credentials to WiFi: {ssidStr}");
                await AwaitMatterAsync(controller.CompleteCommissioning(state, targetWifi, wifiPassword ?? ""), "commissioning completion");
                
                controller.Save(_fabricFile, _keyFile);
                _fabricEnumerated = false;
                return new CommissionResult(true, "Commissioning complete with WiFi configuration.", ssidStr);
            }
            else
            {
                // Wi-Fi was needed but credentials were not supplied in the API call. Return list of scanned networks.
                WipeCommissioningProgress(controller, state);
                var ssidsList = state.WiFiNetworks.Select(n => Encoding.UTF8.GetString(n.SSID)).ToList();
                string ssidsCsv = string.Join(", ", ssidsList);
                return new CommissionResult(false, $"Device requires WiFi configuration. Available networks: {ssidsCsv}", null);
            }
        }
        else
        {
            _logger.LogInformation("[Tapo] Device already on local IP. Completing commissioning...");
            await AwaitMatterAsync(controller.CompleteCommissioning(state), "commissioning completion");
            
            controller.Save(_fabricFile, _keyFile);
            _fabricEnumerated = false;
            return new CommissionResult(true, "Commissioning complete.", null);
        }
    }

    public async Task<bool> RemoveNodeAsync(ulong nodeId)
    {
        await _operationLock.WaitAsync();
        try
        {
            return await RemoveNodeInternalAsync(nodeId);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<bool> RemoveNodeInternalAsync(ulong nodeId)
    {
        var controller = GetController();
        var node = controller.GetNode(nodeId);
        if (node == null)
        {
            return false;
        }

        _logger.LogInformation($"[Tapo] Removing Node {nodeId} from fabric...");
        await AwaitMatterAsync(controller.RemoveNode(node), $"removal of Node {nodeId}");
        InvalidateSession(nodeId);
        controller.Save(_fabricFile, _keyFile);
        _fabricEnumerated = false;
        return true;
    }

    private void WipeCommissioningProgress(Controller controller, CommissioningState state)
    {
        // Cancel/reset state if incomplete
        try
        {
            // MatterDotNet does not expose an explicit abort API; a full reset is
            // the safest way to discard incomplete commissioning state.
            ResetController();
        }
        catch { }
    }

    // Helper methods
    private void GetEndpointsRecursive(MatterEndPoint current, List<MatterEndPoint> list)
    {
        list.Add(current);
        foreach (var child in current.Children)
        {
            GetEndpointsRecursive(child, list);
        }
    }

    private MatterEndPoint? FindEndPoint(MatterEndPoint current, ushort targetIndex)
    {
        if (GetEndpointIndex(current) == targetIndex) return current;
        foreach (var child in current.Children)
        {
            var found = FindEndPoint(child, targetIndex);
            if (found != null) return found;
        }
        return null;
    }

    private ushort GetEndpointIndex(MatterEndPoint endpoint)
    {
        var field = typeof(MatterEndPoint).GetField("index", BindingFlags.NonPublic | BindingFlags.Instance);
        var val = field?.GetValue(endpoint);
        return val is ushort u ? u : (ushort)0;
    }
}

// ------------------ MATTERDOTNET HARMONY PATCHES ------------------
[HarmonyPatch]
public static class TLVReaderPatches
{
    private static FieldInfo? typeField;
    private static MethodInfo? isTagMethod;

    static TLVReaderPatches()
    {
        try
        {
            var assembly = Assembly.Load("MatterDotNet");
            var readerType = assembly.GetType("MatterDotNet.Protocol.Parsers.TLVReader");
            if (readerType != null)
            {
                typeField = readerType.GetField("type", BindingFlags.NonPublic | BindingFlags.Instance);
                isTagMethod = readerType.GetMethod("IsTag", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(long) }, null);
            }
        }
        catch { }
    }

    private static bool CheckTag(TLVReader instance, long tagNumber, bool nullable, out bool shouldSkip)
    {
        shouldSkip = false;
        if (isTagMethod == null) return true;

        try
        {
            bool isTag = (bool)isTagMethod.Invoke(instance, new object[] { tagNumber })!;
            if (!isTag)
            {
                if (nullable)
                {
                    shouldSkip = true;
                    return false;
                }
            }
        }
        catch { }
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(TLVReader), "GetUShort", new[] { typeof(long), typeof(bool) })]
    public static bool Prefix_GetUShort(TLVReader __instance, long tagNumber, bool nullable, ref ushort? __result)
    {
        if (CheckTag(__instance, tagNumber, nullable, out bool shouldSkip)) return true;
        if (shouldSkip) { __result = null; return false; }
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(TLVReader), "GetUInt", new[] { typeof(long), typeof(bool) })]
    public static bool Prefix_GetUInt(TLVReader __instance, long tagNumber, bool nullable, ref uint? __result)
    {
        if (CheckTag(__instance, tagNumber, nullable, out bool shouldSkip)) return true;
        if (shouldSkip) { __result = null; return false; }
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(TLVReader), "GetULong", new[] { typeof(long), typeof(bool) })]
    public static bool Prefix_GetULong(TLVReader __instance, long tagNumber, bool nullable, ref ulong? __result)
    {
        if (CheckTag(__instance, tagNumber, nullable, out bool shouldSkip)) return true;
        if (shouldSkip) { __result = null; return false; }
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(TLVReader), "GetBool", new[] { typeof(long), typeof(bool) })]
    public static bool Prefix_GetBool(TLVReader __instance, long tagNumber, bool nullable, ref bool? __result)
    {
        if (CheckTag(__instance, tagNumber, nullable, out bool shouldSkip)) return true;
        if (shouldSkip) { __result = null; return false; }
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(TLVReader), "GetByte", new[] { typeof(long), typeof(bool) })]
    public static bool Prefix_GetByte(TLVReader __instance, long tagNumber, bool nullable, ref byte? __result)
    {
        if (CheckTag(__instance, tagNumber, nullable, out bool shouldSkip)) return true;
        if (shouldSkip) { __result = null; return false; }
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(TLVReader), "GetString", new[] { typeof(long), typeof(bool), typeof(int), typeof(int) })]
    public static bool Prefix_GetString(TLVReader __instance, long tagNumber, bool nullable, ref string? __result)
    {
        if (CheckTag(__instance, tagNumber, nullable, out bool shouldSkip)) return true;
        if (shouldSkip) { __result = null; return false; }
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(TLVReader), "GetBytes", new[] { typeof(long), typeof(bool), typeof(int), typeof(int) })]
    public static bool Prefix_GetBytes(TLVReader __instance, long tagNumber, bool nullable, ref byte[]? __result)
    {
        if (CheckTag(__instance, tagNumber, nullable, out bool shouldSkip)) return true;
        if (shouldSkip) { __result = null; return false; }
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(TLVReader), "GetAny", new[] { typeof(long), typeof(bool) })]
    public static bool Prefix_GetAny(TLVReader __instance, long tagNumber, bool nullable, ref object? __result)
    {
        if (typeField == null || isTagMethod == null) return true;

        try
        {
            bool isTag = (bool)isTagMethod.Invoke(__instance, new object[] { tagNumber })!;
            if (!isTag)
            {
                if (nullable)
                {
                    __result = null;
                    return false;
                }
                return true;
            }

            var elementType = typeField.GetValue(__instance);
            if (elementType == null) return true;

            int typeVal = (int)elementType;

            if (typeVal == 8 || typeVal == 9)
            {
                __result = __instance.GetBool(tagNumber, nullable);
                return false;
            }
        }
        catch { }

        return true;
    }
}

[HarmonyPatch]
public static class FieldReaderPatches
{
    private static FieldInfo? fieldsField;

    static FieldReaderPatches()
    {
        try
        {
            var assembly = Assembly.Load("MatterDotNet");
            var readerType = assembly.GetType("MatterDotNet.Protocol.Parsers.FieldReader");
            if (readerType != null)
            {
                fieldsField = readerType.GetField("<fields>P", BindingFlags.NonPublic | BindingFlags.Instance);
            }
        }
        catch { }
    }

    private static bool CheckFieldReaderTag(FieldReader instance, long tagNumber, bool nullable, out bool shouldSkip)
    {
        shouldSkip = false;
        if (fieldsField == null) return true;

        try
        {
            var list = fieldsField.GetValue(instance) as System.Collections.IList;
            if (list != null)
            {
                if (tagNumber >= list.Count)
                {
                    if (nullable)
                    {
                        shouldSkip = true;
                        return false;
                    }
                }
            }
        }
        catch { }
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(FieldReader), "GetByte", new[] { typeof(long), typeof(bool) })]
    public static bool Prefix_GetByte(FieldReader __instance, long tagNumber, bool nullable, ref byte? __result)
    {
        if (CheckFieldReaderTag(__instance, tagNumber, nullable, out bool shouldSkip)) return true;
        if (shouldSkip) { __result = null; return false; }
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(FieldReader), "GetSByte", new[] { typeof(long), typeof(bool) })]
    public static bool Prefix_GetSByte(FieldReader __instance, long tagNumber, bool nullable, ref sbyte? __result)
    {
        if (CheckFieldReaderTag(__instance, tagNumber, nullable, out bool shouldSkip)) return true;
        if (shouldSkip) { __result = null; return false; }
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(FieldReader), "GetBool", new[] { typeof(long), typeof(bool) })]
    public static bool Prefix_GetBool(FieldReader __instance, long tagNumber, bool nullable, ref bool? __result)
    {
        if (CheckFieldReaderTag(__instance, tagNumber, nullable, out bool shouldSkip)) return true;
        if (shouldSkip) { __result = null; return false; }
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(FieldReader), "GetShort", new[] { typeof(long), typeof(bool) })]
    public static bool Prefix_GetShort(FieldReader __instance, long tagNumber, bool nullable, ref short? __result)
    {
        if (CheckFieldReaderTag(__instance, tagNumber, nullable, out bool shouldSkip)) return true;
        if (shouldSkip) { __result = null; return false; }
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(FieldReader), "GetUShort", new[] { typeof(long), typeof(bool) })]
    public static bool Prefix_GetUShort(FieldReader __instance, long tagNumber, bool nullable, ref ushort? __result)
    {
        if (CheckFieldReaderTag(__instance, tagNumber, nullable, out bool shouldSkip)) return true;
        if (shouldSkip) { __result = null; return false; }
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(FieldReader), "GetDecimal", new[] { typeof(long), typeof(bool) })]
    public static bool Prefix_GetDecimal(FieldReader __instance, long tagNumber, bool nullable, ref decimal? __result)
    {
        if (CheckFieldReaderTag(__instance, tagNumber, nullable, out bool shouldSkip)) return true;
        if (shouldSkip) { __result = null; return false; }
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(FieldReader), "GetInt", new[] { typeof(long), typeof(bool) })]
    public static bool Prefix_GetInt(FieldReader __instance, long tagNumber, bool nullable, ref int? __result)
    {
        if (CheckFieldReaderTag(__instance, tagNumber, nullable, out bool shouldSkip)) return true;
        if (shouldSkip) { __result = null; return false; }
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(FieldReader), "GetUInt", new[] { typeof(long), typeof(bool) })]
    public static bool Prefix_GetUInt(FieldReader __instance, long tagNumber, bool nullable, ref uint? __result)
    {
        if (CheckFieldReaderTag(__instance, tagNumber, nullable, out bool shouldSkip)) return true;
        if (shouldSkip) { __result = null; return false; }
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(FieldReader), "GetLong", new[] { typeof(long), typeof(bool) })]
    public static bool Prefix_GetLong(FieldReader __instance, long tagNumber, bool nullable, ref long? __result)
    {
        if (CheckFieldReaderTag(__instance, tagNumber, nullable, out bool shouldSkip)) return true;
        if (shouldSkip) { __result = null; return false; }
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(FieldReader), "GetULong", new[] { typeof(long), typeof(bool) })]
    public static bool Prefix_GetULong(FieldReader __instance, long tagNumber, bool nullable, ref ulong? __result)
    {
        if (CheckFieldReaderTag(__instance, tagNumber, nullable, out bool shouldSkip)) return true;
        if (shouldSkip) { __result = null; return false; }
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(FieldReader), "GetFloat", new[] { typeof(long), typeof(bool) })]
    public static bool Prefix_GetFloat(FieldReader __instance, long tagNumber, bool nullable, ref float? __result)
    {
        if (CheckFieldReaderTag(__instance, tagNumber, nullable, out bool shouldSkip)) return true;
        if (shouldSkip) { __result = null; return false; }
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(FieldReader), "GetDouble", new[] { typeof(long), typeof(bool) })]
    public static bool Prefix_GetDouble(FieldReader __instance, long tagNumber, bool nullable, ref double? __result)
    {
        if (CheckFieldReaderTag(__instance, tagNumber, nullable, out bool shouldSkip)) return true;
        if (shouldSkip) { __result = null; return false; }
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(FieldReader), "GetString", new[] { typeof(long), typeof(bool), typeof(int), typeof(int) })]
    public static bool Prefix_GetString(FieldReader __instance, long tagNumber, bool nullable, int maxLength, int minLength, ref string? __result)
    {
        if (CheckFieldReaderTag(__instance, tagNumber, nullable, out bool shouldSkip)) return true;
        if (shouldSkip) { __result = null; return false; }
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(FieldReader), "GetBytes", new[] { typeof(long), typeof(bool), typeof(int), typeof(int) })]
    public static bool Prefix_GetBytes(FieldReader __instance, long tagNumber, bool nullable, int maxLength, int minLength, ref byte[]? __result)
    {
        if (CheckFieldReaderTag(__instance, tagNumber, nullable, out bool shouldSkip)) return true;
        if (shouldSkip) { __result = null; return false; }
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(FieldReader), "GetStruct", new[] { typeof(int), typeof(bool) })]
    public static bool Prefix_GetStruct(FieldReader __instance, int tagNumber, bool nullable, ref object[]? __result)
    {
        if (CheckFieldReaderTag(__instance, tagNumber, nullable, out bool shouldSkip)) return true;
        if (shouldSkip) { __result = null; return false; }
        return true;
    }
}
