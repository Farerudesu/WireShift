namespace NetBinder.Shared.Models;

/// <summary>
/// Binding mode: SingleBind routes all traffic through one interface,
/// LoadBalance distributes connections across multiple interfaces.
/// LoadBalance is future functionality (Phase 5+).
/// </summary>
public enum BindingMode
{
    /// <summary>All traffic from the bound app goes through a single interface.</summary>
    SingleBind = 0,
    /// <summary>Connections are distributed across multiple interfaces by weight. (Future)</summary>
    LoadBalance = 1
}

/// <summary>
/// Defines how traffic from the bound application is routed.
/// </summary>
public enum RoutingMethod
{
    /// <summary>Transparently intercept and route traffic via WinDivert.</summary>
    Transparent = 0,
    /// <summary>Do not intercept traffic. User configures proxy settings manually.</summary>
    ProxyOnly = 1
}

/// <summary>
/// Represents a single interface in a load-balanced binding with its weight.
/// Weight determines the proportion of connections routed through this interface.
/// </summary>
public class InterfaceWeight
{
    public int InterfaceIndex { get; set; }
    public string InterfaceName { get; set; } = string.Empty;
    /// <summary>Relative weight for load balancing (higher = more connections). Default: 1.</summary>
    public int Weight { get; set; } = 1;
}

/// <summary>
/// A binding rule that maps a specific process (by executable path) to a
/// specific network interface (by interface index). When active, all traffic
/// from that process is forced out through the specified interface.
/// </summary>
public class BindingMapping
{
    /// <summary>Unique ID for this mapping entry.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Full executable path of the target process (used as key).</summary>
    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>Display name of the process (for UI purposes).</summary>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>Interface index of the target adapter.</summary>
    public int InterfaceIndex { get; set; }

    /// <summary>Friendly name of the target interface (for UI purposes).</summary>
    public string InterfaceName { get; set; } = string.Empty;

    /// <summary>Whether this binding rule is currently active/enforced.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>SOCKS5 proxy port assigned to the target interface (0 = not assigned).</summary>
    public int ProxyPort { get; set; }

    /// <summary>SOCKS5 proxy address string for the target interface (e.g., "127.0.0.1:10800").</summary>
    public string ProxyAddress { get; set; } = string.Empty;

    /// <summary>Binding mode: SingleBind (one interface) or LoadBalance (multiple interfaces).</summary>
    public BindingMode Mode { get; set; } = BindingMode.SingleBind;

    /// <summary>Routing method: Transparent (WinDivert) or ProxyOnly (Manual SOCKS5 proxy).</summary>
    public RoutingMethod RoutingMethod { get; set; } = RoutingMethod.Transparent;

    /// <summary>
    /// For LoadBalance mode: list of target interfaces with weights.
    /// For SingleBind mode: contains a single entry matching InterfaceIndex/InterfaceName.
    /// </summary>
    public List<InterfaceWeight> TargetInterfaces { get; set; } = [];

    /// <summary>When this mapping was created.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// The complete configuration file stored in %APPDATA%\NetBinder\config.json.
/// Contains all binding mappings and per-interface metric overrides.
/// </summary>
public class NetBinderConfig
{
    public List<BindingMapping> Bindings { get; set; } = [];
    public List<InterfaceMetricOverride> MetricOverrides { get; set; } = [];
    public DateTime LastModified { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A metric/priority override for a specific network interface.
/// Allows the user to set custom metrics without using netsh directly.
/// </summary>
public class InterfaceMetricOverride
{
    public int InterfaceIndex { get; set; }
    public string InterfaceName { get; set; } = string.Empty;
    public uint OriginalMetric { get; set; }     // System default metric
    public uint CustomMetric { get; set; }        // User-specified metric
    public bool IsApplied { get; set; }           // Whether the override is currently active
}

/// <summary>
/// Information about a SOCKS5 proxy running for a specific interface.
/// Sent from Service to UI for display.
/// </summary>
public class ProxyInfo
{
    public int InterfaceIndex { get; set; }
    public string InterfaceName { get; set; } = string.Empty;
    public string ProxyAddress { get; set; } = string.Empty;   // e.g., "127.0.0.1:10800"
    public int Port { get; set; }
    public bool IsRunning { get; set; }
}
