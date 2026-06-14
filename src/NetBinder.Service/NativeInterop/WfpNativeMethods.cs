using System.Runtime.InteropServices;

namespace NetBinder.Service.NativeInterop;

/// <summary>
/// P/Invoke bindings for the Windows Filtering Platform (WFP) user-mode API (fwpuclnt.dll).
///
/// These bindings enable the NetBinder Service to:
/// - Open/close a WFP engine session
/// - Add/remove sublayers and filters
/// - Create filters at FWPM_LAYER_ALE_AUTH_CONNECT_V4/V6 to intercept outbound connections
/// - Use FwpmGetAppIdFromFileName0 to convert an executable path to a WFP app ID
///
/// LIMITATION (documented for Phase 3 fallback):
/// WFP user-mode API can only PERMIT or BLOCK connections at the ALE layer.
/// It CANNOT modify socket options (like IP_UNICAST_IF) from user mode.
/// True per-app interface binding requires a kernel-mode callout driver that
/// modifies the socket's IP_UNICAST_IF option during ALE_AUTH_CONNECT classification.
/// Since we're staying in user mode, our WFP filters can BLOCK traffic from bound apps
/// on the wrong interface (negative filtering), but cannot REDIRECT traffic to a
/// specific interface. The SOCKS5 proxy in Phase 3 serves as the user-mode fallback
/// for actual traffic redirection.
///
/// Current approach: WFP filters are used as a COMPLEMENT to the proxy:
/// - When a binding is active, WFP blocks the app's traffic on all interfaces
///   EXCEPT the target one (by blocking at ALE_AUTH_CONNECT for non-target routes).
/// - The app is then expected to use the SOCKS5 proxy bound to the target interface
///   for its outbound connections.
/// - This is a "permit-only-on-target-interface" approach, not true socket-level binding.
/// </summary>
public static class WfpNativeMethods
{
    private const string Fwpuclnt = "fwpuclnt.dll";

    #region WFP Error Codes (HRESULT)

    public const uint FWP_E_ALREADY_EXISTS = 0x80320008;
    public const uint FWP_E_NOT_FOUND = 0x80320009;
    public const uint FWP_E_IN_USE = 0x8032000A;
    public const uint FWP_E_INVALID_SESSION = 0x8032000C;
    public const uint FWP_E_ENGINE_NOT_OPEN = 0x8032000E;

    #endregion

    #region WFP Constants

    /// <summary>RPC_C_AUTHN_WINNT - Use Windows NT authentication for the WFP session.</summary>
    public const uint RPC_C_AUTHN_WINNT = 10;

    /// <summary>FWP_EMPTY - Empty/zero value for optional fields.</summary>
    public static readonly Guid FWP_EMPTY_GUID = Guid.Empty;

    /// <summary>FWPM_LAYER_ALE_AUTH_CONNECT_V4 - Outbound IPv4 connection authorization.</summary>
    public static readonly Guid FWPM_LAYER_ALE_AUTH_CONNECT_V4 = new("c38d57d1-05a7-4c33-904f-7fbceee60382");

    /// <summary>FWPM_LAYER_ALE_AUTH_CONNECT_V6 - Outbound IPv6 connection authorization.</summary>
    public static readonly Guid FWPM_LAYER_ALE_AUTH_CONNECT_V6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6e1");

    /// <summary>FWPM_CONDITION_ALE_APP_ID - Condition key for the application ID.</summary>
    public static readonly Guid FWPM_CONDITION_ALE_APP_ID = new("d78e1e87-8644-4ea5-9437-d809ecefc971");

    /// <summary>NetBinder sublayer GUID - unique identifier for our sublayer.</summary>
    public static readonly Guid NetBinderSubLayerKey = new("B5F0A2C4-7E3D-4A1F-9B8C-6D2E0F1A3C5E");

    /// <summary>FWP_MATCH_EQUAL - Condition match type: exact equality.</summary>
    public const uint FWP_MATCH_EQUAL = 0;

    /// <summary>FWP_ACTION_BLOCK - Filter action: block the connection.</summary>
    public const uint FWP_ACTION_BLOCK = 0x20000001;

    /// <summary>FWP_ACTION_PERMIT - Filter action: permit the connection.</summary>
    public const uint FWP_ACTION_PERMIT = 0x10000001;

    /// <summary>FWP_ACTION_CALLOUT_UNKNOWN - Filter action: invoke a callout (result unknown).</summary>
    public const uint FWP_ACTION_CALLOUT_UNKNOWN = 0x30000001;

    /// <summary>FWP_DATA_TYPE_BLOB - Data type for byte array (app ID).</summary>
    public const uint FWP_DATA_TYPE_BLOB = 5;

    /// <summary>FWP_DATA_TYPE_UINT32 - Data type for 32-bit unsigned integer.</summary>
    public const uint FWP_DATA_TYPE_UINT32 = 2;

    /// <summary>FWP_DATA_TYPE_BYTE_ARRAY_16 - Data type for 16-byte array (IPv6 address).</summary>
    public const uint FWP_DATA_TYPE_BYTE_ARRAY_16 = 3;

    /// <summary>FWP_DATA_TYPE_UINT8 - Data type for 8-bit unsigned integer.</summary>
    public const uint FWP_DATA_TYPE_UINT8 = 0;

    #endregion

    #region P/Invoke Declarations

    /// <summary>Opens a session to the WFP filter engine.</summary>
    [DllImport(Fwpuclnt, CharSet = CharSet.Unicode)]
    public static extern uint FwpmEngineOpen0(
        string? serverName,
        uint authnService,
        IntPtr authnIdentity,
        IntPtr session,
        out IntPtr engineHandle);

    /// <summary>Closes a WFP engine session.</summary>
    [DllImport(Fwpuclnt)]
    public static extern uint FwpmEngineClose0(IntPtr engineHandle);

    /// <summary>Begins a transaction within the WFP engine session.</summary>
    [DllImport(Fwpuclnt)]
    public static extern uint FwpmTransactionBegin0(IntPtr engineHandle, uint flags);

    /// <summary>Commits a transaction within the WFP engine session.</summary>
    [DllImport(Fwpuclnt)]
    public static extern uint FwpmTransactionCommit0(IntPtr engineHandle);

    /// <summary>Aborts a transaction within the WFP engine session.</summary>
    [DllImport(Fwpuclnt)]
    public static extern uint FwpmTransactionAbort0(IntPtr engineHandle);

    /// <summary>Adds a sublayer to the WFP engine.</summary>
    [DllImport(Fwpuclnt)]
    public static extern uint FwpmSubLayerAdd0(IntPtr engineHandle, ref FWPM_SUBLAYER0 subLayer, IntPtr sd);

    /// <summary>Deletes a sublayer by key from the WFP engine.</summary>
    [DllImport(Fwpuclnt)]
    public static extern uint FwpmSubLayerDeleteByKey0(IntPtr engineHandle, ref Guid subLayerKey);

    /// <summary>Adds a filter to the WFP engine. Returns the filter ID.</summary>
    [DllImport(Fwpuclnt)]
    public static extern uint FwpmFilterAdd0(IntPtr engineHandle, ref FWPM_FILTER0 filter, IntPtr sd, out ulong filterId);

    /// <summary>Deletes a filter by ID from the WFP engine.</summary>
    [DllImport(Fwpuclnt)]
    public static extern uint FwpmFilterDeleteById0(IntPtr engineHandle, ulong filterId);

    /// <summary>
    /// Converts a file path to a WFP application ID (FWP_BYTE_BLOB).
    /// The returned blob must be freed with FwpmFreeMemory0.
    /// </summary>
    [DllImport(Fwpuclnt, CharSet = CharSet.Unicode)]
    public static extern uint FwpmGetAppIdFromFileName0(
        string fileName,
        out IntPtr appIdBlob);

    /// <summary>Frees memory allocated by WFP API calls (e.g., FwpmGetAppIdFromFileName0).</summary>
    [DllImport(Fwpuclnt)]
    public static extern void FwpmFreeMemory0(ref IntPtr pMemory);

    #endregion

    #region WFP Structures

    // -----------------------------------------------------------------------
    // All struct layouts match the Windows SDK (fwpmtypes.h / fwptypes.h).
    // Field order and types MUST match exactly to avoid AccessViolationException.
    // -----------------------------------------------------------------------

    /// <summary>
    /// FWPM_DISPLAY_DATA0 — display name + description strings.
    /// SDK: typedef struct FWPM_DISPLAY_DATA0_ { PWSTR name; PWSTR description; }
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct FWPM_DISPLAY_DATA0
    {
        public string? Name;        // PWSTR
        public string? Description; // PWSTR
    }

    /// <summary>
    /// FWPM_SUBLAYER0 — WFP sublayer descriptor.
    /// SDK (iketypes.h): subLayerKey, displayData, flags, providerData(*), weight
    /// Note: NO providerKey field in the actual SDK struct.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct FWPM_SUBLAYER0
    {
        public Guid SubLayerKey;            // GUID
        public FWPM_DISPLAY_DATA0 DisplayData; // FWPM_DISPLAY_DATA0
        public uint Flags;                  // UINT32
        public IntPtr ProviderData;         // FWP_BYTE_BLOB* (pointer, may be NULL)
        public ushort Weight;               // UINT16
    }

    /// <summary>
    /// FWPM_FILTER0 — WFP filter descriptor.
    /// SDK (fwpmtypes.h): all blob/value/context fields are POINTERS, not inline structs.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct FWPM_FILTER0
    {
        public Guid FilterKey;                  // GUID
        public FWPM_DISPLAY_DATA0 DisplayData;  // FWPM_DISPLAY_DATA0
        public uint Flags;                      // UINT32
        public IntPtr ProviderKey;              // GUID* (pointer, may be NULL)
        public IntPtr ProviderData;             // FWP_BYTE_BLOB* (pointer, may be NULL)
        public Guid LayerKey;                   // GUID
        public Guid SubLayerKey;                // GUID
        public IntPtr Weight;                   // FWP_VALUE0* (pointer, NULL = auto-weight)
        public uint NumFilterConditions;         // UINT32
        public IntPtr FilterConditions;          // FWPM_FILTER_CONDITION0* (pointer)
        public FWPM_ACTION0 Action;             // FWPM_ACTION0
        public IntPtr ProviderContextKey;        // GUID* (pointer, union with providerContextId — use NULL)
        public IntPtr Reserved;                  // GUID* (reserved, must be NULL)
        public ulong FilterId;                   // UINT64 (output — filled by FwpmFilterAdd0)
        public IntPtr EffectiveWeight;           // FWP_VALUE0* (output pointer — filled by engine)
    }

    /// <summary>
    /// FWPM_FILTER_CONDITION0 — a single condition in a filter.
    /// SDK: fieldKey, matchType, conditionValue
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FWPM_FILTER_CONDITION0
    {
        public Guid ConditionKey;               // GUID
        public uint MatchType;                  // FWP_MATCH_TYPE (UINT32)
        public FWP_CONDITION_VALUE0 ConditionValue; // FWP_CONDITION_VALUE0
    }

    /// <summary>
    /// FWPM_ACTION0 — action to perform when a filter matches.
    /// SDK: type (UINT32), union { filterType (GUID), calloutKey (GUID) }
    /// FilterType and CalloutKey are a union (same memory location).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FWPM_ACTION0
    {
        public uint Type;           // FWP_ACTION_TYPE (UINT32)
        public Guid FilterTypeKey;  // GUID union (also covers CalloutKey — same bytes)
    }

    /// <summary>
    /// FWP_CONDITION_VALUE0 — the value side of a filter condition.
    /// SDK: type (FWP_DATA_TYPE), union of value types.
    /// The union immediately follows the type field.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FWP_CONDITION_VALUE0
    {
        public uint Type;           // FWP_DATA_TYPE (UINT32)
        public FWP_VALUE_UNION Value; // union of typed values
    }

    /// <summary>
    /// FWP_VALUE0 — a generic typed WFP value.
    /// SDK: type (FWP_DATA_TYPE), union of value types.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FWP_VALUE0
    {
        public uint Type;           // FWP_DATA_TYPE (UINT32)
        public FWP_VALUE_UNION Union; // union of typed values
    }

    /// <summary>
    /// FWP_VALUE_UNION — explicit layout union for WFP value types.
    /// All members share offset 0 (pointer-sized slot on the platform).
    /// On 64-bit: the union is 8 bytes; on 32-bit: 4 bytes.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct FWP_VALUE_UNION
    {
        [FieldOffset(0)] public byte Uint8;
        [FieldOffset(0)] public ushort Uint16;
        [FieldOffset(0)] public uint Uint32;
        [FieldOffset(0)] public IntPtr Uint64Ptr;    // UINT64* (pointer to heap-allocated UINT64)
        [FieldOffset(0)] public IntPtr BlobPtr;      // FWP_BYTE_BLOB* (used for app ID)
        [FieldOffset(0)] public IntPtr ByteArray16;  // FWP_BYTE_ARRAY16*
    }

    /// <summary>
    /// FWP_BYTE_BLOB — a simple counted byte buffer.
    /// SDK: size (UINT32), data (UINT8*).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FWP_BYTE_BLOB
    {
        public uint Size;    // UINT32
        public IntPtr Data;  // UINT8* — points to the byte array
    }

    #endregion

    /// <summary>
    /// Helper: checks if a WFP HRESULT indicates success.
    /// </summary>
    public static bool Succeeded(uint hr) => hr == 0;

    /// <summary>
    /// Helper: converts a WFP HRESULT to a human-readable error description.
    /// </summary>
    public static string GetErrorString(uint hr)
    {
        return hr switch
        {
            0 => "Success",
            FWP_E_ALREADY_EXISTS => "The object already exists",
            FWP_E_NOT_FOUND => "The object was not found",
            FWP_E_IN_USE => "The object is in use and cannot be deleted",
            FWP_E_INVALID_SESSION => "The session handle is invalid",
            FWP_E_ENGINE_NOT_OPEN => "The WFP engine is not open",
            _ => $"WFP error 0x{hr:X8}"
        };
    }
}
