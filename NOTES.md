# NetBinder - Architectural Decisions & Limitations

## Phase 1: Foundation
- **Solution structure**: 3 projects - Shared (models/protocol), Service (background worker), UI (WPF)
- **IPC**: Named Pipe `"NetBinderService"` with length-prefixed JSON protocol
- **Config**: JSON at `%APPDATA%\NetBinder\config.json`, managed by ConfigManager singleton
- **Native interop**: P/Invoke to `iphlpapi.dll` for adapter enumeration and metric control
- **UAC**: Both projects require admin elevation via app.manifest

## Phase 2: WFP Per-Process Interface Binding

### Key Limitation: WFP User-Mode Cannot Redirect Traffic
WFP user-mode API (`fwpuclnt.dll`) can only **PERMIT** or **BLOCK** connections at the ALE layer.
It **CANNOT** modify socket options like `IP_UNICAST_IF` from user mode.

**What this means**: True per-app interface binding (forcing an app's traffic out through a
specific interface) requires a **kernel-mode callout driver** that intercepts
`ALE_AUTH_CONNECT` classifications and calls `setsockopt(IP_UNICAST_IF)` on the target socket.

**Current approach**: WFP filters serve as "tracking markers" and can optionally block apps
from connecting on non-target interfaces. For actual traffic redirection, the SOCKS5 proxy
(Phase 3) is the user-mode solution.

### Implementation Details
- `WfpNativeMethods.cs`: Full P/Invoke bindings for fwpuclnt.dll
- `WfpFilterManager.cs`: Manages WFP engine session, sublayer, and per-app filters
- Filters at `FWPM_LAYER_ALE_AUTH_CONNECT_V4/V6` using `FWPM_CONDITION_ALE_APP_ID`
- App ID obtained via `FwpmGetAppIdFromFileName0` from executable path
- Dynamic filter add/remove on binding changes (no service restart needed)
- Full cleanup on service shutdown: all filters removed, sublayer deleted, engine closed

## Phase 3: SOCKS5 Proxy (User-Mode Fallback)

### Architecture
- One `Socks5Proxy` instance per network interface, each on a unique port (starting at 10800)
- `Socks5ProxyManager` manages multiple proxy instances and port assignments
- The proxy's outbound socket sets `IP_UNICAST_IF` via `setsockopt()` (ws2_32.dll P/Invoke)
  - This works for the proxy process's own sockets, forcing outbound traffic through the target interface
- Supports RFC 1928 SOCKS5: CONNECT command, NO AUTH, IPv4/IPv6/Domain address types
- Bidirectional data relay between client and remote socket

### For Apps That Support Proxy
- Users configure their app's proxy settings to `127.0.0.1:PORT` shown in the UI
- Example: Chrome `--proxy-server=socks5://127.0.0.1:10800`, Firefox manual proxy, IDM proxy settings

### For Apps That Don't Support Proxy
- Currently requires manual proxy configuration per app
- Future options (not yet implemented):
  1. **WinDivert-based transparent redirect** (LGPL license - check compatibility)
  2. **Process launch wrapper** with environment variable `HTTP_PROXY`/`HTTPS_PROXY`
  3. **Phase 6 kernel driver** for transparent callout-based redirection

### Port Assignment
- Base port: 10800, increments per interface
- Port assignments persisted in config
- Proxy info (address, port, status) shown in UI binding list

## Phase 4: Interface Metric Management

### Implementation
- `SetInterfaceMetric()` via `SetIpInterfaceEntry` (iphlpapi.dll)
- Equivalent to `netsh interface ipv4 set interface <index> metric=<value>`
- Metric overrides persisted in config and re-applied on service start
- UI: metric input field with validation (1-9999 range) per adapter
- Reset restores original system metric

## Phase 5: Load Balancing (Groundwork Only)

### Data Model Extensions
- `BindingMode` enum: `SingleBind` (current) and `LoadBalance` (future)
- `InterfaceWeight` class: interface index + name + weight
- `BindingMapping` extended with `Mode` and `TargetInterfaces` list
- Currently all bindings default to `SingleBind` mode

### LoadBalancerService Stub
- Method signatures for `SelectInterface()`, `CheckInterfaceHealth()`, `GetSessionAffinity()`, `RecordConnection()`
- Documented approach: weighted round-robin at per-connection level
- Session affinity: LRU cache mapping dest_ip -> interface_index
- Health checking: periodic TCP probe to known endpoint via each interface

### UI Placeholder
- Disabled "Load Balancing" tab in TabControl
- Lists planned features: weighted distribution, session affinity, health monitoring, throughput stats

## Phase 6: Kernel Driver (Future - Not Implemented)

### Why Needed
- True transparent per-app interface binding (no proxy configuration required)
- Per-packet load balancing across multiple interfaces
- Cannot be achieved in user mode alone

### Architecture Notes
- **WFP Callout Driver** (KMDF): Register at `FWPM_LAYER_ALE_AUTH_CONNECT_V4`
- In `classifyFn`: get socket handle from classify context, call `ZwSetInformationFile`
  or use `WfpAleClassifyOptionData` to set `IP_UNICAST_IF` on the socket
- Return `FWP_ACTION_PERMIT` to allow the (now-bound) connection
- Requires Windows Driver Kit (WDK)
- Driver signing requirements for production deployment

### Alternative: WinDivert
- User-mode-friendly driver package for packet interception/modification
- LGPL license - check compatibility with project licensing before use
- Can redirect packets to specific interfaces without kernel driver development
- Would operate at a different layer than WFP (NDIS vs ALE)

## General Notes
- All HRESULT errors from WFP API are checked and logged
- Service shutdown performs full cleanup: WFP filters removed, proxies stopped, config saved
- Named Pipe supports reconnection (client can disconnect and reconnect)
- Config uses camelCase JSON serialization for consistency

## UX/UI Enhancements & Decisions

### Inline Editing vs Separate Panels
To minimize UI clutter and keep the layout focused on the primary actions, all secondary panel actions like "Interface Metric Override" were converted to inline editing. Selecting an interface list item switches the static metric text block to a TextBox. Edits are saved on `Enter` or `LostFocus` (blur), and validated immediately with visual feedback (red border). This removes the need for extra dialogs or remote action panels.

### Auto-Reconnection & State Blocking
If communication over the named pipe falls out (e.g. background service restarts or loses admin permissions), the UI now blocks user interaction with a clean, full-window blur overlay explaining the issue and offering a manual reconnect command. A background loop checks and attempts auto-reconnection every 5 seconds to gracefully resume normal operations without app restarts.

### Clipboard Handling & Copy Helpers
To address the SOCKS5 configuration requirement, active bindings now list their proxy endpoints prominently with an inline "Copy" button. This handles clipboard actions in the code-behind using the standard WPF `Clipboard` API and triggers a non-blocking toast, minimizing user cognitive load when copying configurations.

### Performance & virtualization
The process list uses `VirtualizingStackPanel` and recycling to handle large lists of active processes (100+ processes) without freezing the UI thread, ensuring a smooth scrolling experience. Search filtering is done locally in the ViewModel, avoiding latency.

### Selectable Routing Method (WinDivert vs SOCKS5)
To give users maximum flexibility, we introduced a per-binding `RoutingMethod` configuration:
- **Transparent Redirect**: Captures packets via WinDivert NDIS loop and redirects to transparent proxy. Automatically manages WFP permit filters, UDP block filters, and IPv6 drop filters.
- **Manual Proxy (SOCKS5)**: Completely bypasses WinDivert loopback NAT, WFP filters, and packet drops. The service runs the SOCKS5 proxy server for the interface on a local port, allowing users to configure the application's proxy settings manually. This serves as an alternative if transparent interception conflicts with app sandboxes or security policies.
- **Dynamic Updates**: Swapping the route mode immediately toggles the WFP filter registration and updates the redirector's active mappings without requiring service or application restarts.