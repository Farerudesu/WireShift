using System.Text.Json.Serialization;

namespace NetBinder.Shared.Models;

/// <summary>
/// Represents a network interface adapter on the system.
/// Populated from GetAdaptersAddresses (iphlpapi.dll).
/// </summary>
public class NetworkAdapterInfo
{
    public int InterfaceIndex { get; set; }
    public string AdapterName { get; set; } = string.Empty;      // Internal name (e.g., "Ethernet0")
    public string FriendlyName { get; set; } = string.Empty;     // User-visible name (e.g., "Ethernet")
    public string Description { get; set; } = string.Empty;      // Adapter description
    public string IpAddress { get; set; } = string.Empty;        // Primary IPv4 address
    public string SubnetMask { get; set; } = string.Empty;       // Subnet mask
    public string Gateway { get; set; } = string.Empty;          // Default gateway
    public string DnsServer { get; set; } = string.Empty;        // Primary DNS
    public string MacAddress { get; set; } = string.Empty;       // MAC address formatted as XX:XX:XX:XX:XX:XX
    public uint InterfaceMetric { get; set; }                    // Current interface metric
    public OperStatus Status { get; set; }                       // Up/Down/etc.
    public IfType InterfaceType { get; set; }                    // Ethernet/WiFi/Loopback/etc.
}

public enum OperStatus
{
    Up = 1,
    Down = 2,
    Testing = 3,
    Unknown = 4,
    Dormant = 5,
    NotPresent = 6,
    LowerLayerDown = 7
}

public enum IfType
{
    Other = 1,
    Ethernet = 6,
    TokenRing = 9,
    Fddi = 15,
    Ppp = 23,
    Loopback = 24,
    Slip = 28,
    Wireless80211 = 71,
    Tunnel = 131,
    HighPerformanceSerial = 199
}
