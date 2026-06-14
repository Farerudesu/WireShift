using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using NetBinder.Shared.Models;

namespace NetBinder.Service.NativeInterop;

/// <summary>
/// Network adapter and metric management wrapper.
/// Adapter enumeration uses .NET's NetworkInterface API (reliable, avoids P/Invoke struct layout issues).
/// Metric get/set uses iphlpapi.dll P/Invoke (GetIpInterfaceEntry / SetIpInterfaceEntry).
/// </summary>
public static class IphlpApiWrapper
{
    #region P/Invoke Declarations

    private const string IphlpApi = "iphlpapi.dll";
    private const uint AF_INET = 2;
    private const uint ERROR_SUCCESS = 0;

    [DllImport(IphlpApi)]
    private static extern uint GetIpInterfaceEntry(ref MIB_IPINTERFACE_ROW row);

    [DllImport(IphlpApi)]
    private static extern uint SetIpInterfaceEntry(ref MIB_IPINTERFACE_ROW row);

    #endregion

    #region Native Structures

    [StructLayout(LayoutKind.Explicit, Size = 168)]
    private struct MIB_IPINTERFACE_ROW
    {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(8)] public ulong InterfaceLuid;
        [FieldOffset(16)] public uint InterfaceIndex;
        [FieldOffset(20)] public uint MaxReassemblySize;
        [FieldOffset(24)] public ulong InterfaceIdentifier;
        [FieldOffset(32)] public uint MinRouterAdvertisementInterval;
        [FieldOffset(36)] public uint MaxRouterAdvertisementInterval;
        
        [FieldOffset(40)] public byte AdvertisingEnabled;
        [FieldOffset(41)] public byte ForwardingEnabled;
        [FieldOffset(42)] public byte WeakHostSend;
        [FieldOffset(43)] public byte WeakHostReceive;
        [FieldOffset(44)] public byte UseAutomaticMetric;
        [FieldOffset(45)] public byte UseNeighborUnreachabilityDetection;
        [FieldOffset(46)] public byte ManagedAddressConfigurationSupported;
        [FieldOffset(47)] public byte OtherStatefulConfigurationSupported;
        [FieldOffset(48)] public byte AdvertiseDefaultRoute;
        
        [FieldOffset(52)] public uint RouterDiscoveryBehavior;
        [FieldOffset(56)] public uint DadTransmits;
        [FieldOffset(60)] public uint BaseReachableTime;
        [FieldOffset(64)] public uint RetransmitTime;
        [FieldOffset(68)] public uint PathMtuDiscoveryTimeout;
        [FieldOffset(72)] public uint LinkLocalAddressBehavior;
        [FieldOffset(76)] public uint LinkLocalAddressTimeout;
        
        [FieldOffset(80)] public uint ZoneIndex0;
        [FieldOffset(84)] public uint ZoneIndex1;
        [FieldOffset(88)] public uint ZoneIndex2;
        [FieldOffset(92)] public uint ZoneIndex3;
        [FieldOffset(96)] public uint ZoneIndex4;
        [FieldOffset(100)] public uint ZoneIndex5;
        [FieldOffset(104)] public uint ZoneIndex6;
        [FieldOffset(108)] public uint ZoneIndex7;
        [FieldOffset(112)] public uint ZoneIndex8;
        [FieldOffset(116)] public uint ZoneIndex9;
        [FieldOffset(120)] public uint ZoneIndex10;
        [FieldOffset(124)] public uint ZoneIndex11;
        [FieldOffset(128)] public uint ZoneIndex12;
        [FieldOffset(132)] public uint ZoneIndex13;
        [FieldOffset(136)] public uint ZoneIndex14;
        [FieldOffset(140)] public uint ZoneIndex15;
        
        [FieldOffset(144)] public uint SitePrefixLength;
        [FieldOffset(148)] public uint Metric;
        [FieldOffset(152)] public uint NlMtu;
        
        [FieldOffset(156)] public byte Connected;
        [FieldOffset(157)] public byte SupportsWakeUpPatterns;
        [FieldOffset(158)] public byte SupportsNeighborDiscovery;
        [FieldOffset(159)] public byte SupportsRouterDiscovery;
        
        [FieldOffset(160)] public uint ReachableTime;
        
        [FieldOffset(164)] public byte TransmitOffload;
        [FieldOffset(165)] public byte ReceiveOffload;
        
        [FieldOffset(166)] public byte DisableDefaultRoutes;
    }

    #endregion

    /// <summary>
    /// Enumerates all network adapters that are UP and have an IPv4 address.
    /// Uses .NET's NetworkInterface API — reliable and avoids P/Invoke struct layout issues.
    /// Each NIC is processed inside its own try/catch so one bad adapter cannot kill the service.
    /// </summary>
    public static List<NetworkAdapterInfo> GetNetworkAdapters()
    {
        var result = new List<NetworkAdapterInfo>();
        NetworkInterface[]? nics = null;

        Console.WriteLine("[IphlpApiWrapper] About to call NetworkInterface.GetAllNetworkInterfaces()...");
        try
        {
            nics = NetworkInterface.GetAllNetworkInterfaces();
            Console.WriteLine($"[IphlpApiWrapper] Got {nics.Length} NIC(s).");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IphlpApiWrapper] GetAllNetworkInterfaces() threw: {ex}");
            return result;
        }

        foreach (var nic in nics)
        {
            try
            {
                Console.WriteLine($"[IphlpApiWrapper] Processing NIC: {nic.Name} ({nic.NetworkInterfaceType})");

                // Only include adapters that are UP
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    Console.WriteLine($"[IphlpApiWrapper]   Skipping {nic.Name}: not Up.");
                    continue;
                }

                Console.WriteLine($"[IphlpApiWrapper]   Getting IP properties for {nic.Name}...");
                var ipProps = nic.GetIPProperties();
                Console.WriteLine($"[IphlpApiWrapper]   Got IP properties for {nic.Name}.");

                Console.WriteLine($"[IphlpApiWrapper]   Reading UnicastAddresses for {nic.Name}...");
                var ipv4Addr = ipProps.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                Console.WriteLine($"[IphlpApiWrapper]   ipv4Addr = {ipv4Addr?.Address}");

                // Skip adapters without an IPv4 address
                if (ipv4Addr == null)
                {
                    Console.WriteLine($"[IphlpApiWrapper]   Skipping {nic.Name}: no IPv4 address.");
                    continue;
                }

                Console.WriteLine($"[IphlpApiWrapper]   Getting IPv4 interface index for {nic.Name}...");
                int ifIndex = GetIpv4InterfaceIndex(nic);
                Console.WriteLine($"[IphlpApiWrapper]   ifIndex = {ifIndex}");
                if (ifIndex < 0)
                {
                    Console.WriteLine($"[IphlpApiWrapper]   Skipping {nic.Name}: could not determine index.");
                    continue;
                }

                Console.WriteLine($"[IphlpApiWrapper]   Getting metric for index {ifIndex}...");
                uint metric = GetInterfaceMetric(ifIndex);
                Console.WriteLine($"[IphlpApiWrapper]   metric = {metric}");

                var info = new NetworkAdapterInfo
                {
                    InterfaceIndex = ifIndex,
                    AdapterName = nic.Name,
                    FriendlyName = nic.Name, // .NET Name is the friendly name on Windows
                    Description = nic.Description,
                    IpAddress = ipv4Addr.Address.ToString(),
                    SubnetMask = GetSubnetMask(ipv4Addr.PrefixLength).ToString(),
                    Gateway = ipProps.GatewayAddresses
                        .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork)?
                        .Address.ToString() ?? string.Empty,
                    DnsServer = ipProps.DnsAddresses
                        .FirstOrDefault(d => d.AddressFamily == AddressFamily.InterNetwork)?
                        .ToString() ?? string.Empty,
                    MacAddress = FormatMacAddress(nic.GetPhysicalAddress()),
                    InterfaceMetric = metric,
                    Status = OperStatus.Up,
                    InterfaceType = MapNetworkInterfaceType(nic.NetworkInterfaceType),
                };

                result.Add(info);
                Console.WriteLine($"[IphlpApiWrapper]   Added {nic.Name} -> {info.IpAddress}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[IphlpApiWrapper] Error processing NIC {nic.Name}: {ex}");
            }
        }

        Console.WriteLine($"[IphlpApiWrapper] Returning {result.Count} adapter(s).");
        return result;
    }

    /// <summary>
    /// Sets the interface metric for a specific network adapter.
    /// Uses GetIpInterfaceEntry first to populate the LUID, then SetIpInterfaceEntry.
    /// Equivalent to: netsh interface ipv4 set interface &lt;index&gt; metric=&lt;value&gt;
    /// </summary>
    public static bool SetInterfaceMetric(int interfaceIndex, uint metric)
    {
        var row = new MIB_IPINTERFACE_ROW();
        row.Family = (ushort)AF_INET;
        row.InterfaceIndex = (uint)interfaceIndex;

        // Must call GetIpInterfaceEntry first to populate the InterfaceLuid
        uint ret = GetIpInterfaceEntry(ref row);
        if (ret != ERROR_SUCCESS)
        {
            Console.WriteLine($"[IphlpApiWrapper] GetIpInterfaceEntry failed for index {interfaceIndex}: error {ret}");
            return false;
        }

        // Now set the custom metric (disable automatic metric first)
        row.UseAutomaticMetric = 0;
        row.Metric = metric;

        ret = SetIpInterfaceEntry(ref row);
        if (ret != ERROR_SUCCESS)
        {
            Console.WriteLine($"[IphlpApiWrapper] SetIpInterfaceEntry failed for index {interfaceIndex}: error {ret}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Gets the current interface metric for a specific adapter.
    /// </summary>
    public static uint GetInterfaceMetric(int interfaceIndex)
    {
        var row = new MIB_IPINTERFACE_ROW();
        row.Family = (ushort)AF_INET;
        row.InterfaceIndex = (uint)interfaceIndex;

        uint ret = GetIpInterfaceEntry(ref row);
        if (ret == ERROR_SUCCESS)
            return row.Metric;

        return 0;
    }

    #region Private Helpers

    /// <summary>
    /// Gets the IPv4 interface index from a NetworkInterface via IPInterfaceProperties.
    /// </summary>
    private static int GetIpv4InterfaceIndex(NetworkInterface nic)
    {
        try
        {
            Console.WriteLine($"[IphlpApiWrapper]     GetIpv4InterfaceIndex: calling GetIPv4Properties for {nic.Name}...");
            var ipProps = nic.GetIPProperties();
            var ipv4Props = ipProps.GetIPv4Properties();
            Console.WriteLine($"[IphlpApiWrapper]     GetIpv4InterfaceIndex: Index={ipv4Props?.Index}");
            return ipv4Props?.Index ?? -1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IphlpApiWrapper]     GetIpv4InterfaceIndex exception for {nic.Name}: {ex.Message}");
            return -1;
        }
    }

    /// <summary>Formats a PhysicalAddress as XX:XX:XX:XX:XX:XX.</summary>
    private static string FormatMacAddress(PhysicalAddress addr)
    {
        if (addr == null || addr.GetAddressBytes().Length == 0)
            return string.Empty;
        return string.Join(":", addr.GetAddressBytes().Select(b => b.ToString("X2")));
    }

    /// <summary>Converts a CIDR prefix length to a dotted-decimal subnet mask.</summary>
    private static IPAddress GetSubnetMask(int prefixLength)
    {
        if (prefixLength < 0 || prefixLength > 32)
            prefixLength = 0;

        uint mask = prefixLength == 0 ? 0 : (uint)(0xFFFFFFFF << (32 - prefixLength));
        mask = (uint)IPAddress.HostToNetworkOrder((int)mask);
        return new IPAddress(mask);
    }

    /// <summary>Maps .NET NetworkInterfaceType to our IfType enum.</summary>
    private static IfType MapNetworkInterfaceType(NetworkInterfaceType type)
    {
        return type switch
        {
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.FastEthernetFx => IfType.Ethernet,
            NetworkInterfaceType.Wireless80211 => IfType.Wireless80211,
            NetworkInterfaceType.Loopback => IfType.Loopback,
            NetworkInterfaceType.Tunnel => IfType.Tunnel,
            NetworkInterfaceType.Ppp => IfType.Ppp,
            NetworkInterfaceType.Fddi => IfType.Fddi,
            NetworkInterfaceType.TokenRing => IfType.TokenRing,
            NetworkInterfaceType.Slip => IfType.Slip,
            _ => IfType.Other
        };
    }

    #endregion
}
