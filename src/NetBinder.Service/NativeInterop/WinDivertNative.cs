using System;
using System.Runtime.InteropServices;

namespace NetBinder.Service.NativeInterop;

[StructLayout(LayoutKind.Sequential)]
public struct WINDIVERT_ADDRESS
{
    public long Timestamp;
    private ulong _data; // Layer:8, Event:8, Sniffed:1, Outbound:1, Loopback:1, Impostor:1, IPv6:1, IPChecksum:1, TCPChecksum:1, UDPChecksum:1, etc.
    public uint IfIdx;
    public uint SubIfIdx;

    public byte Layer => (byte)(_data & 0xFF);
    public byte Event => (byte)((_data >> 8) & 0xFF);
    public bool Sniffed => ((_data >> 16) & 1) != 0;
    public bool Outbound => ((_data >> 17) & 1) != 0;
    public bool Loopback => ((_data >> 18) & 1) != 0;
    public bool Impostor => ((_data >> 19) & 1) != 0;
    public bool IPv6 => ((_data >> 20) & 1) != 0;
    public bool IPChecksum => ((_data >> 21) & 1) != 0;
    public bool TCPChecksum => ((_data >> 22) & 1) != 0;
    public bool UDPChecksum => ((_data >> 23) & 1) != 0;

    public void SetOutbound(bool outbound)
    {
        if (outbound)
            _data |= (1UL << 17);
        else
            _data &= ~(1UL << 17);
    }

    public void SetLoopback(bool loopback)
    {
        if (loopback)
            _data |= (1UL << 18);
        else
            _data &= ~(1UL << 18);
    }
}

public static class WinDivertNative
{
    private const string WinDivertDll = "WinDivert.dll";

    // WinDivert Layer constants
    public const int WINDIVERT_LAYER_NETWORK = 0;
    public const int WINDIVERT_LAYER_NETWORK_FORWARD = 1;
    public const int WINDIVERT_LAYER_FLOW = 2;
    public const int WINDIVERT_LAYER_SOCKET = 3;
    public const int WINDIVERT_LAYER_REFLECT = 4;

    // WinDivert Flags
    public const ulong WINDIVERT_FLAG_SNIFF = 1;
    public const ulong WINDIVERT_FLAG_DROP = 2;
    public const ulong WINDIVERT_FLAG_RECV_ONLY = 4;
    public const ulong WINDIVERT_FLAG_SEND_ONLY = 8;
    public const ulong WINDIVERT_FLAG_NO_CHECKSUM = 16;

    [DllImport(WinDivertDll, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern IntPtr WinDivertOpen(
        [MarshalAs(UnmanagedType.LPStr)] string filter,
        int layer,
        short priority,
        ulong flags);

    [DllImport(WinDivertDll, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern bool WinDivertRecv(
        IntPtr handle,
        IntPtr packet,
        uint packetLen,
        out uint recvLen,
        ref WINDIVERT_ADDRESS addr);

    [DllImport(WinDivertDll, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern bool WinDivertSend(
        IntPtr handle,
        IntPtr packet,
        uint packetLen,
        out uint sendLen,
        ref WINDIVERT_ADDRESS addr);

    [DllImport(WinDivertDll, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern bool WinDivertClose(IntPtr handle);

    [DllImport(WinDivertDll, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern bool WinDivertHelperCalcChecksums(
        IntPtr packet,
        uint packetLen,
        ref WINDIVERT_ADDRESS addr,
        ulong flags);
}
