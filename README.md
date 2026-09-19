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
* **🧩 Dynamic Routines**: Build reusable ordered sequences that combine every discovered Tapo outlet, SwitchBot ON/OFF actions, Wake on LAN, and configurable time gaps (for example, power a PC outlet, wait 4 seconds, then send WOL).
* **🏷️ Persistent Custom Names**: Save custom labels for individual outlets (stored in the configured local state directory and kept out of Git).
* **👁️ Outlet Hiding**: Hide outlets from the dashboard for a cleaner view. Hidden state is persisted server-side and shared across all frontends. Technical metadata (Node IDs, endpoint types) is only shown when the Hidden toggle is active.
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
  "Storage": {
    "Directory": "D:\\OmniGateData"
  },
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
| `Storage` | `Directory` | empty | Optional persistent data directory outside the release folder; stores names, hidden state, routines, and Matter fabric files |
| `Storage` | `NamesFile` | `names.json` | Optional custom path for outlet names; relative paths use `Storage:Directory` |
| `Storage` | `HiddenFile` | `hidden.json` | Optional custom path for hidden outlet state; relative paths use `Storage:Directory` |
| `Storage` | `RoutinesFile` | `routines.json` | Optional custom path for saved routine definitions; relative paths use `Storage:Directory` |
| `Tapo` | `FabricFile` | `fabric.bin` | Matter fabric state file path |
| `Tapo` | `KeyFile` | `fabric.key` | Matter private key file path |
| `Tapo` | `SafetyLockEndpoint` | `4` | Endpoint ID protected by safety lock |
| `Tapo` | `KeepAliveMinutes` | `30` | Background state-read interval that keeps the Matter CASE session warm |
| `Tapo` | `CommissioningTimeoutSeconds` | `120` | Maximum time allowed for initial Matter/Wi-Fi commissioning |
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

The local Matter fabric files (`fabric.bin` and `fabric.key`) are copied into the release folder when they exist, but remain ignored by Git. Keep both files together: they contain the fabric identity used by all commissioned Tapo devices. The dashboard's **Matter Provisioning Wizard** adds a new Matter-capable Tapo to that existing fabric.

For a server deployment, keep runtime state outside the release folder so replacing the application files cannot overwrite it. Add this to the server's `appsettings.local.json` (do not commit it):

```json
{
  "Storage": {
    "Directory": "D:\\OmniGateData"
  }
}
```

Create that folder before the first deployment. On first startup, OmniGate migrates an existing `names.json`, `hidden.json`, `routines.json`, `fabric.bin`, and `fabric.key` from the application folder when the corresponding external file does not already exist. Later deployments use the external copies and leave them untouched. Back up the folder before deploying or commissioning another Tapo.

---

## 📡 REST API Reference

OmniGate exposes a simple REST API that makes it easy to integrate with iOS Shortcuts, Stream Decks, Home Assistant, or task schedulers:

### Tapo Power Strip (Matter)
* **`GET /api/tapo/list`**: Returns a list of discovered Tapo strips, their outlets, current ON/OFF states, and custom labels.
* **`POST /api/tapo/commission`**: Commissions a Matter-capable Tapo using `{ "setupCode": "...", "wifiSsid": "...", "wifiPassword": "..." }`. The setup code may be a Matter PIN or an `MT:` QR payload.
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

### Dynamic Routines (iOS Shortcuts)
* **`GET /api/routines`**: Lists saved routines and their generated API paths.
* **`GET /api/routines/{idOrSlug}`**: Returns one routine definition.
* **`POST /api/routines`**: Creates a routine from an ordered `steps` array.
* **`PUT /api/routines/{idOrSlug}`**: Updates a routine definition.
* **`DELETE /api/routines/{idOrSlug}`**: Deletes a routine that is not currently running.
* **`POST /api/routines/{idOrSlug}/run`**: Starts the routine and returns `202 Accepted` with a `runId`. This is the URL to paste into iOS Shortcuts' **Get Contents of URL** action using the `POST` method; no request body is required.
* **`GET /api/routine-runs/{runId}`**: Returns queued, running, completed, failed, or cancelled status and per-step results.

Routine step examples:
```json
{
  "name": "Start workstation",
  "stopOnError": true,
  "preventDuplicateRuns": true,
  "steps": [
    { "type": "tapo", "nodeId": "123456789", "endpointId": 1, "action": "on" },
    { "type": "delay", "seconds": 4 },
    { "type": "wol", "action": "wake" },
    { "type": "switchbot", "action": "on" }
  ]
}
```

Supported routine step types are `tapo` (`on`/`off`), `switchbot` (`on`/`off`), `wol` (`wake`), and `delay` (1–3600 seconds). Routines execute sequentially in the saved order. SwitchBot results indicate that the command was queued; Tapo and WOL results indicate that the underlying command was acknowledged or the packet was sent.

When copying a routine URL for an iPhone, use the OmniGate host's LAN address (for example `http://192.168.1.10:5000`) rather than `localhost`.

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
