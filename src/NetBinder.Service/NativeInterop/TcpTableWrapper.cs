using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using NetBinder.Shared.Models;

namespace NetBinder.Service.NativeInterop;

/// <summary>
/// Wrapper around iphlpapi.dll's GetExtendedTcpTable function.
/// Enumerates all TCP connections with their owning PID.
/// Combined with System.Diagnostics.Process to get executable paths.
/// </summary>
public static class TcpTableWrapper
{
    #region P/Invoke Declarations

    private const string IphlpApi = "iphlpapi.dll";
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;
    private const uint NO_ERROR = 0;

    // Table type for GetExtendedTcpTable - owner PID is included
    private const uint TCP_TABLE_OWNER_PID_ALL = 5;
    
    // Table type for GetExtendedUdpTable - owner PID is included
    private const uint UDP_TABLE_OWNER_PID = 1;

    // TCP states matching MIB_TCP_STATE
    private const int MIB_TCP_STATE_CLOSED = 1;
    private const int MIB_TCP_STATE_LISTEN = 2;
    private const int MIB_TCP_STATE_SYN_SENT = 3;
    private const int MIB_TCP_STATE_SYN_RCVD = 4;
    private const int MIB_TCP_STATE_ESTAB = 5;
    private const int MIB_TCP_STATE_FIN_WAIT1 = 6;
    private const int MIB_TCP_STATE_FIN_WAIT2 = 7;
    private const int MIB_TCP_STATE_CLOSE_WAIT = 8;
    private const int MIB_TCP_STATE_CLOSING = 9;
    private const int MIB_TCP_STATE_LAST_ACK = 10;
    private const int MIB_TCP_STATE_TIME_WAIT = 11;
    private const int MIB_TCP_STATE_DELETE_TCB = 12;

    [DllImport(IphlpApi)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref uint pdwSize,
        bool bOrder,
        uint ulAf,
        uint TableClass,
        uint Reserved);

    [DllImport(IphlpApi)]
    private static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable,
        ref uint pdwSize,
        bool bOrder,
        uint ulAf,
        uint TableClass,
        uint Reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPTABLE_OWNER_PID
    {
        public uint dwNumEntries;
        // Followed by MIB_TCPROW_OWNER_PID[dwNumEntries] in memory
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] ucLocalAddr;
        public uint dwLocalScopeId;
        public uint dwLocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] ucRemoteAddr;
        public uint dwRemoteScopeId;
        public uint dwRemotePort;
        public uint dwState;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDPROW_OWNER_PID
    {
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwOwningPid;
    }

    #endregion

    /// <summary>
    /// Gets all processes that have active TCP connections (ESTABLISHED, LISTEN, etc.).
    /// Groups connections by PID and enriches with process name and executable path.
    /// </summary>
    public static List<NetworkProcessInfo> GetProcessesWithConnections()
    {
        var connections = GetAllTcpConnections();
        var processMap = new Dictionary<int, List<ConnectionInfo>>();

        // Group connections by PID
        foreach (var conn in connections)
        {
            int pid = (int)conn.dwOwningPid;
            if (!processMap.ContainsKey(pid))
                processMap[pid] = [];

            processMap[pid].Add(new ConnectionInfo
            {
                LocalEndPoint = $"{IpFromUint(conn.dwLocalAddr)}:{PortFromUint(conn.dwLocalPort)}",
                RemoteEndPoint = $"{IpFromUint(conn.dwRemoteAddr)}:{PortFromUint(conn.dwRemotePort)}",
                State = MapTcpState(conn.dwState)
            });
        }

        // Build process info list
        var result = new List<NetworkProcessInfo>();
        foreach (var (pid, conns) in processMap)
        {
            var info = new NetworkProcessInfo
            {
                Pid = pid,
                ActiveConnectionCount = conns.Count(c => c.State == Shared.Models.TcpState.Established),
                Connections = conns
            };

            // Get process name and path
            try
            {
                using var process = Process.GetProcessById(pid);
                info.ProcessName = process.ProcessName;

                // Get full executable path - may throw AccessDenied for system processes
                try
                {
                    info.FilePath = process.MainModule?.FileName ?? string.Empty;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // Access denied - process is likely a system process
                    info.FilePath = $"<access denied> ({process.ProcessName})";
                }
            }
            catch (ArgumentException)
            {
                // Process has already exited
                info.ProcessName = $"<exited> (PID {pid})";
            }
            catch (System.ComponentModel.Win32Exception)
            {
                info.ProcessName = $"<system> (PID {pid})";
            }

            result.Add(info);
        }

        // Sort by connection count descending (most active first)
        return result.OrderByDescending(p => p.ActiveConnectionCount).ToList();
    }

    /// <summary>
    /// Look up the owner PID for a given local port. Checks both IPv4 and IPv6.
    /// </summary>
    public static int GetOwnerPidForLocalPort(ushort localPort)
    {
        uint targetPortNetwork = (uint)(((localPort & 0xFF) << 8) | ((localPort >> 8) & 0xFF));
        
        // Check IPv4
        var connections = GetAllTcpConnections();
        foreach (var conn in connections)
        {
            if ((conn.dwLocalPort & 0xFFFF) == targetPortNetwork)
            {
                return (int)conn.dwOwningPid;
            }
        }
        
        // Check IPv6
        var connections6 = GetAllTcp6Connections();
        foreach (var conn in connections6)
        {
            if ((conn.dwLocalPort & 0xFFFF) == targetPortNetwork)
            {
                return (int)conn.dwOwningPid;
            }
        }
        
        return 0;
    }

    /// <summary>
    /// Retrieves all TCP rows with owning PID from GetExtendedTcpTable.
    /// </summary>
    private static List<MIB_TCPROW_OWNER_PID> GetAllTcpConnections()
    {
        var result = new List<MIB_TCPROW_OWNER_PID>();
        uint size = 0;

        // First call to get buffer size
        uint ret = GetExtendedTcpTable(
            IntPtr.Zero, ref size, false,
            2, // AF_INET (IPv4 only)
            TCP_TABLE_OWNER_PID_ALL, 0);

        if (ret != ERROR_INSUFFICIENT_BUFFER && ret != NO_ERROR)
            return result;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            ret = GetExtendedTcpTable(buffer, ref size, false, 2, TCP_TABLE_OWNER_PID_ALL, 0);
            if (ret != NO_ERROR)
                return result;

            // Read number of entries
            uint numEntries = (uint)Marshal.ReadInt32(buffer);
            int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
            IntPtr rowPtr = IntPtr.Add(buffer, 4); // Skip past dwNumEntries

            for (int i = 0; i < numEntries; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                result.Add(row);
                rowPtr = IntPtr.Add(rowPtr, rowSize);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    /// <summary>
    /// Retrieves all TCP6 rows with owning PID from GetExtendedTcpTable.
    /// </summary>
    private static List<MIB_TCP6ROW_OWNER_PID> GetAllTcp6Connections()
    {
        var result = new List<MIB_TCP6ROW_OWNER_PID>();
        uint size = 0;

        // First call to get buffer size
        uint ret = GetExtendedTcpTable(
            IntPtr.Zero, ref size, false,
            23, // AF_INET6
            TCP_TABLE_OWNER_PID_ALL, 0);

        if (ret != ERROR_INSUFFICIENT_BUFFER && ret != NO_ERROR)
            return result;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            ret = GetExtendedTcpTable(buffer, ref size, false, 23, TCP_TABLE_OWNER_PID_ALL, 0);
            if (ret != NO_ERROR)
                return result;

            // Read number of entries
            uint numEntries = (uint)Marshal.ReadInt32(buffer);
            int rowSize = Marshal.SizeOf<MIB_TCP6ROW_OWNER_PID>();
            IntPtr rowPtr = IntPtr.Add(buffer, 4); // Skip past dwNumEntries

            for (int i = 0; i < numEntries; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCP6ROW_OWNER_PID>(rowPtr);
                result.Add(row);
                rowPtr = IntPtr.Add(rowPtr, rowSize);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    /// <summary>
    /// Look up the owner PID for a given local UDP port.
    /// </summary>
    public static int GetOwnerPidForLocalUdpPort(ushort localPort)
    {
        uint targetPortNetwork = (uint)(((localPort & 0xFF) << 8) | ((localPort >> 8) & 0xFF));

        uint size = 0;
        uint ret = GetExtendedUdpTable(IntPtr.Zero, ref size, false, 2, UDP_TABLE_OWNER_PID, 0);
        if (ret != ERROR_INSUFFICIENT_BUFFER && ret != NO_ERROR)
            return 0;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            ret = GetExtendedUdpTable(buffer, ref size, false, 2, UDP_TABLE_OWNER_PID, 0);
            if (ret != NO_ERROR)
                return 0;

            uint numEntries = (uint)Marshal.ReadInt32(buffer);
            int rowSize = Marshal.SizeOf<MIB_UDPROW_OWNER_PID>();
            IntPtr rowPtr = IntPtr.Add(buffer, 4);

            for (int i = 0; i < numEntries; i++)
            {
                var row = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(rowPtr);
                if ((row.dwLocalPort & 0xFFFF) == targetPortNetwork)
                {
                    return (int)row.dwOwningPid;
                }
                rowPtr = IntPtr.Add(rowPtr, rowSize);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return 0;
    }

    #region Private Helpers

    private static string IpFromUint(uint ip)
    {
        // Network byte order -> IPAddress handles it
        return new IPAddress(ip).ToString();
    }

    private static int PortFromUint(uint port)
    {
        // Port is in network byte order, convert to host order
        return (int)(((port >> 8) & 0xFF) | ((port & 0xFF) << 8));
    }

    private static Shared.Models.TcpState MapTcpState(uint state)
    {
        return state switch
        {
            MIB_TCP_STATE_CLOSED => Shared.Models.TcpState.Closed,
            MIB_TCP_STATE_LISTEN => Shared.Models.TcpState.Listen,
            MIB_TCP_STATE_SYN_SENT => Shared.Models.TcpState.SynSent,
            MIB_TCP_STATE_SYN_RCVD => Shared.Models.TcpState.SynReceived,
            MIB_TCP_STATE_ESTAB => Shared.Models.TcpState.Established,
            MIB_TCP_STATE_FIN_WAIT1 => Shared.Models.TcpState.FinWait1,
            MIB_TCP_STATE_FIN_WAIT2 => Shared.Models.TcpState.FinWait2,
            MIB_TCP_STATE_CLOSE_WAIT => Shared.Models.TcpState.CloseWait,
            MIB_TCP_STATE_CLOSING => Shared.Models.TcpState.Closing,
            MIB_TCP_STATE_LAST_ACK => Shared.Models.TcpState.LastAck,
            MIB_TCP_STATE_TIME_WAIT => Shared.Models.TcpState.TimeWait,
            MIB_TCP_STATE_DELETE_TCB => Shared.Models.TcpState.DeleteTcb,
            _ => Shared.Models.TcpState.Closed
        };
    }

    #endregion
}
