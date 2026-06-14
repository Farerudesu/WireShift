# RFC: Load Balancing Feature

> **Status:** Draft
> **Author:** WireShift Contributors
> **Created:** 2026-06-15
> **Last Updated:** 2026-06-15

---

## Summary

Add the ability to distribute a bound application's TCP connections across multiple network interfaces based on configurable weights. This enables bandwidth aggregation, failover resilience, and fine-grained control over which traffic flows through which connection.

---

## Motivation

- **Bandwidth aggregation** — Users with multiple internet connections (e.g., Ethernet + Wi-Fi, dual WAN) want to combine their throughput by spreading connections across all available interfaces.
- **Failover** — If one connection drops, traffic automatically shifts to the remaining healthy interfaces with zero manual intervention.
- **Traffic-type routing** — Use a low-latency interface for gaming or real-time applications while routing bulk downloads through a high-bandwidth interface.

---

## Current State

The codebase already has partial groundwork for load balancing:

| Component | Status |
|---|---|
| `BindingMode.LoadBalance` | Defined in the data model |
| `InterfaceWeight` class | Exists with `InterfaceIndex`, `InterfaceName`, `Weight` properties |
| `LoadBalancerService.cs` | Contains method stubs (not yet implemented) |
| UI "Load Balancing" tab | Present but disabled |

See **NOTES.md** (Phase 5 — Load Balancing groundwork) for the original design notes.

---

## Proposed Design

### 1. Connection Distribution Algorithm

Use **weighted round-robin at the per-connection level** (not per-packet). Each new TCP connection originating from the bound application is assigned to the next interface according to configured weights.

#### How Weighted Round-Robin Works

Given a set of interfaces with weights, build a schedule array proportional to those weights and cycle through it:

```
Example:
  Interface A — weight 3
  Interface B — weight 1

Schedule: [A, A, A, B]

Connection 1 → A
Connection 2 → A
Connection 3 → A
Connection 4 → B
Connection 5 → A   (cycle restarts)
...
```

Interface A receives **75%** of new connections; Interface B receives **25%**.

#### Schedule Generation (Pseudocode)

```csharp
private int[] BuildSchedule(List<InterfaceWeight> weights)
{
    var schedule = new List<int>();
    foreach (var w in weights)
    {
        for (int i = 0; i < w.Weight; i++)
            schedule.Add(w.InterfaceIndex);
    }
    return schedule.ToArray();
}
```

The schedule is regenerated whenever:
- Weights are updated by the user.
- An interface goes DOWN or comes back UP.

A thread-safe atomic counter (`_roundRobinIndex`) tracks the current position in the schedule. The next interface is selected via:

```csharp
int idx = Interlocked.Increment(ref _roundRobinIndex) % _schedule.Length;
return _schedule[idx];
```

---

### 2. Session Affinity

To avoid breaking application-level sessions (e.g., authenticated connections, multi-request API calls), an **LRU session affinity cache** maps destination endpoints to a fixed interface.

#### Cache Key

```
(DestinationIP, DestinationPort) → InterfaceIndex
```

#### Behavior

1. When a new connection is opened, first check the affinity cache.
2. If a cache entry exists **and** the mapped interface is healthy → use that interface.
3. If no entry exists or the mapped interface is DOWN → use weighted round-robin to select an interface and insert/update the cache entry.

#### Configuration

| Parameter | Default | Description |
|---|---|---|
| `SessionAffinityEnabled` | `true` | Enable/disable session affinity per-binding |
| `SessionAffinityTtl` | `5 minutes` | Time-to-live for cache entries |
| `SessionAffinityCacheSize` | `4096` | Maximum number of entries (LRU eviction) |

When `SessionAffinityEnabled` is set to `false`, the system uses pure weighted round-robin for every connection.

#### Implementation Notes

- Use a `ConcurrentDictionary<(IPAddress, int), AffinityEntry>` with a background cleanup timer.
- Each `AffinityEntry` stores the `InterfaceIndex`, a `LastUsed` timestamp, and a `CreatedAt` timestamp.
- The cleanup timer runs every 60 seconds and evicts entries older than `SessionAffinityTtl`.
- If the cache exceeds `SessionAffinityCacheSize`, the least-recently-used entries are evicted first.

---

### 3. Interface Health Checking

Continuous health monitoring ensures traffic is only routed to working interfaces.

#### Probe Mechanism

- **Method:** TCP connect probe to a configurable endpoint.
- **Default endpoint:** `1.1.1.1:443` (Cloudflare DNS — globally reachable, fast).
- **Probe interval:** Every **10 seconds** per interface.
- **Probes are bound to each specific interface** to test that interface's actual connectivity.

#### State Machine

```
                 ┌─────────────────────┐
                 │       HEALTHY       │
                 │  (accepting traffic) │
                 └──────────┬──────────┘
                            │
              3 consecutive failures
                            │
                            ▼
                 ┌─────────────────────┐
                 │      UNHEALTHY      │
                 │  (no new traffic)   │
                 └──────────┬──────────┘
                            │
              2 consecutive successes
                            │
                            ▼
                 ┌─────────────────────┐
                 │       HEALTHY       │
                 │  (accepting traffic) │
                 └─────────────────────┘
```

| Transition | Condition |
|---|---|
| HEALTHY → UNHEALTHY | 3 consecutive probe failures |
| UNHEALTHY → HEALTHY | 2 consecutive probe successes |

The asymmetric thresholds (3 down / 2 up) prevent flapping on unstable connections.

#### Failover Behavior

When an interface is marked UNHEALTHY:

1. Remove it from the active schedule (regenerate schedule with remaining healthy interfaces).
2. All existing affinity cache entries pointing to the DOWN interface are invalidated.
3. New connections are distributed across the remaining healthy interfaces.
4. **Existing connections are NOT terminated** — they will fail naturally and the application will reconnect through a healthy interface.

When an interface recovers:

1. Re-add it to the active schedule with its original weight.
2. New connections will begin flowing to it again.

#### Edge Case: All Interfaces Down

If all interfaces become unhealthy, fall back to the **primary interface** (lowest index) regardless of health status. Log a warning. This prevents a complete outage due to transient probe failures.

---

### 4. Integration Points

#### 4.1 `RedirectorService.MatchBinding()`

Currently returns a single `InterfaceIndex` from the binding. For `LoadBalance` mode:

```csharp
// Before (current):
return binding.InterfaceIndex;

// After:
if (binding.Mode == BindingMode.LoadBalance)
{
    return LoadBalancerService.SelectInterface(binding, destinationIp, destinationPort);
}
return binding.InterfaceIndex;
```

`SelectInterface` encapsulates the full selection logic:
1. Check session affinity cache.
2. If miss → weighted round-robin.
3. Verify selected interface is healthy.
4. Update affinity cache.
5. Return the chosen `InterfaceIndex`.

#### 4.2 `TransparentProxy.HandleClientAsync()`

**No changes required.** The NAT entry already stores a `TargetInterfaceIndex` per-connection, and each connection is routed through its individually assigned interface. The load balancer's decision is fully consumed upstream at the `RedirectorService` level.

#### 4.3 `PipeServer` / `ConfigManager`

Add the following IPC command handlers:

| Command | Description |
|---|---|
| `AddLoadBalancedBinding` | Create a new binding with `BindingMode.LoadBalance` and a list of `InterfaceWeight` entries |
| `UpdateWeights` | Modify the weight distribution for an existing load-balanced binding |
| `RemoveInterfaceFromLB` | Remove a single interface from a load-balanced binding |
| `GetHealthStatus` | Return current `InterfaceHealthStatus` for all interfaces in a binding |
| `GetConnectionStats` | Return per-interface active connection count and throughput |

**Persistence:** Weights, health check configuration, and session affinity settings are stored in `config.json` under a new `"loadBalancing"` key:

```json
{
  "loadBalancing": {
    "bindings": [
      {
        "appPath": "C:\\Games\\game.exe",
        "interfaces": [
          { "interfaceIndex": 3, "weight": 3 },
          { "interfaceIndex": 7, "weight": 1 }
        ],
        "sessionAffinity": {
          "enabled": true,
          "ttlSeconds": 300
        },
        "healthCheck": {
          "endpoint": "1.1.1.1:443",
          "intervalSeconds": 10,
          "downThreshold": 3,
          "upThreshold": 2
        }
      }
    ]
  }
}
```

#### 4.4 UI Changes

Enable and populate the existing **"Load Balancing"** tab with the following controls:

| Control | Description |
|---|---|
| Multi-select interface list | Checkboxes to include/exclude interfaces from load balancing |
| Weight sliders (1–10) | One slider per selected interface to set its relative weight |
| Connection count display | Real-time count of active connections per interface |
| Health status indicators | Green (healthy), yellow (recovering/degraded), red (down) dot per interface |
| Bandwidth distribution chart | Pie chart showing estimated traffic split based on current weights |
| Session affinity toggle | Checkbox to enable/disable per-binding |
| Health check config | Expandable section for endpoint, interval, and threshold overrides |

**Real-time updates:** The UI should poll `GetHealthStatus` and `GetConnectionStats` every 2 seconds via the named pipe connection to keep indicators current.

---

## Data Structures

### Existing

```csharp
/// <summary>
/// Represents an interface and its relative weight in the load balancing schedule.
/// </summary>
public class InterfaceWeight
{
    public int InterfaceIndex { get; set; }
    public string InterfaceName { get; set; }
    public int Weight { get; set; } = 1;
}
```

### To Implement

```csharp
/// <summary>
/// Tracks the health status of a single network interface.
/// Updated by the health check background task.
/// </summary>
public class InterfaceHealthStatus
{
    public int InterfaceIndex { get; set; }
    public bool IsHealthy { get; set; }
    public int ConsecutiveFailures { get; set; }
    public int ConsecutiveSuccesses { get; set; }
    public DateTime LastChecked { get; set; }
    public double LatencyMs { get; set; }
}

/// <summary>
/// Session affinity cache entry mapping a destination endpoint to an interface.
/// </summary>
public class AffinityEntry
{
    public int InterfaceIndex { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastUsed { get; set; }
}

/// <summary>
/// Per-interface connection statistics for UI display.
/// </summary>
public class InterfaceConnectionStats
{
    public int InterfaceIndex { get; set; }
    public string InterfaceName { get; set; }
    public int ActiveConnections { get; set; }
    public long BytesSent { get; set; }
    public long BytesReceived { get; set; }
    public double CurrentThroughputMbps { get; set; }
}

/// <summary>
/// Configuration for health checking a single interface.
/// </summary>
public class HealthCheckConfig
{
    public string Endpoint { get; set; } = "1.1.1.1:443";
    public int IntervalSeconds { get; set; } = 10;
    public int DownThreshold { get; set; } = 3;
    public int UpThreshold { get; set; } = 2;
}
```

---

## Implementation Phases

### Phase 1 — Basic Weighted Round-Robin

- Implement `LoadBalancerService.SelectInterface()` with weighted round-robin.
- Integrate with `RedirectorService.MatchBinding()`.
- Add `AddLoadBalancedBinding` and `UpdateWeights` pipe commands.
- Persist configuration in `config.json`.
- **Milestone:** Connections from a bound app are distributed across interfaces by weight.

### Phase 2 — Session Affinity Cache

- Implement the LRU affinity cache with `ConcurrentDictionary`.
- Add background cleanup timer for TTL-based eviction.
- Add `SessionAffinityEnabled` and `SessionAffinityTtl` to the binding config.
- **Milestone:** Repeated connections to the same server consistently use the same interface.

### Phase 3 — Health Monitoring and Failover

- Implement the TCP probe health checker as a background `Task`.
- Track per-interface health state with the HEALTHY/UNHEALTHY state machine.
- Regenerate the schedule on health state changes.
- Invalidate stale affinity entries on failover.
- Add `GetHealthStatus` pipe command.
- **Milestone:** Traffic automatically avoids downed interfaces and recovers when they come back.

### Phase 4 — UI Integration

- Enable the "Load Balancing" tab.
- Build the multi-select interface list with weight sliders.
- Wire up health status indicators and connection count displays.
- Add the bandwidth distribution pie chart.
- Poll for real-time updates via named pipe.
- **Milestone:** Users can configure and monitor load balancing entirely from the GUI.

### Phase 5 — Per-Interface Throughput Statistics

- Instrument `TransparentProxy` to track bytes sent/received per-interface.
- Expose `GetConnectionStats` pipe command.
- Display throughput metrics in the UI.
- **Milestone:** Users have full visibility into how traffic is distributed in practice.

---

## Open Questions

1. **Per-packet load balancing** — Should we support distributing individual packets across interfaces (MPTCP-style)? This would offer true bandwidth aggregation for single connections but is significantly more complex, requiring packet reassembly and reordering. Recommendation: defer to a future RFC.

2. **Long-lived connections** — How should WebSocket, SSH, and other persistent connections be handled? They will be assigned to one interface at connection time and stay there. If that interface goes down, the connection will break. Should we attempt transparent migration? Recommendation: document this as expected behavior for v1; explore connection migration in a future phase.

3. **Per-interface health endpoints** — Should the health check target be configurable per-interface rather than globally? For example, one interface might be on a corporate network where `1.1.1.1` is blocked. Recommendation: support per-interface overrides in `HealthCheckConfig` from Phase 3.

4. **UDP traffic** — The current design focuses on TCP connections. How should UDP traffic (DNS, gaming protocols) be handled? Recommendation: route all UDP through the primary interface initially; add UDP load balancing in a future phase.

5. **Weight auto-tuning** — Should we automatically adjust weights based on measured throughput or latency? Recommendation: gather statistics in Phase 5 first, then evaluate feasibility.

---

## References

- **NOTES.md** — Phase 5 (Load Balancing groundwork) for original design discussions.
- **LoadBalancerService.cs** — Existing method stubs to be implemented.
- **InterfaceWeight.cs** — Existing data model for interface weights.
- **BindingMode enum** — `BindingMode.LoadBalance` is already defined.
