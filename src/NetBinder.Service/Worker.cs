using NetBinder.Service.NativeInterop;
using NetBinder.Service.Services;
using NetBinder.Shared.Models;

namespace NetBinder.Service;

/// <summary>
/// Main background service for NetBinder. Runs the Named Pipe server
/// that communicates with the UI, manages WFP filters, and runs SOCKS5 proxies.
/// This service must run with administrator privileges.
/// </summary>
public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly ConfigManager _configManager;
    private readonly WfpFilterManager _wfpManager;
    private readonly Socks5ProxyManager _proxyManager;
    private readonly RedirectorService _redirector;
    private readonly TransparentProxy _transparentProxy;
    private PipeServer? _pipeServer;

    public Worker(ILogger<Worker> logger, ConfigManager configManager, WfpFilterManager wfpManager, Socks5ProxyManager proxyManager, RedirectorService redirector, TransparentProxy transparentProxy)
    {
        _logger = logger;
        _configManager = configManager;
        _wfpManager = wfpManager;
        _proxyManager = proxyManager;
        _redirector = redirector;
        _transparentProxy = transparentProxy;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("NetBinder Service starting... (ExecuteAsync entered)");

        try
        {
            // Initialize WFP filter engine (requires admin)
            _logger.LogInformation("Initializing WFP...");
            bool wfpOk = _wfpManager.Initialize();
            if (wfpOk)
            {
                _logger.LogInformation("WFP engine initialized successfully.");
            }
            else
            {
                _logger.LogWarning("WFP engine initialization failed. Per-app filtering will not work. " +
                                   "SOCKS5 proxy may still provide limited functionality.");
            }

            // Initialize RedirectorService bindings from active config
            _logger.LogInformation("Initializing WinDivert Redirector bindings...");
            _redirector.UpdateBindings(_configManager.Config.Bindings);

            // Start Transparent Proxy
            _logger.LogInformation("Starting Transparent TCP Relay Proxy...");
            if (_transparentProxy.Start())
            {
                _logger.LogInformation("Transparent TCP Relay Proxy started on port {Port}.", _transparentProxy.ListenPort);
                
                // Start WinDivert Redirector
                _logger.LogInformation("Starting WinDivert Redirector...");
                if (_redirector.Start(_transparentProxy.ListenPort))
                {
                    _logger.LogInformation("WinDivert Redirector started successfully.");
                }
                else
                {
                    _logger.LogWarning("Failed to start WinDivert Redirector. Transparent routing will not work.");
                }
            }
            else
            {
                _logger.LogWarning("Failed to start Transparent TCP Relay Proxy. Transparent routing will not work.");
            }

            // Start SOCKS5 proxies for all bound interfaces
            StartProxiesForActiveBindings();

            // Apply saved WFP filters for active bindings
            ApplySavedBindingFilters();

            // Start the Named Pipe server for UI communication
            _pipeServer = new PipeServer(_configManager, _wfpManager, _proxyManager, _redirector);
            _pipeServer.Start();

            _logger.LogInformation("NetBinder Service started successfully. Pipe server listening.");

            // Apply any saved metric overrides from config
            ApplySavedMetricOverrides();

            _logger.LogInformation("Entering keep-alive loop...");
            // Keep the service alive until cancelled
            await Task.Delay(Timeout.Infinite, stoppingToken);
            _logger.LogInformation("Keep-alive loop cancelled.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("NetBinder Service is shutting down (OperationCanceledException)...");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NetBinder Service encountered a fatal error in ExecuteAsync");
        }
        finally
        {
            _logger.LogInformation("ExecuteAsync finally block running...");

            // Cleanup WinDivert and Transparent Proxy
            try
            {
                _redirector.Stop();
                _logger.LogInformation("WinDivert Redirector stopped.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping WinDivert Redirector");
            }

            try
            {
                _transparentProxy.Stop();
                _logger.LogInformation("Transparent TCP Relay Proxy stopped.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping Transparent TCP Relay Proxy");
            }

            // Cleanup: stop proxies, remove WFP filters, shut down engine
            try
            {
                _proxyManager.StopAll();
                _logger.LogInformation("All SOCKS5 proxies stopped.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping SOCKS5 proxies");
            }

            try
            {
                _wfpManager.Shutdown();
                _logger.LogInformation("WFP engine shut down successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error shutting down WFP engine");
            }

            _pipeServer?.Stop();
            _pipeServer?.Dispose();

            _logger.LogInformation("NetBinder Service stopped. (ExecuteAsync finally complete)");
        }
        _logger.LogInformation("ExecuteAsync returned.");
    }

    /// <summary>
    /// Starts SOCKS5 proxy instances for all interfaces that have active bindings.
    /// </summary>
    private void StartProxiesForActiveBindings()
    {
        var interfaceIndices = _configManager.Config.Bindings
            .Where(b => b.IsActive)
            .Select(b => b.InterfaceIndex)
            .Distinct();

        foreach (var idx in interfaceIndices)
        {
            var binding = _configManager.Config.Bindings.First(b => b.InterfaceIndex == idx);
            var proxy = _proxyManager.StartProxy(idx, binding.InterfaceName);
            if (proxy != null)
            {
                // Update bindings with proxy info
                foreach (var b in _configManager.Config.Bindings.Where(b => b.InterfaceIndex == idx))
                {
                    b.ProxyAddress = proxy.ProxyAddress;
                    b.ProxyPort = proxy.ListenPort;
                }
                _logger.LogInformation("SOCKS5 proxy started on {Address} for interface '{Name}'",
                    proxy.ProxyAddress, binding.InterfaceName);
            }
            else
            {
                _logger.LogWarning("Failed to start SOCKS5 proxy for interface index {Index}", idx);
            }
        }
        _configManager.Save();
    }

    /// <summary>
    /// Re-applies WFP filters for all active bindings from the persisted config.
    /// </summary>
    private void ApplySavedBindingFilters()
    {
        if (!_wfpManager.IsInitialized) return;

        foreach (var binding in _configManager.Config.Bindings.Where(b => b.IsActive && b.RoutingMethod == RoutingMethod.Transparent))
        {
            try
            {
                bool ok = _wfpManager.AddBindingFilter(binding);
                if (ok)
                    _logger.LogInformation("Restored WFP filter for {Process} -> {Interface}", binding.ProcessName, binding.InterfaceName);
                else
                    _logger.LogWarning("Failed to restore WFP filter for {Process}", binding.ProcessName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error restoring WFP filter for {Process}", binding.ProcessName);
            }
        }
    }

    /// <summary>
    /// Applies metric overrides that were saved from a previous session.
    /// </summary>
    private void ApplySavedMetricOverrides()
    {
        foreach (var metricOverride in _configManager.Config.MetricOverrides.Where(m => !m.IsApplied))
        {
            try
            {
                bool success = IphlpApiWrapper.SetInterfaceMetric(
                    metricOverride.InterfaceIndex, metricOverride.CustomMetric);
                if (success)
                {
                    metricOverride.IsApplied = true;
                    _logger.LogInformation("Applied metric override for {Interface}: {Metric}",
                        metricOverride.InterfaceName, metricOverride.CustomMetric);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to apply metric override for interface {Index}",
                    metricOverride.InterfaceIndex);
            }
        }
        _configManager.Save();
    }
}