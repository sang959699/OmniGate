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
using HarmonyLib;

// MatterDotNet imports
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
            Console.WriteLine("[System] Harmony patches applied successfully for MatterDotNet.");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[Error] Failed to apply runtime patches: {ex.Message}");
            Console.ResetColor();
        }

        var builder = WebApplication.CreateBuilder(args);

        // Load configuration files
        builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);

        // Configure Logging
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
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
        builder.Services.AddSingleton<ISwitchBotService, SwitchBotService>();
        builder.Services.AddSingleton<ITapoService, TapoService>();

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

        Console.WriteLine($"[Server] Starting HTTP REST API Server on {listenUrl}...");
        await app.RunAsync(listenUrl);
    }
}

// ------------------ DTO models ------------------
public record NameUpdateRequest(string Id, string Name);
public record CommissionRequest(string SetupCode, string? WifiSsid = null, string? WifiPassword = null);
public record CommissionResult(bool Success, string Message, string? ConfiguredSSID = null);

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
                Console.WriteLine($"[Names] Failed to load names.json: {ex.Message}");
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
                Console.WriteLine($"[Names] Failed to save names.json: {ex.Message}");
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
        Console.WriteLine($"[SwitchBot] Configured device with MAC: {_macAddressStr}");
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
    private readonly SemaphoreSlim _operationLock = new SemaphoreSlim(1, 1);

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
                await controller.EnumerateFabric();
                _fabricEnumerated = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"[Tapo] Initial EnumerateFabric failed: {ex.Message}");
        }
        finally
        {
            _fabricLock.Release();
        }
    }

    private async Task<SecureSession> GetOrCreateSessionAsync(Node node, CancellationToken cancellationToken)
    {
        if (_sessionCache.TryGetValue(node.ID, out var cachedSession))
        {
            return cachedSession;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogInformation($"[Tapo] Establishing Secure CASE session with Node {node.ID} (Cache Miss)...");
        var session = await node.GetCASESession();
        _sessionCache[node.ID] = session;
        return session;
    }

    private void InvalidateSession(ulong nodeId)
    {
        if (_sessionCache.TryRemove(nodeId, out var session))
        {
            _logger.LogInformation($"[Tapo] Invalidated secure session cache for Node {nodeId}.");
        }
    }

    public async Task<List<TapoNodeDto>> ListNodesAsync(CancellationToken cancellationToken = default)
    {
        var nodesList = new List<TapoNodeDto>();
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                catch (Exception ex)
                {
                    _logger.LogWarning($"[Tapo] Node {node.ID} CASE session failed (cached path): {ex.Message}. Retrying...");
                    InvalidateSession(node.ID);
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        session = await GetOrCreateSessionAsync(node, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception retryEx)
                    {
                        _logger.LogError($"[Tapo] Node {node.ID} CASE session retry failed: {retryEx.Message}");
                    }
                }

                var endpoints = new List<EndPoint>();
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

                    string stateStr = "Unknown";
                    if (ep.HasCluster<On_Off>())
                    {
                        var onOff = ep.GetCluster<On_Off>();
                        if (session != null)
                        {
                            try
                            {
                                bool stateVal = await onOff.GetOnOff(session);
                                stateStr = stateVal ? "ON" : "OFF";
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                stateStr = $"Offline ({ex.Message})";
                            }
                        }
                        else
                        {
                            stateStr = "Session Offline";
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
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[Tapo] ListNodesAsync was cancelled by browser refresh.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError($"[Tapo] Critical error in ListNodesAsync: {ex.Message}");
        }
        finally
        {
            _operationLock.Release();
        }

        return nodesList;
    }

    public async Task<bool> ControlOutletAsync(ulong nodeId, ushort endpointId, bool turnOn, bool toggle, CancellationToken cancellationToken = default)
    {

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                if (toggle)
                {
                    success = await onOff.Toggle(session);
                }
                else if (turnOn)
                {
                    success = await onOff.On(session);
                }
                else
                {
                    success = await onOff.Off(session);
                }
            }
            catch (OperationCanceledException)
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
                
                if (toggle)
                {
                    success = await onOff.Toggle(session);
                }
                else if (turnOn)
                {
                    success = await onOff.On(session);
                }
                else
                {
                    success = await onOff.Off(session);
                }
            }

            return success;
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

    public async Task<CommissionResult> CommissionNodeAsync(string setupCode, string? wifiSsid, string? wifiPassword)
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
        
        CommissioningState state = await controller.StartCommissioning(payload, "", VerificationLevel.AnyDevice);
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
                await controller.CompleteCommissioning(state, targetWifi, wifiPassword ?? "");
                
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
            await controller.CompleteCommissioning(state);
            
            controller.Save(_fabricFile, _keyFile);
            _fabricEnumerated = false;
            return new CommissionResult(true, "Commissioning complete.", null);
        }
    }

    public async Task<bool> RemoveNodeAsync(ulong nodeId)
    {
        var controller = GetController();
        var node = controller.GetNode(nodeId);
        if (node == null)
        {
            return false;
        }

        _logger.LogInformation($"[Tapo] Removing Node {nodeId} from fabric...");
        await controller.RemoveNode(node);
        controller.Save(_fabricFile, _keyFile);
        _fabricEnumerated = false;
        return true;
    }

    private void WipeCommissioningProgress(Controller controller, CommissioningState state)
    {
        // Cancel/reset state if incomplete
        try
        {
            // Note: MatterDotNet controller manages intermediate states. Re-loading works as a fallback reset.
            InitializeController();
        }
        catch { }
    }

    // Helper methods
    private void GetEndpointsRecursive(EndPoint current, List<EndPoint> list)
    {
        list.Add(current);
        foreach (var child in current.Children)
        {
            GetEndpointsRecursive(child, list);
        }
    }

    private EndPoint? FindEndPoint(EndPoint current, ushort targetIndex)
    {
        if (GetEndpointIndex(current) == targetIndex) return current;
        foreach (var child in current.Children)
        {
            var found = FindEndPoint(child, targetIndex);
            if (found != null) return found;
        }
        return null;
    }

    private ushort GetEndpointIndex(EndPoint endpoint)
    {
        var field = typeof(EndPoint).GetField("index", BindingFlags.NonPublic | BindingFlags.Instance);
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
