using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NetBinder.Service.NativeInterop;
using NetBinder.Shared.Models;

namespace NetBinder.Service.Services;

/// <summary>
/// Redirector service using WinDivert to transparently redirect bound app traffic
/// through a specific network interface via a local transparent proxy.
///
/// ARCHITECTURE:
/// 1. Capture outbound SYN from bound app (e.g., Brave -> google.com:443)
/// 2. Rewrite dst to 127.0.0.1:proxyPort AND set addr to loopback interface
/// 3. The transparent proxy receives the connection, looks up the original dest
/// 4. Proxy connects to google.com:443 via the TARGET interface (IP_UNICAST_IF + Bind)
/// 5. Capture inbound reply from proxy (127.0.0.1:proxyPort -> 127.0.0.1:clientPort)
/// 6. Rewrite src back to original remote (google.com:443) AND dst back to client IP
///    so the kernel's TCP state machine accepts the reply
/// 7. Set addr back to original interface for proper delivery
///
/// KEY INSIGHT: WinDivert's WinDivertSend uses addr.IfIdx to determine which interface
/// to inject the packet on. For loopback delivery, we MUST set IfIdx=1 (loopback)
/// and the Loopback flag=true. For reverse NAT, we must restore the original interface.
/// </summary>
public class RedirectorService : IDisposable
{
    private IntPtr _divertHandle = IntPtr.Zero;
    private Thread? _divertThread;
    private bool _isRunning;
    private int _proxyPort;

    /// <summary>
    /// NAT table entry: stores everything needed for bidirectional packet rewriting.
    /// </summary>
    private struct NatEntry
    {
        public IPEndPoint OriginalDest;       // Where the app wanted to connect (e.g., google.com:443)
        public IPAddress OriginalClientIp;    // Client's source IP on the physical interface
        public int TargetInterfaceIndex;      // Interface to route through
        public uint OriginalIfIdx;            // Original WinDivert interface index (for reverse NAT)
        public uint OriginalSubIfIdx;         // Original WinDivert sub-interface index
    }

    private readonly ConcurrentDictionary<ushort, NatEntry> _natTable = new();
    private readonly List<BindingMapping> _activeBindings = new();
    private readonly object _bindingsLock = new();
    private readonly ConcurrentDictionary<int, string> _pidPathCache = new();

    // Windows loopback interface index is always 1
    private const uint LOOPBACK_IFIDX = 1;

    public bool IsRunning => _isRunning;

    /// <summary>
    /// Looks up the NAT mapping for a given client local port.
    /// Used by the Transparent Proxy to know the real destination and target interface.
    /// </summary>
    public (IPEndPoint OriginalDest, int InterfaceIndex)? GetNATMapping(ushort localPort)
    {
        if (_natTable.TryGetValue(localPort, out var entry))
        {
            return (entry.OriginalDest, entry.TargetInterfaceIndex);
        }
        return null;
    }

    /// <summary>
    /// Updates active bindings. Flushes DNS and resets existing connections.
    /// </summary>
    public void UpdateBindings(IEnumerable<BindingMapping> bindings)
    {
        lock (_bindingsLock)
        {
            _activeBindings.Clear();
            foreach (var b in bindings)
            {
                if (b.IsActive)
                    _activeBindings.Add(b);
            }
            Console.WriteLine($"[RedirectorService] Updated active bindings. Count: {_activeBindings.Count}");
        }

        if (_isRunning)
            FlushDnsAndResetConnections();
    }

    private void FlushDnsAndResetConnections()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ipconfig",
                Arguments = "/flushdns",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit(3000);
            Console.WriteLine("[RedirectorService] Flushed Windows DNS cache.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RedirectorService] Warning: Failed to flush DNS: {ex.Message}");
        }

        try { ResetBoundProcessConnections(); }
        catch (Exception ex)
        {
            Console.WriteLine($"[RedirectorService] Warning: Failed to reset connections: {ex.Message}");
        }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint SetTcpEntry(ref MIB_TCPROW row);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
    }

    private const uint MIB_TCP_STATE_DELETE_TCB = 12;

    private void ResetBoundProcessConnections()
    {
        List<string> boundPaths;
        lock (_bindingsLock)
        {
            boundPaths = _activeBindings.Select(b => b.ExecutablePath).ToList();
        }
        if (boundPaths.Count == 0) return;

        var processes = TcpTableWrapper.GetProcessesWithConnections();
        int resetCount = 0;

        foreach (var proc in processes)
        {
            if (string.IsNullOrEmpty(proc.FilePath) || proc.FilePath.StartsWith("<")) continue;
            bool isBound = boundPaths.Any(bp => bp.Equals(proc.FilePath, StringComparison.OrdinalIgnoreCase));
            if (!isBound) continue;

            if (proc.Connections == null) continue;
            foreach (var conn in proc.Connections)
            {
                if (conn.State != Shared.Models.TcpState.Established) continue;
                try
                {
                    var localParts = conn.LocalEndPoint.Split(':');
                    var remoteParts = conn.RemoteEndPoint.Split(':');
                    if (localParts.Length < 2 || remoteParts.Length < 2) continue;

                    var localIp = IPAddress.Parse(localParts[0]);
                    int localPort = int.Parse(localParts[^1]);
                    var remoteIp = IPAddress.Parse(remoteParts[0]);
                    int remotePort = int.Parse(remoteParts[^1]);

                    if (localIp.Equals(IPAddress.Loopback)) continue;

                    var row = new MIB_TCPROW
                    {
                        dwState = MIB_TCP_STATE_DELETE_TCB,
                        dwLocalAddr = BitConverter.ToUInt32(localIp.GetAddressBytes(), 0),
                        dwLocalPort = (uint)IPAddress.HostToNetworkOrder((short)localPort),
                        dwRemoteAddr = BitConverter.ToUInt32(remoteIp.GetAddressBytes(), 0),
                        dwRemotePort = (uint)IPAddress.HostToNetworkOrder((short)remotePort)
                    };

                    if (SetTcpEntry(ref row) == 0) resetCount++;
                }
                catch { }
            }
        }

        if (resetCount > 0)
            Console.WriteLine($"[RedirectorService] Reset {resetCount} existing TCP connections.");
    }

    /// <summary>
    /// Starts the WinDivert packet capture and redirection loop.
    /// </summary>
    public bool Start(int proxyPort)
    {
        if (_isRunning) return true;

        _proxyPort = proxyPort;
        _isRunning = true;

        // Filter:
        // 1. Outbound TCP (except to/from our proxy port) - for SYN interception and NAT
        // 2. TCP packets from our proxy port - for reverse NAT
        // 3. Outbound UDP to port 443 - for QUIC blocking
        // 4. Outbound IPv6 TCP/UDP - for IPv6 blocking of bound apps
        string filter = $"(outbound and ip and tcp and tcp.DstPort != {proxyPort} and tcp.SrcPort != {proxyPort}) or (ip and tcp and tcp.SrcPort == {proxyPort}) or (outbound and (ip or ipv6) and udp and udp.DstPort == 443) or (outbound and ipv6 and tcp)";

        Console.WriteLine($"[RedirectorService] Opening WinDivert with filter: {filter}");

        _divertHandle = WinDivertNative.WinDivertOpen(
            filter,
            WinDivertNative.WINDIVERT_LAYER_NETWORK,
            0,
            0
        );

        if (_divertHandle == IntPtr.Zero || _divertHandle == new IntPtr(-1))
        {
            int err = Marshal.GetLastWin32Error();
            Console.WriteLine($"[RedirectorService] Failed to open WinDivert handle. Win32 Error: {err}");
            _isRunning = false;
            return false;
        }

        _divertThread = new Thread(PacketLoop)
        {
            IsBackground = true,
            Name = "NetBinderWinDivertThread"
        };
        _divertThread.Start();

        Console.WriteLine("[RedirectorService] Started successfully.");
        return true;
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;
        Console.WriteLine("[RedirectorService] Stopping...");

        if (_divertHandle != IntPtr.Zero)
        {
            WinDivertNative.WinDivertClose(_divertHandle);
            _divertHandle = IntPtr.Zero;
        }

        if (_divertThread != null)
        {
            if (!_divertThread.Join(TimeSpan.FromSeconds(3)))
                Console.WriteLine("[RedirectorService] Warning: Thread did not stop gracefully.");
            _divertThread = null;
        }

        _natTable.Clear();
        _pidPathCache.Clear();
        Console.WriteLine("[RedirectorService] Stopped.");
    }

    private void PacketLoop()
    {
        const int bufferSize = 65536;
        IntPtr pPacketBuffer = Marshal.AllocHGlobal(bufferSize);
        WINDIVERT_ADDRESS addr = new WINDIVERT_ADDRESS();

        try
        {
            while (_isRunning)
            {
                if (!WinDivertNative.WinDivertRecv(_divertHandle, pPacketBuffer, bufferSize, out uint recvLen, ref addr))
                {
                    if (!_isRunning) break;
                    int err = Marshal.GetLastWin32Error();
                    if (err != 995) // ERROR_OPERATION_ABORTED (expected on close)
                        Console.WriteLine($"[RedirectorService] WinDivertRecv failed. Error: {err}");
                    Thread.Sleep(1);
                    continue;
                }

                ProcessPacket(pPacketBuffer, recvLen, ref addr);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RedirectorService] Fatal error in packet loop: {ex}");
        }
        finally
        {
            Marshal.FreeHGlobal(pPacketBuffer);
        }
    }

    private unsafe void ProcessPacket(IntPtr pPacketBuffer, uint recvLen, ref WINDIVERT_ADDRESS addr)
    {
        byte* packet = (byte*)pPacketBuffer.ToPointer();
        byte ipVersion = (byte)((packet[0] >> 4) & 0x0F);

        // IPv6: block TCP SYN + QUIC from bound apps
        if (ipVersion == 6)
        {
            HandleIPv6(packet, recvLen, pPacketBuffer, ref addr);
            return;
        }

        if (ipVersion != 4)
        {
            WinDivertNative.WinDivertSend(_divertHandle, pPacketBuffer, recvLen, out _, ref addr);
            return;
        }

        byte ipProto = packet[9];

        // UDP: block QUIC for bound apps
        if (ipProto == 17)
        {
            HandleUdp(packet, recvLen, pPacketBuffer, ref addr);
            return;
        }

        if (ipProto != 6) // Not TCP
        {
            WinDivertNative.WinDivertSend(_divertHandle, pPacketBuffer, recvLen, out _, ref addr);
            return;
        }

        // TCP processing
        int ipHeaderLen = (packet[0] & 0x0F) * 4;
        ushort srcPort = (ushort)((packet[ipHeaderLen] << 8) | packet[ipHeaderLen + 1]);
        ushort dstPort = (ushort)((packet[ipHeaderLen + 2] << 8) | packet[ipHeaderLen + 3]);
        byte tcpFlags = packet[ipHeaderLen + 13];

        if (addr.Loopback)
        {
            // LOOPBACK PACKET: This could be from our proxy replying to the client.
            // Check if it's from our proxy port and has a NAT entry.
            if (srcPort == (ushort)_proxyPort && _natTable.TryGetValue(dstPort, out var natEntry))
            {
                // REVERSE NAT: Rewrite source to look like original remote server
                byte[] origIpBytes = natEntry.OriginalDest.Address.GetAddressBytes();
                packet[12] = origIpBytes[0]; // src IP = original remote IP
                packet[13] = origIpBytes[1];
                packet[14] = origIpBytes[2];
                packet[15] = origIpBytes[3];

                // Restore destination to original client IP
                byte[] clientIpBytes = natEntry.OriginalClientIp.GetAddressBytes();
                packet[16] = clientIpBytes[0]; // dst IP = original client IP
                packet[17] = clientIpBytes[1];
                packet[18] = clientIpBytes[2];
                packet[19] = clientIpBytes[3];

                // Rewrite source port to original remote port
                ushort origPort = (ushort)natEntry.OriginalDest.Port;
                packet[ipHeaderLen] = (byte)(origPort >> 8);
                packet[ipHeaderLen + 1] = (byte)(origPort & 0xFF);

                // Restore addr to original physical interface
                addr.IfIdx = natEntry.OriginalIfIdx;
                addr.SubIfIdx = natEntry.OriginalSubIfIdx;
                addr.SetLoopback(false);
                addr.SetOutbound(false); // Mark as inbound so Windows TCP stack delivers it to the client
                // Keep as inbound (proxy -> client)

                WinDivertNative.WinDivertHelperCalcChecksums(pPacketBuffer, recvLen, ref addr, 0);

                if ((tcpFlags & (0x01 | 0x04)) != 0)
                    ScheduleNatCleanup(dstPort);

                Console.WriteLine($"[RedirectorService] Reverse NAT: proxy:{srcPort} -> {natEntry.OriginalDest} for client port {dstPort}");
            }

            // Re-inject (modified or not)
            WinDivertNative.WinDivertSend(_divertHandle, pPacketBuffer, recvLen, out _, ref addr);
            return;
        }

        // NON-LOOPBACK PACKET
        if (addr.Outbound)
        {
            // Check if dst is 127.x.x.x (skip our own rewritten loopback traffic that leaked)
            if (packet[16] == 127)
            {
                WinDivertNative.WinDivertSend(_divertHandle, pPacketBuffer, recvLen, out _, ref addr);
                return;
            }

            bool isNatted = _natTable.ContainsKey(srcPort);

            if (isNatted)
            {
                // Already tracked: redirect to our proxy on loopback
                // Rewrite both IPs to 127.0.0.1
                packet[12] = 127; packet[13] = 0; packet[14] = 0; packet[15] = 1; // src = 127.0.0.1
                packet[16] = 127; packet[17] = 0; packet[18] = 0; packet[19] = 1; // dst = 127.0.0.1

                // Rewrite dst port to proxy port
                packet[ipHeaderLen + 2] = (byte)(_proxyPort >> 8);
                packet[ipHeaderLen + 3] = (byte)(_proxyPort & 0xFF);

                // CRITICAL: Set addr to loopback interface so WinDivert delivers correctly
                addr.IfIdx = LOOPBACK_IFIDX;
                addr.SubIfIdx = 0;
                addr.SetLoopback(true);

                WinDivertNative.WinDivertHelperCalcChecksums(pPacketBuffer, recvLen, ref addr, 0);

                if ((tcpFlags & (0x01 | 0x04)) != 0)
                    ScheduleNatCleanup(srcPort);
            }
            else
            {
                // Check for new SYN from bound app
                bool isSyn = (tcpFlags & 0x02) != 0 && (tcpFlags & 0x10) == 0;
                if (isSyn)
                {
                    int pid = TcpTableWrapper.GetOwnerPidForLocalPort(srcPort);
                    if (pid > 0)
                    {
                        string? exePath = GetProcessPath(pid);
                        int? targetInterfaceIndex = MatchBinding(exePath);

                        if (targetInterfaceIndex.HasValue)
                        {
                            // Save NAT entry with all the info needed for bidirectional rewriting
                            uint origDstIpRaw = *(uint*)(packet + 16);
                            uint origSrcIpRaw = *(uint*)(packet + 12);

                            var natEntry = new NatEntry
                            {
                                OriginalDest = new IPEndPoint(new IPAddress(origDstIpRaw), dstPort),
                                OriginalClientIp = new IPAddress(origSrcIpRaw),
                                TargetInterfaceIndex = targetInterfaceIndex.Value,
                                OriginalIfIdx = addr.IfIdx,
                                OriginalSubIfIdx = addr.SubIfIdx
                            };

                            _natTable[srcPort] = natEntry;
                            Console.WriteLine($"[RedirectorService] NAT SYN: port={srcPort}, PID={pid}, dest={natEntry.OriginalDest}, via interface {targetInterfaceIndex.Value}");

                            // Rewrite to loopback proxy
                            packet[12] = 127; packet[13] = 0; packet[14] = 0; packet[15] = 1;
                            packet[16] = 127; packet[17] = 0; packet[18] = 0; packet[19] = 1;
                            packet[ipHeaderLen + 2] = (byte)(_proxyPort >> 8);
                            packet[ipHeaderLen + 3] = (byte)(_proxyPort & 0xFF);

                            addr.IfIdx = LOOPBACK_IFIDX;
                            addr.SubIfIdx = 0;
                            addr.SetLoopback(true);

                            WinDivertNative.WinDivertHelperCalcChecksums(pPacketBuffer, recvLen, ref addr, 0);
                        }
                    }
                }
            }

            WinDivertNative.WinDivertSend(_divertHandle, pPacketBuffer, recvLen, out _, ref addr);
        }
        else
        {
            // Inbound non-loopback: just pass through
            WinDivertNative.WinDivertSend(_divertHandle, pPacketBuffer, recvLen, out _, ref addr);
        }
    }

    private unsafe void HandleIPv6(byte* packet, uint recvLen, IntPtr pPacketBuffer, ref WINDIVERT_ADDRESS addr)
    {
        if (addr.Outbound)
        {
            byte nextHeader = packet[6];

            if (nextHeader == 6) // TCP
            {
                ushort srcPort = (ushort)((packet[40] << 8) | packet[41]);
                byte tcpFlags = packet[40 + 13];
                bool isSyn = (tcpFlags & 0x02) != 0 && (tcpFlags & 0x10) == 0;

                if (isSyn)
                {
                    int pid = TcpTableWrapper.GetOwnerPidForLocalPort(srcPort);
                    if (pid > 0 && MatchBinding(GetProcessPath(pid)).HasValue)
                    {
                        Console.WriteLine($"[RedirectorService] Blocked IPv6 TCP SYN. PID={pid}, Port={srcPort}");
                        return; // DROP
                    }
                }
            }
            else if (nextHeader == 17) // UDP
            {
                ushort udpDstPort = (ushort)((packet[40 + 2] << 8) | packet[40 + 3]);
                if (udpDstPort == 443)
                {
                    ushort udpSrcPort = (ushort)((packet[40] << 8) | packet[41]);
                    int pid = TcpTableWrapper.GetOwnerPidForLocalUdpPort(udpSrcPort);
                    if (pid > 0 && MatchBinding(GetProcessPath(pid)).HasValue)
                    {
                        Console.WriteLine($"[RedirectorService] Blocked IPv6 QUIC. PID={pid}");
                        return; // DROP
                    }
                }
            }
        }

        WinDivertNative.WinDivertSend(_divertHandle, pPacketBuffer, recvLen, out _, ref addr);
    }

    private unsafe void HandleUdp(byte* packet, uint recvLen, IntPtr pPacketBuffer, ref WINDIVERT_ADDRESS addr)
    {
        if (addr.Outbound)
        {
            int ipHdrLen = (packet[0] & 0x0F) * 4;
            ushort udpDstPort = (ushort)((packet[ipHdrLen + 2] << 8) | packet[ipHdrLen + 3]);

            if (udpDstPort == 443)
            {
                ushort udpSrcPort = (ushort)((packet[ipHdrLen] << 8) | packet[ipHdrLen + 1]);
                int pid = TcpTableWrapper.GetOwnerPidForLocalUdpPort(udpSrcPort);
                if (pid > 0 && MatchBinding(GetProcessPath(pid)).HasValue)
                {
                    Console.WriteLine($"[RedirectorService] Blocked QUIC (UDP 443). PID={pid}, Port={udpSrcPort}");
                    return; // DROP
                }
            }
        }
        WinDivertNative.WinDivertSend(_divertHandle, pPacketBuffer, recvLen, out _, ref addr);
    }

    private void ScheduleNatCleanup(ushort localPort)
    {
        _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(t =>
        {
            if (_natTable.TryRemove(localPort, out _))
                Console.WriteLine($"[RedirectorService] NAT cleanup: port {localPort}");
        });
    }

    private string? GetProcessPath(int pid)
    {
        if (_pidPathCache.TryGetValue(pid, out var cachedPath))
            return cachedPath;

        try
        {
            using var process = Process.GetProcessById(pid);
            string? path = process.MainModule?.FileName;
            if (!string.IsNullOrEmpty(path))
            {
                _pidPathCache[pid] = path;
                return path;
            }
        }
        catch { }
        return null;
    }

    private int? MatchBinding(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;

        string exeName = Path.GetFileName(exePath);
        string nameWithoutExt = Path.GetFileNameWithoutExtension(exePath);

        lock (_bindingsLock)
        {
            foreach (var binding in _activeBindings)
            {
                if (binding.ExecutablePath.Equals(exePath, StringComparison.OrdinalIgnoreCase) ||
                    binding.ProcessName.Equals(exeName, StringComparison.OrdinalIgnoreCase) ||
                    binding.ProcessName.Equals(nameWithoutExt, StringComparison.OrdinalIgnoreCase))
                {
                    if (binding.RoutingMethod == RoutingMethod.Transparent)
                    {
                        return binding.InterfaceIndex;
                    }
                }
            }
        }
        return null;
    }

    public void Dispose()
    {
        Stop();
    }
}
