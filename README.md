# OmniGate 🎛️

OmniGate is a unified, high-performance, and lightweight local smart home controller backend and web dashboard. It enables instant local control of **Tapo P304M Matter Power Strips** (via Wi-Fi/Matter), **SwitchBot Bots** (via Bluetooth LE), and **Wake on LAN** (via UDP broadcast) from a single Web UI or REST API webhooks.

Designed for low latency and high stability, OmniGate features sub-50ms execution times and robust hardware serialization lockouts to prevent overlapping radio conflicts.

---

## ✨ Features

* **⚡ Ultra-Low Latency Matter Control**: Implements Secure CASE session caching. Bypasses slow mDNS and Sigma key exchanges to send commands to Tapo outlets in **under 50ms** (down from 2-3 seconds).
* **🔋 Battery-Safe BLE Optimization**: Employs Windows GATT database caching to connect to SwitchBot Bots in under 1 second. Utilizes a smart **10-second active session keep-alive** to make consecutive commands **instantaneous (~10ms)** while conserving the CR2 battery.
* **🛡️ Hardware Serialization Lock & Cancel Protection**: Integrates a sequential hardware semaphore lock and propagates ASP.NET Core `CancellationToken` signals. If you refresh the dashboard or spam commands, old requests are immediately evicted from the queues, preventing the Tapo strip from returning `BUSY` or getting stuck.
* **📱 Premium Mobile-Responsive Dashboard**: A modern, glassmorphic dark-theme Web UI. Collapses setting panels (BLE Radar, Matter Provisioning Wizard, Wake on LAN, iOS Shortcut Integration assistant) and reflows tables into cards on screens under 768px.
* **🔗 iOS Shortcuts Integration**: Custom assistant that generates ready-to-use Siri Shortcuts (POST webhooks) to toggle or trigger your outlets, switches, and Wake on LAN hands-free.
* **🏷️ Persistent Custom Names**: Save custom labels for individual outlets (stored locally in `names.json` and kept out of Git).
* **👁️ Outlet Hiding**: Hide outlets from the dashboard for a cleaner view. Hidden state is persisted server-side in `hidden.json` and shared across all frontends. Technical metadata (Node IDs, endpoint types) is only shown when the Hidden toggle is active.
* **💻 Wake on LAN (WOL)**: Send UDP magic packets to wake a desktop PC on the local network. Configurable target MAC, broadcast IP, and port. Works from the dashboard UI, REST API, or iOS Shortcuts.
* **🩹 Self-Healing Socket & Session Recovery**: Monitors UDP network socket status and automatically recovers from long-running socket corruption (e.g. after 24+ hours of inactivity, DHCP renewals, or sleep mode wakeups). On socket failures (`SocketException` or `"An invalid argument was supplied"`), OmniGate disposes of the stale Matter controller, clears the cache, binds to a fresh socket, and transparently auto-retries the command without dropping requests.

---

## 🛠️ Tech Stack

* **Backend**: C# / .NET 10.0 / ASP.NET Core / [Lib.Harmony](https://github.com/pardeike/Harmony) (used to patch MatterDotNet library serialization issues).
* **Frontend**: Vanilla HTML5, custom CSS (Outfit font, smooth gradients, status animations), and Vanilla Javascript.

---

## 🚀 Getting Started

### Prerequisites
* **Runtime**: [ASP.NET Core Runtime 10.0](https://dotnet.microsoft.com/download/dotnet/10.0) (Windows x64 Hosting Bundle) installed on the hosting server.
* **Hardware**:
  * Tapo P304M Matter Power Strip (commissioned on local Wi-Fi).
  * SwitchBot Bot (within BLE range of the host PC).
  * Desktop PC with Wake on LAN enabled in BIOS (optional).

### Configuration

The application reads from `appsettings.json` (defaults) and `appsettings.local.json` (local secrets/overrides). Only `appsettings.local.json` should contain real device addresses — it is automatically ignored by Git.

Create an `appsettings.local.json` file in the root directory:

```json
{
  "SwitchBot": {
    "MacAddress": "YOUR:SWITCHBOT:MAC:ADDRESS"
  },
  "WakeOnLan": {
    "TargetMacAddress": "YOUR-PC-MAC-ADDRESS",
    "BroadcastIP": "192.168.1.255"
  }
}
```

#### Configuration Reference

| Section | Key | Default | Description |
|---|---|---|---|
| `SwitchBot` | `MacAddress` | `00:00:00:00:00:00` | BLE MAC address of your SwitchBot Bot |
| `SwitchBot` | `ListenUrl` | `http://0.0.0.0:5000` | HTTP server bind address |
| `SwitchBot` | `EnableBackgroundWatcher` | `false` | Passive BLE scanning to warm connection cache |
| `Tapo` | `FabricFile` | `fabric.bin` | Matter fabric state file path |
| `Tapo` | `KeyFile` | `fabric.key` | Matter private key file path |
| `Tapo` | `SafetyLockEndpoint` | `4` | Endpoint ID protected by safety lock |
| `WakeOnLan` | `TargetMacAddress` | `00:00:00:00:00:00` | MAC address of the PC to wake |
| `WakeOnLan` | `BroadcastIP` | `255.255.255.255` | Subnet broadcast IP (e.g. `192.168.1.255`) |
| `WakeOnLan` | `Port` | `9` | UDP port for magic packet |

> **Note**: Use your subnet-directed broadcast (e.g. `192.168.1.255` for a `192.168.1.0/24` network) instead of `255.255.255.255` for reliable WOL delivery.

### Running the App
Download the compiled release folder, open a command prompt inside it, and run:
```cmd
OmniGate.exe
```
The server will start listening on `http://localhost:5000` (or the IP configured under `ListenUrl`). Open this address in any browser to access the dashboard.

---

## 📡 REST API Reference

OmniGate exposes a simple REST API that makes it easy to integrate with iOS Shortcuts, Stream Decks, Home Assistant, or task schedulers:

### Tapo Power Strip (Matter)
* **`GET /api/tapo/list`**: Returns a list of discovered Tapo strips, their outlets, current ON/OFF states, and custom labels.
* **`POST /api/tapo/{nodeId}/{endpointId}/on`**: Turns a specific outlet ON.
* **`POST /api/tapo/{nodeId}/{endpointId}/off`**: Turns a specific outlet OFF.
* **`POST /api/tapo/{nodeId}/{endpointId}/toggle`**: Toggles the outlet state.

### SwitchBot Bot (Bluetooth LE)
* **`GET /api/switchbot/status`**: Returns the connection state, battery level, and last notification payload.
* **`POST /api/switchbot/on`**: Presses/Turns on the SwitchBot.
* **`POST /api/switchbot/off`**: Presses/Turns off the SwitchBot.

### Wake on LAN
* **`POST /api/wol/wake`**: Sends a UDP magic packet to wake the configured PC. Accepts an optional JSON body to override defaults:
  ```json
  { "macAddress": "AA:BB:CC:DD:EE:FF", "broadcastIp": "192.168.1.255", "port": 9 }
  ```
  All fields are optional — if omitted, server-side `appsettings.local.json` values are used.

### Custom Labels
* **`GET /api/names`**: Retrieves all custom device labels.
* **`POST /api/names`**: Updates a custom label (takes JSON body `{ "id": "nodeId_endpointId", "name": "Desk Lamp" }`).

### Hidden Outlets
* **`GET /api/tapo/hidden`**: Returns the list of hidden outlet keys.
* **`POST /api/tapo/hidden`**: Updates the hidden state (takes JSON body `{ "key": "nodeId_endpointId", "hidden": true }`).

---

## 📦 Building from Source

To compile the project manually:

```bash
# Publish framework-dependent release files
dotnet publish -c Release -o publish
```
Copy the contents of the `publish/` folder (including the `wwwroot` directory) to your target server and run.
