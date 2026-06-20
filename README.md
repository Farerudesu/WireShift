<p align="center">
  <h1 align="center">🔀 WireShift</h1>
  <p align="center"><em>Shift your wires, shift your traffic</em></p>
</p>

<p align="center">
  <a href="#"><img src="https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6?style=for-the-badge&logo=windows&logoColor=white" alt="Windows"></a>
  <a href="#"><img src="https://img.shields.io/badge/.NET-8.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 8"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-green?style=for-the-badge" alt="License: MIT"></a>
  <a href="#contributing"><img src="https://img.shields.io/badge/PRs-welcome-brightgreen?style=for-the-badge" alt="PRs Welcome"></a>
  <a href="docs/RFC_LOAD_BALANCING.md"><img src="https://img.shields.io/badge/help%20wanted-Load%20Balancing-orange?style=for-the-badge" alt="Help Wanted: Load Balancing"></a>
</p>

---

## Overview

**WireShift** is an open-source Windows desktop application that lets you bind specific applications to specific network interfaces  -  forcing their traffic through the adapter you choose.

Have Wi-Fi *and* Ethernet connected? Want your browser on the VPN but your game on Ethernet? WireShift makes it one click.

- **Per-application network interface binding**  -  force any app's traffic through a specific adapter (Wi-Fi, Ethernet, VPN, cellular, etc.)
- **Two routing modes**  -  *Transparent Redirect* via WinDivert for zero-config interception, and *Manual Proxy* via SOCKS5 as a flexible fallback
- **Real-time process monitoring**  -  live process list with network connection info, updated in real time
- **WPF desktop UI**  -  clean, modern interface built with Windows Presentation Foundation

---

## Features
| Feature | Description |
|---|---|
| **One-click binding** | Select an app, select an interface, bind. Done. |
| **Transparent traffic redirection** | WinDivert intercepts and reroutes packets automatically — no app configuration needed |
| **SOCKS5 proxy fallback** | For apps with built-in proxy support (browsers, download managers, etc.) |
| **Real-time process list** | See all running processes with their active network connections |
| **Interface metric management** | Automatically adjusts adapter metrics for proper routing |
| **Auto-reconnection** | UI automatically reconnects to the background service if it restarts |
| **QUIC/UDP blocking** | Blocks QUIC & UDP for bound apps, forcing TCP for reliable interception |
| **IPv6 blocking** | Ensures IPv4-only routing through the target interface |
| **Persistent configuration** | Bindings survive restarts — pick up right where you left off |
| **Selectable routing method** | Choose Transparent Redirect or Manual Proxy per binding |
---

## Architecture

WireShift is composed of three projects:

| Project | Internal Name | Role |
|---|---|---|
| **WireShift.Shared** | `NetBinder.Shared` | Shared models, protocol messages, and DTOs used by both Service and UI |
| **WireShift.Service** | `NetBinder.Service` | Background service  -  WinDivert packet interception, WFP filter management, SOCKS5 proxy server, transparent proxy, named pipe IPC server |
| **WireShift.UI** | `NetBinder.UI` | WPF desktop application  -  process list, interface selector, binding management |

---

## How It Works

WireShift's transparent mode uses a **NAT rewriting** approach powered by [WinDivert](https://github.com/basil00/Divert):

```
App ──► SYN packet ──► WinDivert captures ──► Rewrite dest to local proxy
                                                        │
                                                        ▼
App ◄── Response ◄──── Reverse NAT rewrite ◄── Transparent Proxy
                       (looks like original               │
                        server replied)                    ▼
                                               Connect to real destination
                                               via target interface
                                               (IP_UNICAST_IF + bind)
```

**Step by step:**

1. **Capture**  -  WinDivert intercepts the outbound **SYN** packet from the bound application
2. **Rewrite**  -  The destination address is rewritten to point at WireShift's local transparent proxy
3. **Proxy connects**  -  The transparent proxy opens a connection to the *real* destination, bound to the target network interface using `IP_UNICAST_IF` and explicit socket binding
4. **Reverse NAT**  -  Response packets are rewritten so the application sees them as coming from the original server  -  completely transparent

> [!NOTE]
> WFP (Windows Filtering Platform) filters are installed per-app to ensure only the bound application's traffic is intercepted. Other applications are unaffected.

---

## Getting Started

### Prerequisites

| Requirement | Details |
|---|---|
| **OS** | Windows 10 or Windows 11 (64-bit) |
| **Runtime** | .NET 8.0 SDK (for building from source) |
| **Privileges** |  **Administrator**  -  required for WinDivert and WFP filter management |


### Quick Start (Pre-built)

1. Download your preferred edition from [**Releases**](https://github.com/YOUR_USERNAME/WireShift/releases)
2. Extract the ZIP to a folder of your choice
3. Double-click **`WireShift.bat`** to launch (will prompt for admin privileges)

> [!IMPORTANT]
> WireShift **must** run as Administrator. The launcher batch file will request elevation automatically.

> [!TIP]
> If you have .NET 8 installed, grab the **Portable** edition  -  it's 20x smaller!

### Building from Source

```bash
git clone https://github.com/Farerudesu/WireShift.git
cd WireShift
dotnet build
```

### Publishing Self-Contained EXEs

To create standalone executables that don't require .NET to be installed:

```bash
# Build the background service
dotnet publish src/NetBinder.Service -c Release -r win-x64 --self-contained -p:PublishSingleFile=true

# Build the UI application
dotnet publish src/NetBinder.UI -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

The output binaries will be in each project's `bin/Release/net8.0-windows/win-x64/publish/` directory.

---

## Usage

1. **Launch WireShift**  -  run `WireShift.bat`, or start both the Service and UI executables as Administrator
2. **Select a network interface** from the left panel (Wi-Fi, Ethernet, VPN, etc.)
3. **Select a running process** from the right panel's real-time process list
4. **Choose a routing method**  -  *Transparent Redirect* or *Manual Proxy (SOCKS5)*
5. **Click `Bind -->`** or double-click the process to create the binding
6. The binding appears in the **bottom panel** with its current status

> [!TIP]
> You can change the routing method for an existing binding without removing it. Just select the binding and switch modes.

---

## Routing Methods

### Transparent Redirect *(Default)*

| | |
|---|---|
| **How** | WinDivert intercepts packets at the network layer and reroutes them through a local transparent proxy |
| **Config needed** | None  -  completely automatic |
| **Best for** | Most applications: games, streaming, productivity tools, etc. |
| **Pros** | Zero-config, works with any TCP application |

### Manual Proxy (SOCKS5)

| | |
|---|---|
| **How** | WireShift spins up a local SOCKS5 proxy bound to the target interface |
| **Config needed** | Copy the proxy endpoint (shown in the binding row) and paste it into the app's proxy settings |
| **Best for** | Browsers, download managers, apps with native proxy support |
| **Pros** | Works when transparent mode conflicts with app sandboxes or security software |

> [!NOTE]
> Some applications (e.g., sandboxed UWP apps) may not work well with transparent redirection. Use Manual Proxy mode for those.

---

## Roadmap

- [x] Per-app interface binding via WinDivert
- [x] SOCKS5 proxy fallback
- [x] WFP filter management
- [x] Interface metric control
- [x] Selectable routing methods per binding
- [ ] **Load Balancing**  -  distribute connections across multiple interfaces *(contributors wanted!)*
- [ ] Kernel-mode WFP callout driver for true transparent binding
- [ ] Per-interface bandwidth monitoring
- [ ] System tray mode with minimize-to-tray
- [ ] Dark mode UI theme

---

## 🤝 Contributing

We welcome contributions of all kinds  -  bug fixes, features, docs, and tests!

Please see **[CONTRIBUTING.md](CONTRIBUTING.md)** for guidelines on how to get started.

### Help Wanted: Load Balancing

We're actively looking for contributors to help design and implement **multi-interface load balancing**  -  distributing an application's connections across multiple network adapters for increased throughput and redundancy.

> 📄 Read the **[Load Balancing RFC](docs/RFC_LOAD_BALANCING.md)** for the full design proposal and discussion.

If you're interested, open an issue or jump into the RFC discussion!

---

## License

This project is licensed under the **MIT License**  -  see the [LICENSE](LICENSE) file for details.

---

## Acknowledgments

- **[WinDivert](https://github.com/basil00/Divert)** by basil00  -  user-mode packet capture, modification, and injection for Windows
- **Windows Filtering Platform (WFP)**  -  Microsoft's built-in framework for per-application network filter management
- **Special Thanks To Gemini 3.1 Pro, Claude 4.6 Opus**
---

<p align="center">
  <sub>Built with ❤️ for the Windows networking community</sub>
</p>
