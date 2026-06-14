using NetBinder.Service;
using NetBinder.Service.NativeInterop;
using NetBinder.Service.Services;

// Capture any hard crashes that escape normal logging (e.g., AccessViolationException).
AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
{
    var ex = e.ExceptionObject as Exception;
    var msg = ex?.ToString() ?? e.ExceptionObject?.ToString() ?? "Unknown fatal error";
    Console.Error.WriteLine("[FATAL] Unhandled exception:");
    Console.Error.WriteLine(msg);
    try
    {
        var crashDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetBinder");
        Directory.CreateDirectory(crashDir);
        File.AppendAllText(
            Path.Combine(crashDir, "crash.log"),
            $"[{DateTime.Now:O}] FATAL UNHANDLED EXCEPTION:\n{msg}\n\n");
    }
    catch { }
};

TaskScheduler.UnobservedTaskException += (sender, e) =>
{
    Console.Error.WriteLine("[FATAL] Unobserved task exception:");
    Console.Error.WriteLine(e.Exception);
    e.SetObserved();
};

var builder = Host.CreateApplicationBuilder(args);

// Register singletons
builder.Services.AddSingleton<ConfigManager>();
builder.Services.AddSingleton<WfpFilterManager>();
builder.Services.AddSingleton<Socks5ProxyManager>();
builder.Services.AddSingleton<RedirectorService>();
builder.Services.AddSingleton<TransparentProxy>(sp =>
{
    var redirector = sp.GetRequiredService<RedirectorService>();
    return new TransparentProxy(redirector.GetNATMapping);
});

// Register the main worker service
builder.Services.AddHostedService<Worker>();

// Configure as Windows Service if needed
// builder.Services.AddWindowsService(options => { options.ServiceName = "NetBinder Service"; });

var host = builder.Build();
host.Run();