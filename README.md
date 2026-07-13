# OmniGate 🎛️

OmniGate is a unified, high-performance, and lightweight local smart home controller backend and web dashboard. It enables instant local control of **Tapo P304M Matter Power Strips** (via Wi-Fi/Matter) and **SwitchBot Bots** (via Bluetooth LE) from a single Web UI or REST API webhooks.

Designed for low latency and high stability, OmniGate features sub-50ms execution times and robust hardware serialization lockouts to prevent overlapping radio conflicts.

---

## ✨ Features

* **⚡ Ultra-Low Latency Matter Control**: Implements Secure CASE session caching. Bypasses slow mDNS and Sigma key exchanges to send commands to Tapo outlets in **under 50ms** (down from 2-3 seconds).
* **🔋 Battery-Safe BLE Optimization**: Employs Windows GATT database caching to connect to SwitchBot Bots in under 1 second. Utilizes a smart **10-second active session keep-alive** to make consecutive commands **instantaneous (~10ms)** while conserving the CR2 battery.
* **🛡️ Hardware Serialization Lock & Cancel Protection**: Integrates a sequential hardware semaphore lock and propagates ASP.NET Core `CancellationToken` signals. If you refresh the dashboard or spam commands, old requests are immediately evicted from the queues, preventing the Tapo strip from returning `BUSY` or getting stuck.
* **📱 Premium Mobile-Responsive Dashboard**: A modern, glassmorphic dark-theme Web UI. Collapses setting panels (BLE Radar, Matter Provisioning Wizard, iOS Shortcut Integration assistant) and reflows tables into cards on screens under 768px.
* **🔗 iOS Shortcuts Integration**: Custom assistant that generates ready-to-use Siri Shortcuts (POST webhooks) to toggle or trigger your outlets and switches hands-free.
* **🏷️ Persistent Custom Names**: Save custom labels for individual outlets (stored locally in `names.json` and kept out of Git).

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

### Configuration (`appsettings.json`)
The application is pre-configured to run locally. To define your physical SwitchBot MAC address, create an `appsettings.local.json` file in the root directory:

```json
{
  "SwitchBot": {
    "MacAddress": "E8:09:12:F0:B9:C5",
    "ListenUrl": "http://0.0.0.0:5000"
  }
}
```

*Note: Your `appsettings.local.json` is automatically ignored by Git and will not leak if you push to public repos.*

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

*Note: Outlets are protected by a safety lock mechanism (by default, Endpoint 4 is locked to prevent turning off the host server PC).*

### SwitchBot Bot (Bluetooth LE)
* **`GET /api/switchbot/status`**: Returns the connection state, battery level, and last notification payload.
* **`POST /api/switchbot/on`**: Presses/Turns on the SwitchBot.
* **`POST /api/switchbot/off`**: Presses/Turns off the SwitchBot.

### Custom Labels
* **`GET /api/names`**: Retrieves all custom device labels.
* **`POST /api/names`**: Updates a custom label (takes JSON body `{ "id": "nodeId_endpointId", "name": "Desk Lamp" }`).

---

## 📦 Building from Source

To compile the project manually:

```bash
# Publish framework-dependent release files
dotnet publish -c Release -o publish
```
Copy the contents of the `publish/` folder (including the `wwwroot` directory) to your target server and run.
