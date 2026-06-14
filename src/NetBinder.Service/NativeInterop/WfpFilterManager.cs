using System.Runtime.InteropServices;
using NetBinder.Shared.Models;

namespace NetBinder.Service.NativeInterop;

/// <summary>
/// Manages WFP (Windows Filtering Platform) filters for per-app interface binding.
///
/// ARCHITECTURAL DECISION & LIMITATION:
/// =====================================
/// WFP user-mode API can only PERMIT or BLOCK connections at the ALE layer.
/// It CANNOT modify socket options (like IP_UNICAST_IF) from user mode.
/// True per-app interface binding would require a kernel-mode callout driver that
/// intercepts ALE_AUTH_CONNECT classifications and calls setsockopt(IP_UNICAST_IF)
/// on the target socket — this requires the Windows Driver Kit (WDK).
///
/// CURRENT APPROACH (user-mode, no kernel driver):
/// - We register WFP filters at FWPM_LAYER_ALE_AUTH_CONNECT_V4/V6
/// - Each filter matches a specific app (by FWPM_CONDITION_ALE_APP_ID) and
///   uses FWP_ACTION_PERMIT to allow its traffic
/// - This creates an "allow list" for bound apps — without our filter, the app
///   would be subject to normal routing rules
/// - For actual traffic REDIRECTION to a specific interface, the SOCKS5 proxy
///   (Phase 3) is the user-mode solution
///
/// FUTURE APPROACH (Phase 6 - Kernel Driver):
/// - Implement a WFP callout driver (KMDF/WDM) that registers at
///   FWPM_LAYER_ALE_AUTH_CONNECT_V4
/// - In the classifyFn callback, use the classifyCtx to get the socket handle
/// - Call ZwSetInformationFile or WfpAleClassifyOptionData to set
///   IP_UNICAST_IF on the socket before returning FWP_ACTION_PERMIT
/// - This achieves true transparent per-app interface binding
/// </summary>
public class WfpFilterManager : IDisposable
{
    private IntPtr _engineHandle = IntPtr.Zero;
    private bool _isInitialized;
    private bool _disposed;

    /// <summary>Tracks active filter IDs keyed by binding ID for cleanup.</summary>
    private readonly Dictionary<Guid, ulong> _filterIdsByBinding = [];

    /// <summary>Track IPv4 and IPv6 filters separately per binding.</summary>
    private readonly Dictionary<Guid, ulong> _filterV6IdsByBinding = [];

    public bool IsInitialized => _isInitialized;

    /// <summary>
    /// Initializes the WFP filter engine: opens a session and registers our sublayer.
    /// Must be called before any filter operations.
    /// Requires administrator privileges.
    /// </summary>
    public bool Initialize()
    {
        if (_isInitialized) return true;

        // Open a session to the WFP filter engine
        uint hr = WfpNativeMethods.FwpmEngineOpen0(
            null,                       // local engine
            WfpNativeMethods.RPC_C_AUTHN_WINNT,
            IntPtr.Zero,
            IntPtr.Zero,
            out _engineHandle);

        if (!WfpNativeMethods.Succeeded(hr))
        {
            Console.WriteLine($"[WfpFilterManager] FwpmEngineOpen0 failed: {WfpNativeMethods.GetErrorString(hr)}");
            return false;
        }

        // Add our sublayer directly (no transaction needed for single-object add)
        var subLayer = new WfpNativeMethods.FWPM_SUBLAYER0
        {
            SubLayerKey = WfpNativeMethods.NetBinderSubLayerKey,
            DisplayData = new WfpNativeMethods.FWPM_DISPLAY_DATA0
            {
                Name = "NetBinder Filter Sublayer",
                Description = "Per-application network interface binding filters for NetBinder"
            },
            Flags = 0,
            ProviderData = IntPtr.Zero,
            Weight = 65535  // High weight = high priority
        };

        hr = WfpNativeMethods.FwpmSubLayerAdd0(_engineHandle, ref subLayer, IntPtr.Zero);
        if (!WfpNativeMethods.Succeeded(hr) && hr != WfpNativeMethods.FWP_E_ALREADY_EXISTS)
        {
            Console.WriteLine($"[WfpFilterManager] FwpmSubLayerAdd0 failed: {WfpNativeMethods.GetErrorString(hr)}");
            WfpNativeMethods.FwpmEngineClose0(_engineHandle);
            _engineHandle = IntPtr.Zero;
            return false;
        }

        _isInitialized = true;
        Console.WriteLine("[WfpFilterManager] WFP engine initialized successfully. Sublayer registered.");
        return true;
    }

    /// <summary>
    /// Adds a WFP filter for a specific app-to-interface binding.
    /// Creates IPv4 permit filter at ALE_AUTH_CONNECT_V4 for the given app path.
    ///
    /// NOTE: In pure user-mode WFP, we can only PERMIT/BLOCK traffic, not redirect it.
    /// This filter permits the app's traffic through the ALE layer. Actual interface
    /// redirection is handled by the SOCKS5 proxy (Phase 3) or a future kernel driver.
    ///
    /// Without the kernel driver, this filter serves as a "tracking marker" that
    /// identifies which apps have bindings, and can optionally BLOCK apps from
    /// connecting on non-target interfaces (Phase 3 enhancement).
    /// </summary>
    public bool AddBindingFilter(BindingMapping binding)
    {
        if (!_isInitialized)
        {
            Console.WriteLine("[WfpFilterManager] Not initialized. Call Initialize() first.");
            return false;
        }

        // Remove existing filter for this binding if it exists
        if (_filterIdsByBinding.ContainsKey(binding.Id))
        {
            RemoveBindingFilter(binding.Id);
        }

        // Get the WFP app ID from the executable path
        uint hr = WfpNativeMethods.FwpmGetAppIdFromFileName0(binding.ExecutablePath, out IntPtr appIdBlob);
        if (!WfpNativeMethods.Succeeded(hr))
        {
            Console.WriteLine($"[WfpFilterManager] FwpmGetAppIdFromFileName0 failed for '{binding.ExecutablePath}': {WfpNativeMethods.GetErrorString(hr)}");
            return false;
        }

        try
        {
            // Create the filter condition: match on ALE_APP_ID
            // FWP_CONDITION_VALUE0: Type = FWP_BYTE_BLOB_TYPE (6), Value.BlobPtr = appIdBlob
            // Note: FWP_BYTE_BLOB_TYPE == 6, not FWP_DATA_TYPE_BLOB (5)
            var condition = new WfpNativeMethods.FWPM_FILTER_CONDITION0
            {
                ConditionKey = WfpNativeMethods.FWPM_CONDITION_ALE_APP_ID,
                MatchType = WfpNativeMethods.FWP_MATCH_EQUAL,
                ConditionValue = new WfpNativeMethods.FWP_CONDITION_VALUE0
                {
                    Type = WfpNativeMethods.FWP_DATA_TYPE_BLOB,
                    Value = new WfpNativeMethods.FWP_VALUE_UNION
                    {
                        BlobPtr = appIdBlob
                    }
                }
            };

            // Allocate native memory for the condition array
            IntPtr conditionPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WfpNativeMethods.FWPM_FILTER_CONDITION0>());
            try
            {
                Marshal.StructureToPtr(condition, conditionPtr, false);

                // Create the IPv4 filter
                bool v4Success = AddFilterInternal(
                    layerKey: WfpNativeMethods.FWPM_LAYER_ALE_AUTH_CONNECT_V4,
                    binding: binding,
                    conditionPtr: conditionPtr,
                    suffix: "_V4",
                    filterIdOut: out ulong v4FilterId);

                if (v4Success)
                {
                    _filterIdsByBinding[binding.Id] = v4FilterId;
                }

                // Also add IPv6 filter (best effort)
                bool v6Success = AddFilterInternal(
                    layerKey: WfpNativeMethods.FWPM_LAYER_ALE_AUTH_CONNECT_V6,
                    binding: binding,
                    conditionPtr: conditionPtr,
                    suffix: "_V6",
                    filterIdOut: out ulong v6FilterId);

                if (v6Success)
                {
                    _filterV6IdsByBinding[binding.Id] = v6FilterId;
                }

                Console.WriteLine($"[WfpFilterManager] Added filter for '{binding.ProcessName}' -> {binding.InterfaceName} " +
                                  $"(V4={v4Success}, V6={v6Success})");
                return v4Success; // At least IPv4 must succeed
            }
            finally
            {
                Marshal.FreeHGlobal(conditionPtr);
            }
        }
        finally
        {
            // Free the app ID blob
            WfpNativeMethods.FwpmFreeMemory0(ref appIdBlob);
        }
    }

    private bool AddFilterInternal(
        Guid layerKey,
        BindingMapping binding,
        IntPtr conditionPtr,
        string suffix,
        out ulong filterIdOut)
    {
        filterIdOut = 0;

        var filter = new WfpNativeMethods.FWPM_FILTER0
        {
            FilterKey = Guid.NewGuid(),
            DisplayData = new WfpNativeMethods.FWPM_DISPLAY_DATA0
            {
                Name = $"NetBinder: {binding.ProcessName} -> {binding.InterfaceName}{suffix}",
                Description = $"Permit {binding.ExecutablePath} on {binding.InterfaceName} (Interface Index {binding.InterfaceIndex})"
            },
            Flags = 0,
            ProviderKey = IntPtr.Zero,
            ProviderData = IntPtr.Zero,
            LayerKey = layerKey,
            SubLayerKey = WfpNativeMethods.NetBinderSubLayerKey,
            Weight = IntPtr.Zero,           // NULL = engine assigns default weight
            NumFilterConditions = 1,
            FilterConditions = conditionPtr,
            Action = new WfpNativeMethods.FWPM_ACTION0
            {
                Type = WfpNativeMethods.FWP_ACTION_PERMIT,
                FilterTypeKey = Guid.Empty
            },
            ProviderContextKey = IntPtr.Zero,
            Reserved = IntPtr.Zero
        };

        uint hr = WfpNativeMethods.FwpmFilterAdd0(_engineHandle, ref filter, IntPtr.Zero, out filterIdOut);
        if (!WfpNativeMethods.Succeeded(hr))
        {
            Console.WriteLine($"[WfpFilterManager] FwpmFilterAdd0{suffix} failed: {WfpNativeMethods.GetErrorString(hr)}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Removes a WFP filter for a specific binding.
    /// </summary>
    public bool RemoveBindingFilter(Guid bindingId)
    {
        if (!_isInitialized) return false;

        bool success = true;

        // Remove IPv4 filter
        if (_filterIdsByBinding.TryGetValue(bindingId, out ulong v4FilterId))
        {
            uint hr = WfpNativeMethods.FwpmFilterDeleteById0(_engineHandle, v4FilterId);
            if (WfpNativeMethods.Succeeded(hr))
            {
                _filterIdsByBinding.Remove(bindingId);
                Console.WriteLine($"[WfpFilterManager] Removed V4 filter {v4FilterId}");
            }
            else
            {
                Console.WriteLine($"[WfpFilterManager] Failed to remove V4 filter: {WfpNativeMethods.GetErrorString(hr)}");
                success = false;
            }
        }

        // Remove IPv6 filter
        if (_filterV6IdsByBinding.TryGetValue(bindingId, out ulong v6FilterId))
        {
            uint hr = WfpNativeMethods.FwpmFilterDeleteById0(_engineHandle, v6FilterId);
            if (WfpNativeMethods.Succeeded(hr))
            {
                _filterV6IdsByBinding.Remove(bindingId);
                Console.WriteLine($"[WfpFilterManager] Removed V6 filter {v6FilterId}");
            }
            else
            {
                Console.WriteLine($"[WfpFilterManager] Failed to remove V6 filter: {WfpNativeMethods.GetErrorString(hr)}");
                success = false;
            }
        }

        return success;
    }

    /// <summary>
    /// Shuts down the WFP filter engine. Removes all filters and the sublayer, then closes the session.
    /// Called when the service stops.
    /// </summary>
    public void Shutdown()
    {
        if (!_isInitialized || _engineHandle == IntPtr.Zero) return;

        try
        {
            // Remove all filters
            foreach (var filterId in _filterIdsByBinding.Values)
            {
                WfpNativeMethods.FwpmFilterDeleteById0(_engineHandle, filterId);
            }
            _filterIdsByBinding.Clear();

            foreach (var filterId in _filterV6IdsByBinding.Values)
            {
                WfpNativeMethods.FwpmFilterDeleteById0(_engineHandle, filterId);
            }
            _filterV6IdsByBinding.Clear();

            // Remove our sublayer
            var subLayerKey = WfpNativeMethods.NetBinderSubLayerKey;
            WfpNativeMethods.FwpmSubLayerDeleteByKey0(_engineHandle, ref subLayerKey);

            // Close the engine
            WfpNativeMethods.FwpmEngineClose0(_engineHandle);
            _engineHandle = IntPtr.Zero;
            _isInitialized = false;

            Console.WriteLine("[WfpFilterManager] WFP engine shut down. All filters and sublayer removed.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WfpFilterManager] Error during shutdown: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Shutdown();
        }
    }
}
