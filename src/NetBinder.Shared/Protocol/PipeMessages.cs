namespace NetBinder.Shared.Protocol;

/// <summary>
/// Named Pipe protocol messages between the UI and Service.
/// The pipe name is: "NetBinderService".
/// Messages are JSON-encoded, length-prefixed (4-byte little-endian length + JSON payload).
/// </summary>

// === Request messages (UI -> Service) ===

public abstract class PipeRequest
{
    public string Type { get; set; } = string.Empty;
}

/// <summary>Request the list of network adapters.</summary>
public class GetAdaptersRequest : PipeRequest
{
    public GetAdaptersRequest() => Type = "GetAdapters";
}

/// <summary>Request the list of processes with active network connections.</summary>
public class GetProcessesRequest : PipeRequest
{
    public GetProcessesRequest() => Type = "GetProcesses";
}

/// <summary>Request to add a new binding mapping.</summary>
public class AddBindingRequest : PipeRequest
{
    public AddBindingRequest() => Type = "AddBinding";
    public string ExecutablePath { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public int InterfaceIndex { get; set; }
    public string InterfaceName { get; set; } = string.Empty;
    public NetBinder.Shared.Models.RoutingMethod RoutingMethod { get; set; } = NetBinder.Shared.Models.RoutingMethod.Transparent;
}

/// <summary>Request to remove a binding mapping.</summary>
public class RemoveBindingRequest : PipeRequest
{
    public RemoveBindingRequest() => Type = "RemoveBinding";
    public Guid BindingId { get; set; }
}

/// <summary>Request to update a binding mapping (e.g., change interface).</summary>
public class UpdateBindingRequest : PipeRequest
{
    public UpdateBindingRequest() => Type = "UpdateBinding";
    public Guid BindingId { get; set; }
    public int NewInterfaceIndex { get; set; }
    public string NewInterfaceName { get; set; } = string.Empty;
}

/// <summary>Request to toggle a binding active/inactive.</summary>
public class ToggleBindingRequest : PipeRequest
{
    public ToggleBindingRequest() => Type = "ToggleBinding";
    public Guid BindingId { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>Request to set the routing method for a binding.</summary>
public class SetRoutingMethodRequest : PipeRequest
{
    public SetRoutingMethodRequest() => Type = "SetRoutingMethod";
    public Guid BindingId { get; set; }
    public NetBinder.Shared.Models.RoutingMethod RoutingMethod { get; set; }
}

/// <summary>Request to get all current binding mappings.</summary>
public class GetBindingsRequest : PipeRequest
{
    public GetBindingsRequest() => Type = "GetBindings";
}

/// <summary>Request to set interface metric override.</summary>
public class SetMetricRequest : PipeRequest
{
    public SetMetricRequest() => Type = "SetMetric";
    public int InterfaceIndex { get; set; }
    public uint Metric { get; set; }
}

/// <summary>Request to reset interface metric to system default.</summary>
public class ResetMetricRequest : PipeRequest
{
    public ResetMetricRequest() => Type = "ResetMetric";
    public int InterfaceIndex { get; set; }
}

/// <summary>Request current config (bindings + metric overrides).</summary>
public class GetConfigRequest : PipeRequest
{
    public GetConfigRequest() => Type = "GetConfig";
}

/// <summary>Request proxy info for all interfaces with running proxies.</summary>
public class GetProxyInfoRequest : PipeRequest
{
    public GetProxyInfoRequest() => Type = "GetProxyInfo";
}

/// <summary>Request to start a SOCKS5 proxy for a specific interface.</summary>
public class StartProxyRequest : PipeRequest
{
    public StartProxyRequest() => Type = "StartProxy";
    public int InterfaceIndex { get; set; }
    public string InterfaceName { get; set; } = string.Empty;
}

/// <summary>Request to stop a SOCKS5 proxy for a specific interface.</summary>
public class StopProxyRequest : PipeRequest
{
    public StopProxyRequest() => Type = "StopProxy";
    public int InterfaceIndex { get; set; }
}

// === Response messages (Service -> UI) ===

public abstract class PipeResponse
{
    public string Type { get; set; } = string.Empty;
    public bool Success { get; set; } = true;
    public string? Error { get; set; }
}

public class GetAdaptersResponse : PipeResponse
{
    public GetAdaptersResponse() => Type = "GetAdapters";
    public List<NetBinder.Shared.Models.NetworkAdapterInfo> Adapters { get; set; } = [];
}

public class GetProcessesResponse : PipeResponse
{
    public GetProcessesResponse() => Type = "GetProcesses";
    public List<NetBinder.Shared.Models.NetworkProcessInfo> Processes { get; set; } = [];
}

public class GetBindingsResponse : PipeResponse
{
    public GetBindingsResponse() => Type = "GetBindings";
    public List<NetBinder.Shared.Models.BindingMapping> Bindings { get; set; } = [];
}

public class GetConfigResponse : PipeResponse
{
    public GetConfigResponse() => Type = "GetConfig";
    public NetBinder.Shared.Models.NetBinderConfig Config { get; set; } = new();
}

public class GenericResponse : PipeResponse
{
    public GenericResponse() => Type = "Generic";
}

public class GetProxyInfoResponse : PipeResponse
{
    public GetProxyInfoResponse() => Type = "GetProxyInfo";
    public List<NetBinder.Shared.Models.ProxyInfo> Proxies { get; set; } = [];
}
