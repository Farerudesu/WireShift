namespace NetBinder.Shared.Models;

/// <summary>
/// Represents a running process that has active network connections.
/// Populated from GetExtendedTcpTable (iphlpapi.dll) + Process.GetProcessById.
/// </summary>
public class NetworkProcessInfo
{
    public int Pid { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;            // Full executable path
    public int ActiveConnectionCount { get; set; }                   // Number of active TCP connections
    public List<ConnectionInfo> Connections { get; set; } = [];
    public bool IsBound { get; set; }                                // Whether this process has a binding rule
    public string? BoundInterfaceName { get; set; }                  // Interface it's bound to (if any)
}

/// <summary>
/// Details of a single network connection belonging to a process.
/// </summary>
public class ConnectionInfo
{
    public string LocalEndPoint { get; set; } = string.Empty;   // IP:Port
    public string RemoteEndPoint { get; set; } = string.Empty;  // IP:Port
    public TcpState State { get; set; }
}

public enum TcpState
{
    Closed = 1,
    Listen = 2,
    SynSent = 3,
    SynReceived = 4,
    Established = 5,
    FinWait1 = 6,
    FinWait2 = 7,
    CloseWait = 8,
    Closing = 9,
    LastAck = 10,
    TimeWait = 11,
    DeleteTcb = 12
}
